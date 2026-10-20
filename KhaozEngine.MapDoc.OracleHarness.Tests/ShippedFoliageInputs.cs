using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>One source object as the captured region bytes record it.</summary>
internal sealed record FoliageSourceObject(long Id, string ArchetypeId, int X, int Z, int Plane, int Rotation, IReadOnlyList<string> Tags);

/// <summary>One verified layer correspondence. Models map source archetypes to native assets in authored order and
/// materials map source ids to native resources.</summary>
internal sealed record FoliageLayerBinding(TileFoliageLayer Source, FoliageLayerRow Row, IReadOnlyDictionary<string, string> Models,
    IReadOnlyDictionary<ushort, string> Materials);

/// <summary>A clearing effect the source applies, recomputed per owner and layer from the original object.</summary>
internal sealed record FoliageOwnerEffect(long SourceObjectId, string NativeLayerId, MapFoliageClearanceKind Kind);

/// <summary>Closed, snapshotted private foliage input. Every byte the comparison reads is attested, captured once and
/// rechecked around the released file loaders. Native heads reload from the captured bytes only.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ShippedFoliageInputs
{
    internal TileWorldDocument World { get; }
    internal TileWorldCatalogs Catalogs { get; }
    internal FoliageComparisonPaths Paths { get; }
    internal FoliageManifest Manifest { get; }
    internal IReadOnlyList<FoliageLayerBinding> Layers { get; }
    internal IReadOnlyList<FoliageOwnerEffect> OwnerEffects { get; }
    internal int SourceOwnerCount { get; }
    internal string ReportDirectory { get; }
    readonly Snapshot _source;
    readonly byte[] _native;
    readonly string _tiledDirectory;

    ShippedFoliageInputs(TileWorldDocument world, TileWorldCatalogs catalogs, FoliageComparisonPaths paths, FoliageManifest manifest,
        IReadOnlyList<FoliageLayerBinding> layers, IReadOnlyList<FoliageOwnerEffect> effects, int owners, Snapshot source, byte[] native,
        string tiledDirectory, string reportDirectory)
    {
        World = world; Catalogs = catalogs; Paths = paths; Manifest = manifest; Layers = layers; OwnerEffects = effects;
        SourceOwnerCount = owners; _source = source; _native = native; _tiledDirectory = tiledDirectory; ReportDirectory = reportDirectory;
    }

    internal static ShippedFoliageInputs Read(PrivateOracleInputs inputs)
    {
        FoliageComparisonPaths paths = Comparison(inputs.Provenance.Comparison);
        var source = new Snapshot(inputs);
        string[] worlds = inputs.Provenance.Paths.Select(p => p.Path).Where(p => System.IO.Path.GetFileName(p) == TileWorldFile.ManifestFileName).ToArray();
        Require(worlds.Length == 1, "foliage extraction needs exactly one source world");
        IReadOnlyList<FoliageSourceObject> captured = ReadSourceObjects(source, worlds[0]);
        FoliageManifest manifest = ShippedFoliageManifest.Read(source.Json(paths.FoliageManifestPath));
        source.Bytes(manifest.GeneratorPath);
        foreach (string helper in manifest.Helpers) source.Bytes(helper);
        var (world, catalogs) = ShippedSourceInventory.ReadWorld(inputs);
        Require(world.TileSize == 1, "foliage source terrain conversion requires metre tiles");
        Require(SameObjects(captured, world.AllObjects().Select(o => new FoliageSourceObject(o.Id, o.ArchetypeId, o.X, o.Z, o.Plane, o.Rotation,
            (o.Tags ?? []).AsReadOnly())).ToArray()), "loaded object inventory differs from captured regions");

        byte[] nativeBytes = source.Bytes(paths.NativeDocumentPath).ToArray();
        Require(FormatVersion(source.Json(paths.NativeDocumentPath)) == MapDocumentFile.CurrentFormatVersion, "foliage scaffold must declare the current format");
        MapDocument native = MapDocumentFile.LoadText(Encoding.UTF8.GetString(nativeBytes));
        Require(native.ResolverIdentity?.ResolverVersion == 2 && native.Tiles is null && native.Foliage is { PayloadVersion: 1 } &&
            native.NativeAssets.Count != 0, "foliage scaffold requires a monolithic resolver two document with foliage");
        MapAssetClosure assets = MapAssetClosure.Load(native.NativeAssets, source);
        Require(System.IO.Path.GetFileName(paths.NativeTiledManifestPath) == "map.json", "unattested tiled foliage store");
        string tiledDirectory = System.IO.Path.GetDirectoryName(paths.NativeTiledManifestPath)!.Replace('\\', '/');
        Require(tiledDirectory.Length != 0, "unattested tiled foliage store");
        Require(FormatVersion(source.Json(paths.NativeTiledManifestPath)) == MapDocumentFile.CurrentFormatVersion, "foliage scaffold must declare the current format");
        string scratch = System.IO.Path.GetDirectoryName(inputs.ReportPath)!;
        MapDocument tiled = LoadTiled(source, tiledDirectory, scratch);
        Require(tiled.Foliage is { PayloadVersion: 1 } && tiled.Foliage.Layers.Select(l => l.Id).SequenceEqual(native.Foliage!.Layers.Select(l => l.Id)),
            "tiled foliage store differs from the monolithic scaffold");
        if (paths.BuildingManifestPath is { } building) ShippedBuildingInputs.Read(inputs, source.Json(building));

        var check = new ShippedFoliageCorrespondence(world, catalogs, manifest, native, assets);
        IReadOnlyList<FoliageLayerBinding> layers = check.Layers();
        // Owner seats and poses are effective facts, so owners check against the scaffold's own captured context.
        MapFoliageContextResult capture = MapFoliageGenerationContext.Capture(native, assets);
        Require(capture.Status == MapFoliageStatus.Complete && capture.Context is not null, "foliage scaffold capture refused");
        (IReadOnlyList<FoliageOwnerEffect> effects, int owners) = check.Owners(capture.Context!);
        // The released world, catalog and closure readers use files. Refuse loader-time changes before keeping any result.
        Require(inputs.Provenance.Verify(inputs.SourceRoot) == 0, "foliage extraction changed during load");
        return new(world, catalogs, paths, manifest, layers, effects, owners, source, nativeBytes, tiledDirectory, scratch);
    }

    /// <summary>A fresh monolithic head from the captured scaffold bytes.</summary>
    internal MapDocument ReloadDocument() => MapDocumentFile.LoadText(Encoding.UTF8.GetString(_native));

    /// <summary>A fresh tiled head, loaded from its own copy of the captured store bytes.</summary>
    internal MapDocument ReloadTiled() => LoadTiled(_source, _tiledDirectory, ReportDirectory);

    /// <summary>A fresh closure from the captured resource bytes.</summary>
    internal MapAssetClosure ReloadAssets(MapDocument document) => MapAssetClosure.Load(document.NativeAssets, _source);

    // The released tiled reader opens a directory. Each head gets its own private copy of exactly the attested store
    // bytes under the secure report parent, verified as a whole store, and the copy is removed before the head is used.
    static MapDocument LoadTiled(Snapshot source, string directory, string scratch)
    {
        string root = System.IO.Path.Combine(scratch, "foliage-head-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            string store = System.IO.Path.Combine(root, "store");
            foreach (string path in source.Under(directory))
            {
                string target = System.IO.Path.Combine(store, path[(directory.Length + 1)..]);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, source.Bytes(path));
            }
            Require(MapDocumentFile.VerifyTiled(store).Count == 0, "native tiled store does not verify");
            return MapDocumentFile.LoadTiled(store);
        }
        finally { Directory.Delete(root, true); }
    }

    static int FormatVersion(JsonElement document)
    {
        Require(document.ValueKind == JsonValueKind.Object && document.TryGetProperty("formatVersion", out JsonElement version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out _), "foliage scaffold must declare the current format");
        return document.GetProperty("formatVersion").GetInt32();
    }

    // Source objects straight from the captured region bytes, each region checked against the world manifest's hash.
    static IReadOnlyList<FoliageSourceObject> ReadSourceObjects(Snapshot source, string worldPath)
    {
        JsonElement world = source.Json(worldPath);
        string directory = System.IO.Path.GetDirectoryName(worldPath)!.Replace('\\', '/');
        Require(world.ValueKind == JsonValueKind.Object && world.TryGetProperty("catalogPaths", out JsonElement catalogs) &&
            catalogs.ValueKind == JsonValueKind.Array && world.TryGetProperty("regions", out JsonElement found) &&
            found.ValueKind == JsonValueKind.Array, "incomplete source world");
        JsonElement regions = world.GetProperty("regions");
        catalogs = world.GetProperty("catalogPaths");
        foreach (JsonElement catalog in catalogs.EnumerateArray())
            source.Json(source.RelativeTo(directory, catalog.GetString() ?? throw new InvalidDataException("incomplete source world")));
        var seen = new HashSet<RegionCoord>();
        var objects = new List<FoliageSourceObject>();
        foreach (JsonElement region in regions.EnumerateArray())
        {
            Shape(region, "rx", "rz", "hash");
            var coord = new RegionCoord(region.GetProperty("rx").GetInt32(), region.GetProperty("rz").GetInt32());
            Require(seen.Add(coord), "duplicate source region");
            string path = source.RelativeTo(directory, "regions/" + TileWorldFile.RegionFileName(coord));
            Require(Hash(source.Bytes(path)) == Text(region, "hash"), "source region hash differs from manifest");
            JsonElement body = source.Json(path);
            Require(body.GetProperty("rx").GetInt32() == coord.Rx && body.GetProperty("rz").GetInt32() == coord.Rz, "source region identity mismatch");
            if (!body.TryGetProperty("objects", out JsonElement rows) || rows.ValueKind == JsonValueKind.Null) continue;
            foreach (JsonElement row in rows.EnumerateArray())
            {
                string[] tags = row.TryGetProperty("tags", out JsonElement t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(v => v.GetString() ?? throw new InvalidDataException("null source tag")).ToArray() : [];
                var item = new FoliageSourceObject(row.GetProperty("id").GetInt64(), row.GetProperty("archetypeId").GetString() ?? "",
                    row.GetProperty("x").GetInt32(), row.GetProperty("z").GetInt32(), row.GetProperty("plane").GetInt32(),
                    row.TryGetProperty("rotation", out JsonElement r) ? r.GetInt32() : 0, tags.AsReadOnly());
                Require(RegionCoord.Of(item.X, item.Z) == coord, "source object has the wrong region");
                objects.Add(item);
            }
        }
        Require(objects.Select(o => o.Id).Distinct().Count() == objects.Count, "duplicate source object");
        return objects.AsReadOnly();
    }

    static bool SameObjects(IReadOnlyList<FoliageSourceObject> first, IReadOnlyList<FoliageSourceObject> second)
    {
        if (first.Count != second.Count) return false;
        var a = first.OrderBy(o => o.Id).ToArray();
        var b = second.OrderBy(o => o.Id).ToArray();
        return a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.ArchetypeId == p.Second.ArchetypeId && p.First.X == p.Second.X &&
            p.First.Z == p.Second.Z && p.First.Plane == p.Second.Plane && p.First.Rotation == p.Second.Rotation &&
            p.First.Tags.SequenceEqual(p.Second.Tags, StringComparer.Ordinal));
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Every attested byte, captured once after the outer verification and checked against its attested hash.
    /// Reads never touch the extraction again.</summary>
    internal sealed class Snapshot : IMapAssetSource
    {
        readonly string _root;
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);

        internal Snapshot(PrivateOracleInputs inputs)
        {
            _root = inputs.SourceRoot;
            Require(inputs.Provenance.Verify(_root) == 0, "foliage extraction changed before capture");
            foreach (ShippedSourcePath path in inputs.Provenance.Paths)
            {
                byte[] bytes = File.ReadAllBytes(FullPath(path.Path));
                Require(Hash(bytes) == path.Sha256, "stale attested foliage dependency");
                _bytes.Add(path.Path, bytes);
            }
        }

        string FullPath(string path)
        {
            SafePath(path);
            string current = _root;
            foreach (string part in path.Split('/'))
            {
                current = System.IO.Path.Combine(current, part);
                Require(new FileInfo(current).LinkTarget is null && new DirectoryInfo(current).LinkTarget is null, "linked foliage dependency");
            }
            return current;
        }

        internal string RelativeTo(string directory, string path)
        {
            Require(!string.IsNullOrWhiteSpace(path) && !System.IO.Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') &&
                !path.Any(char.IsControl), "unsafe dependency path");
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, directory, path));
            Require(full.StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal), "dependency escapes extraction");
            return System.IO.Path.GetRelativePath(_root, full).Replace('\\', '/');
        }

        internal byte[] Bytes(string path) =>
            _bytes.TryGetValue(SafePath(path), out byte[]? bytes) ? bytes : throw new InvalidDataException("unattested foliage dependency");

        internal IEnumerable<string> Under(string directory)
        {
            string[] paths = _bytes.Keys.Where(p => p.StartsWith(directory + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
            Require(paths.Length != 0, "unattested tiled foliage store");
            return paths;
        }

        internal JsonElement Json(string path)
        {
            using JsonDocument json = JsonDocument.Parse(Bytes(path));
            UniqueMembers(json.RootElement);
            return json.RootElement.Clone();
        }

        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => Bytes(reference.Path);
    }
}

