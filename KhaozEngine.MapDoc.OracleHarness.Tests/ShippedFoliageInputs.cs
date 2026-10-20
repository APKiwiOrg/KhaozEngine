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

    internal static ShippedFoliageInputs Read(PrivateOracleInputs inputs) => Read(inputs, null);

    /// <summary>Reads with an optional test hook that runs after the released file loaders and before the recheck that
    /// refuses any change they could have observed. Harness tests only.</summary>
    internal static ShippedFoliageInputs Read(PrivateOracleInputs inputs, Action? afterLoaders)
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

        var check = new ShippedFoliageCorrespondence(world, catalogs, manifest, native, assets);
        IReadOnlyList<FoliageLayerBinding> layers = check.Layers();
        // Owner seats and poses are effective facts, so owners check against the scaffold's own captured context.
        MapFoliageContextResult capture = MapFoliageGenerationContext.Capture(native, assets);
        Require(capture.Status == MapFoliageStatus.Complete && capture.Context is not null, "foliage scaffold capture refused");
        (IReadOnlyList<FoliageOwnerEffect> effects, int owners) = check.Owners(capture.Context!);
        afterLoaders?.Invoke();
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