/// <summary>Verifies every correspondence row against the original source and the native scaffold, independently of
/// any generated sample. Seated ordinals come from each original object's plane, never from the native table, and
/// per-owner applicability is recomputed from the source rules: Solid and Door on the layer's own plane, a roof only
/// from a strictly higher plane.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ShippedFoliageCorrespondence(TileWorldDocument world, TileWorldCatalogs catalogs, FoliageManifest manifest,
    MapDocument native, MapAssetClosure assets)
{
    static readonly MapFoliageClearanceKind[] Kinds = [MapFoliageClearanceKind.Solid, MapFoliageClearanceKind.RoofAbove, MapFoliageClearanceKind.Door];
    readonly IReadOnlyList<MapFoliageLayer> _native = native.Foliage!.Layers;

    internal IReadOnlyList<FoliageLayerBinding> Layers()
    {
        IReadOnlyList<TileFoliageLayer> sources = world.FoliageLayers;
        Require(manifest.Layers.Count == sources.Count && manifest.Layers.Count == _native.Count &&
            manifest.Layers.Select(l => l.SourceLayerId).ToHashSet(StringComparer.Ordinal).SetEquals(sources.Select(l => l.Id)) &&
            manifest.Layers.Select(l => l.NativeLayerId).ToHashSet(StringComparer.Ordinal).SetEquals(_native.Select(l => l.Id)),
            "incomplete foliage layer correspondence");
        var result = new List<FoliageLayerBinding>();
        foreach (FoliageLayerRow row in manifest.Layers)
        {
            TileFoliageLayer source = sources.Single(l => l.Id == row.SourceLayerId);
            MapFoliageLayer layer = _native.Single(l => l.Id == row.NativeLayerId);
            Require(layer.Support.Kind == MapSupportBindingKind.Surface && layer.Support.SurfaceId == row.SupportSurfaceId,
                "private foliage imports must bind a Surface");
            Require(row.CompatibilitySupports.All(s => s.SourcePlane < world.PlaneCount), "unknown source plane");
            Require(row.CompatibilitySupports.Any(s => s.SourcePlane == source.Plane && s.NativeSurfaceId == row.SupportSurfaceId),
                "foliage layer support differs from its source plane");
            Require(layer.CompatibilityOrdinals.Count == row.CompatibilitySupports.Count && layer.CompatibilityOrdinals.All(t =>
                row.CompatibilitySupports.Any(s => s.NativeSurfaceId == t.SurfaceId && s.SourcePlane == t.Ordinal)),
                "compatibility table differs from source planes");
            foreach (FoliageSupportRow support in row.CompatibilitySupports) RequireDerived(support);
            Require(row.Models.Select(m => m.SourceArchetypeId).SequenceEqual(source.Archetypes.Select(a => a.Id), StringComparer.Ordinal) &&
                row.Models.Select(m => m.NativeAssetId).SequenceEqual(layer.Models.Select(m => m.AssetId), StringComparer.Ordinal),
                "foliage model correspondence differs");
            foreach (FoliageModelRow model in row.Models) Closure(() => assets.GetAsset(model.NativeAssetId));
            RequireMaterials(row, source, layer);
            Require(Same(row.RuleIds, layer.ClearanceRules.Select(r => r.Id)), "unknown or missing foliage clearance rule");
            Require(Same(row.MaskIds, layer.ExceptionMasks.Select(m => m.Id)), "unknown or missing foliage exception mask");
            result.Add(new(source, row, row.Models.ToDictionary(m => m.SourceArchetypeId, m => m.NativeAssetId, StringComparer.Ordinal),
                row.Materials.ToDictionary(m => m.SourceMaterialId, m => m.NativeResourceId)));
        }
        return result.AsReadOnly();
    }

    internal (IReadOnlyList<FoliageOwnerEffect> Effects, int Owners) Owners(MapFoliageGenerationContext context)
    {
        // Every source object the released sampler can clear foliage around: solid collision, a roof or a door tag.
        var sourceOwners = world.AllObjects().Where(o => catalogs.Archetype(o.ArchetypeId) is { } a && (Solid(a) || a.IsRoof || Door(o, a)))
            .ToDictionary(o => o.Id);
        Require(manifest.Owners.Count == sourceOwners.Count && manifest.Owners.All(o => sourceOwners.ContainsKey(o.SourceObjectId)),
            "incomplete foliage owner correspondence");
        var placements = context.Resources.Placements.ToDictionary(p => p.PlacementId, StringComparer.Ordinal);
        var seats = context.Resources.PlacementSupports.ToDictionary(s => s.PlacementId, s => s.Support, StringComparer.Ordinal);
        var effects = new List<FoliageOwnerEffect>();
        var listedPlacements = new HashSet<string>(StringComparer.Ordinal);
        foreach (FoliageOwnerRow row in manifest.Owners)
        {
            TileObject obj = sourceOwners[row.SourceObjectId];
            TileObjectArchetype archetype = catalogs.Archetype(obj.ArchetypeId)!;
            Require(row.SourcePlane == obj.Plane && row.SourceArchetypeId == obj.ArchetypeId, "foliage owner differs from its source object");
            // World placements carry their effective pose under the resolver's kind and id key.
            if (row.NativeOwner.Kind != MapPrefabChildKind.Placement || !placements.TryGetValue(row.NativeOwner.Id, out MapResolvedPlacement? placement) ||
                !context.Resources.Transforms.TryGetValue("Placement:" + row.NativeOwner.Id, out MapEffectiveTransform pose))
                throw new InvalidDataException("missing native foliage owner");
            listedPlacements.Add(row.NativeOwner.Id);
            FoliageLayerRow[] listing = manifest.Layers.Where(l => l.OwnerIds.Contains(row.SourceObjectId)).ToArray();
            Require(listing.Length != 0, "incomplete foliage owner correspondence");
            foreach (FoliageLayerRow layer in listing)
                Require(layer.CompatibilitySupports.Any(s => s.SourcePlane == obj.Plane && s.NativeSurfaceId == row.SeatedSurfaceId),
                    "seated support differs from the source plane");
            if (seats.TryGetValue(row.NativeOwner.Id, out var seat))
                Require(Seated(seat) == row.SeatedSurfaceId && _native.All(l => l.OwnerSupports.All(o => o.Owner != row.NativeOwner)),
                    "seated support differs from the source plane");
            else
                foreach (MapFoliageLayer layer in _native)
                {
                    MapFoliageOwnerSupport[] rows = layer.OwnerSupports.Where(o => o.Owner == row.NativeOwner).ToArray();
                    bool listed = listing.Any(l => l.NativeLayerId == layer.Id);
                    Require(rows.Length == (listed ? 1 : 0) && rows.All(o => o.Support.Kind == MapSupportBindingKind.Surface &&
                        o.Support.SurfaceId == row.SeatedSurfaceId), "seated support differs from the source plane");
                }

            TileRect footprint = TileFootprint.Of(archetype, obj.X, obj.Z, obj.Rotation);
            var matchedRules = new HashSet<string>(StringComparer.Ordinal);
            var matchedMasks = new HashSet<string>(StringComparer.Ordinal);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (MapFoliageLayer layer in _native)
            {
                FoliageLayerRow layerRow = manifest.Layers.Single(l => l.NativeLayerId == layer.Id);
                TileFoliageLayer sourceLayer = world.FoliageLayers.Single(l => l.Id == layerRow.SourceLayerId);
                MapFoliageClearanceRule[] rules = layer.ClearanceRules.Where(r => Matches(r, placement)).ToArray();
                MapFoliageMask[] masks = layer.ExceptionMasks.Where(m => m.Owner == row.NativeOwner).ToArray();
                Require(listing.Any(l => l.NativeLayerId == layer.Id) == (rules.Length + masks.Length != 0), "foliage layer owner references differ");
                foreach (MapFoliageClearanceRule rule in rules) RequireRule(rule.Kind, rule.Footprint, pose, footprint, obj, archetype, matchedRules, rule.Id);
                foreach (MapFoliageMask mask in masks)
                {
                    Require(mask.Support.Kind == MapSupportBindingKind.Surface, "private foliage imports must bind a Surface");
                    RequireRule(mask.Kind, mask.Footprint, pose, footprint, obj, archetype, matchedMasks, mask.Id);
                }
                int? seated = Ordinal(layer, row.SeatedSurfaceId), sample = Ordinal(layer, layer.Support.SurfaceId!);
                foreach (MapFoliageClearanceKind kind in Kinds)
                {
                    bool expected = SourceEffect(kind, sourceLayer, obj, archetype);
                    bool actual = Enabled(layer, kind) && Applies(seated, sample, row.SeatedSurfaceId, layer.Support.SurfaceId!, kind) &&
                        (rules.Any(r => r.Kind == kind) || masks.Any(m => m.Kind == kind && m.Support.SurfaceId == layer.Support.SurfaceId));
                    Require(expected == actual, "foliage owner applicability differs from source");
                    if (!expected) continue;
                    effects.Add(new(row.SourceObjectId, layer.Id, kind));
                    targets.Add(layer.Id);
                }
            }
            Require(Same(row.RuleIds, matchedRules) && Same(row.MaskIds, matchedMasks), "foliage owner rule references differ");
            Require(Same(row.Targets.Select(t => t.NativeLayerId), targets) && row.Targets.All(t =>
                manifest.Layers.Single(l => l.NativeLayerId == t.NativeLayerId).SupportSurfaceId == t.SupportSurfaceId),
                "foliage clearing targets differ from source");
        }
        // No native owner outside the correspondence: every placement a rule matches and every mask owner is listed.
        foreach (MapFoliageLayer layer in _native)
        {
            Require(layer.ExceptionMasks.All(m => m.Owner.Kind == MapPrefabChildKind.Placement &&
                listedPlacements.Contains(m.Owner.Id)), "unlisted native foliage owner");
            Require(context.Resources.Placements.Where(p => layer.ClearanceRules.Any(r => Matches(r, p))).All(p => listedPlacements.Contains(p.PlacementId)),
                "unlisted native foliage owner");
        }
        return (effects.AsReadOnly(), sourceOwners.Count);
    }

    // The released index: solid collision, a roof archetype, and a door tag on the object or its archetype.
    static bool Solid(TileObjectArchetype archetype) => archetype.CollisionKind == TileCollisionKind.Solid;
    static bool Door(TileObject obj, TileObjectArchetype archetype) => Tag(obj.Tags, "door") || Tag(archetype.Tags, "door");
    static bool Tag(IReadOnlyList<string>? tags, string wanted) => tags?.Any(t => string.Equals(t, wanted, StringComparison.OrdinalIgnoreCase)) == true;

    static bool SourceEffect(MapFoliageClearanceKind kind, TileFoliageLayer layer, TileObject obj, TileObjectArchetype archetype) => kind switch
    {
        MapFoliageClearanceKind.Solid => layer.ExcludeSolidObjects && Solid(archetype) && obj.Plane == layer.Plane,
        MapFoliageClearanceKind.RoofAbove => layer.ExcludeIndoors && archetype.IsRoof && obj.Plane > layer.Plane,
        _ => layer.DoorClearance > 0f && Door(obj, archetype) && obj.Plane == layer.Plane,
    };

    static bool Enabled(MapFoliageLayer layer, MapFoliageClearanceKind kind) => kind switch
    {
        MapFoliageClearanceKind.Solid => layer.ExcludeSolidObjects,
        MapFoliageClearanceKind.RoofAbove => layer.ExcludeIndoors,
        _ => layer.DoorClearance > 0f,
    };

    // Compatibility ordinals as the plan rules them: equal for Solid and Door, strictly higher for a roof, and an
    // unmapped seat falls back to canonical identity for Solid and Door and never clears as a roof.
    static bool Applies(int? owner, int? sample, string seat, string support, MapFoliageClearanceKind kind)
    {
        if (owner is null) return kind != MapFoliageClearanceKind.RoofAbove && string.Equals(seat, support, StringComparison.Ordinal);
        if (sample is null) return false;
        return kind == MapFoliageClearanceKind.RoofAbove ? owner > sample : owner == sample;
    }

    static int? Ordinal(MapFoliageLayer layer, string surface) =>
        layer.CompatibilityOrdinals.FirstOrDefault(r => r.SurfaceId == surface)?.Ordinal;

    static bool Matches(MapFoliageClearanceRule rule, MapResolvedPlacement placement) =>
        string.Equals(rule.AssetId, placement.AssetId, StringComparison.Ordinal) &&
        (rule.PlacementTag is not { } tag || placement.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));

    static string? Seated(MapSupportResult seat) => seat.Status switch
    {
        MapSupportStatus.Supported => seat.Face?.OwnerId,
        MapSupportStatus.LegacyFallback => seat.Compatibility?.Patch.SurfaceId,
        _ => null,
    };

    // The generator's self-check, repeated: each rule or mask kind needs the original object's own property, a solid
    // or roof footprint must cover exactly the original tiles, and a door clears around the original footprint centre.
    static void RequireRule(MapFoliageClearanceKind kind, MapFoliageRect local, MapEffectiveTransform pose, TileRect footprint,
        TileObject obj, TileObjectArchetype archetype, HashSet<string> ids, string id)
    {
        Require(kind switch
        {
            MapFoliageClearanceKind.Solid => Solid(archetype),
            MapFoliageClearanceKind.RoofAbove => archetype.IsRoof,
            _ => Door(obj, archetype),
        }, "clearance rule disagrees with the source collision, roof or door");
        var kernel = new MapResourceTransform(pose);
        (double X, double Z) Point(float x, float z)
        {
            MapExactPoint p = kernel.Point(new(MapExactValue.FromSingle(x), default, MapExactValue.FromSingle(z)));
            return (p.X.ToDouble(), p.Z.ToDouble());
        }
        if (kind == MapFoliageClearanceKind.Door)
        {
            (double x, double z) = Point((local.MinX + local.MaxX) * 0.5f, (local.MinZ + local.MaxZ) * 0.5f);
            float cx = TileWorldSpace.WorldX(footprint.X + footprint.Width * 0.5f, 1f), cz = TileWorldSpace.WorldZ(footprint.Z + footprint.Height * 0.5f, 1f);
            Require(Math.Abs(x - cx) <= 0.00001 && Math.Abs(z - cz) <= 0.00001, "clearance rule disagrees with the source footprint");
        }
        else
        {
            var corners = new[] { Point(local.MinX, local.MinZ), Point(local.MaxX, local.MinZ), Point(local.MaxX, local.MaxZ), Point(local.MinX, local.MaxZ) };
            double minX = TileWorldSpace.WorldX(footprint.X, 1f), maxX = TileWorldSpace.WorldX(footprint.X1, 1f);
            double minZ = TileWorldSpace.WorldZ(footprint.Z1, 1f), maxZ = TileWorldSpace.WorldZ(footprint.Z, 1f);
            Require(Math.Abs(corners.Min(c => c.X) - minX) <= 0.0001 && Math.Abs(corners.Max(c => c.X) - maxX) <= 0.0001 &&
                Math.Abs(corners.Min(c => c.Z) - minZ) <= 0.0001 && Math.Abs(corners.Max(c => c.Z) - maxZ) <= 0.0001,
                "clearance rule disagrees with the source footprint");
        }
        ids.Add(id);
    }

    // A compatibility row's surface must be the conversion of its own source plane: one patch per source region whose
    // plane is authored, on the metre and centimetre lattice, with every corner at the source plane's height.
    void RequireDerived(FoliageSupportRow row)
    {
        var surface = native.Surfaces.Refs.FirstOrDefault(s => s.Id == row.NativeSurfaceId);
        Require(surface is not null && surface.Role == MapSurfaceRole.SupportFloor &&
            surface.Frame.Equals(MapLatticeFrame.ImportedMetreCentimetre), "native support surface does not derive from its source plane");
        var patches = native.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == row.NativeSurfaceId).ToArray();
        var authored = world.Regions.Values.Where(r => !r.Planes[row.SourcePlane].IsEmpty).Select(r => r.Coord).ToHashSet();
        Require(patches.Length != 0 && authored.All(c => patches.Any(p => p.Key.SlotX == c.Rx && p.Key.SlotZ == c.Rz)),
            "native support surface does not derive from its source plane");
        const int side = TileRegion.Size + 1;
        foreach (var patch in patches)
        {
            var coord = new RegionCoord((int)patch.Key.SlotX, (int)patch.Key.SlotZ);
            Require(world.GetRegion(coord) is not null && patch.CellMinX == 0 && patch.CellMinZ == 0 && patch.Width == TileRegion.Size &&
                patch.Depth == TileRegion.Size && patch.Heights.Length == side * side, "native support surface does not derive from its source plane");
            for (int z = 0; z < side; z++)
                for (int x = 0; x < side; x++)
                    Require(patch.Heights[z * side + x] == world.CornerHeightCm(coord.OriginX + x, coord.OriginZ + z, row.SourcePlane),
                        "native support surface does not derive from its source plane");
        }
    }

    void RequireMaterials(FoliageLayerRow row, TileFoliageLayer source, MapFoliageLayer layer)
    {
        Require(layer.Materials.OrderBy(m => m.Id).Select(m => (m.Id, m.ResourceId, m.Kind))
            .SequenceEqual(row.Materials.Select(m => (m.SourceMaterialId, m.NativeResourceId, m.Kind))), "foliage material correspondence differs");
        foreach (FoliageMaterialRow material in row.Materials)
        {
            Require(catalogs.Material(material.SourceMaterialId) is { } m && (m.Kind == GroundMaterialKind.Water) == (material.Kind == MapFoliageMaterialKind.Water),
                "foliage material kind differs from the source catalog");
            Require(Closure(() => assets.GetResource(material.NativeResourceId)).Kind == MapResourceKind.Material, "unattested foliage material");
        }
        var visible = new HashSet<ushort>();
        foreach (TileRegion region in world.Regions.Values)
        {
            TilePlaneData plane = region.Planes[source.Plane];
            foreach (ushort id in (plane.Underlay ?? []).Concat(plane.Overlay ?? [])) if (id != 0) visible.Add(id);
        }
        Require(visible.All(id => row.Materials.Any(m => m.SourceMaterialId == id)), "incomplete foliage material correspondence");
    }

    static T Closure<T>(Func<T> read)
    {
        try { return read(); }
        catch (MapDocumentException) { throw new InvalidDataException("unattested foliage closure member"); }
    }

    static bool Same(IEnumerable<string> first, IEnumerable<string> second) =>
        first.ToHashSet(StringComparer.Ordinal).SetEquals(second);
}
