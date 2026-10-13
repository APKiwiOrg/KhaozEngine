using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Test-only, closed extraction. Source semantics are attested correspondences, never inferred groups.</summary>
internal sealed class ShippedBuildingInputs
{
    internal TileWorldDocument World { get; }
    internal TileWorldCatalogs Catalogs { get; }
    internal IReadOnlyDictionary<string, TilePrefab> Prefabs { get; }
    internal IReadOnlyDictionary<string, byte[]> MeshBytes { get; }
    internal JsonElement Comparison { get; }
    internal MapDocument NativeDocument { get; }
    internal MapAssetClosure Assets { get; }
    internal TileColliderOptions CollisionOptions { get; }
    readonly AttestedSource _source;

    ShippedBuildingInputs(TileWorldDocument world, TileWorldCatalogs catalogs, Dictionary<string, TilePrefab> prefabs,
        JsonElement comparison, MapDocument native, AttestedSource source, MapAssetClosure assets)
    {
        World = world; Catalogs = catalogs; Prefabs = prefabs; Comparison = comparison;
        NativeDocument = native; _source = source; Assets = assets;
        MeshBytes = Array(comparison, "leaves").Select(l => Text(l, "meshPath")).Distinct(StringComparer.Ordinal)
            .ToDictionary(p => p, p => source.Bytes(p).ToArray(), StringComparer.Ordinal);
        var options = comparison.GetProperty("sourceCollisionOptions");
        CollisionOptions = new()
        {
            WallThickness = Number(options, "wallThickness"),
            BlockedHeight = options.GetProperty("blockedHeight").ValueKind == JsonValueKind.Null ? null : Number(options, "blockedHeight"),
            WalkSurfaceThickness = Number(options, "walkSurfaceThickness")
        };
    }

    internal static ShippedBuildingInputs Read(PrivateOracleInputs inputs)
    {
        JsonElement comparison = inputs.Provenance.Comparison.Clone();
        RejectDuplicateMembers(comparison);
        Shape(comparison, "schemaVersion", "nativeDocumentPath", "sourceCollisionOptions", "expectations", "definitions",
            "selectedLeafIds", "buildings", "leaves", "floors", "rooms", "groups", "roofs", "lights", "doorwayRecords", "doorways", "probes");
        Require(comparison.GetProperty("schemaVersion").GetInt32() == 1, "unsupported building comparison schema");
        var source = new AttestedSource(inputs);
        string worldPath = inputs.Provenance.Paths.Single(p => Path.GetFileName(p.Path) == TileWorldFile.ManifestFileName).Path;
        using (var json = JsonDocument.Parse(source.Bytes(worldPath)))
        {
            string directory = Path.GetDirectoryName(worldPath)!.Replace('\\', '/');
            foreach (var region in Array(json.RootElement, "regions"))
                source.Bytes(directory + "/regions/r_" + region.GetProperty("rx").GetInt32() + "_" + region.GetProperty("rz").GetInt32() + ".json");
            foreach (var catalog in Array(json.RootElement, "catalogPaths"))
                source.Bytes(source.RelativeTo(directory, catalog.GetString()!));
        }
        var (world, catalogs) = ShippedSourceInventory.ReadWorld(inputs);
        Require(world.TileSize == 1, "R2 building floor conversion requires metre tiles");
        var prefabs = new Dictionary<string, TilePrefab>(StringComparer.Ordinal);
        foreach (var definition in Array(comparison, "definitions"))
        {
            Shape(definition, "id", "path", "resourceId", "keys");
            string path = Text(definition, "path");
            source.Bytes(path);
            prefabs.Add(Text(definition, "id"), TilePrefabFile.Load(source.FullPath(path)));
        }
        string nativePath = Text(comparison, "nativeDocumentPath");
        using (var json = JsonDocument.Parse(source.Bytes(nativePath)))
            Require(json.RootElement.GetProperty("formatVersion").GetInt32() == 7, "native scaffold must declare format seven");
        MapDocument native = MapDocumentFile.LoadText(Encoding.UTF8.GetString(source.Bytes(nativePath)));
        Require(native.FormatVersion == 7 && native.NativeAssets.Count != 0 && native.Prefabs is not null,
            "building comparison requires format seven native roots and instances");
        var assets = MapAssetClosure.Load(native.NativeAssets, source);
        var result = new ShippedBuildingInputs(world, catalogs, prefabs, comparison, native, source, assets);
        result.Validate();
        // The released source readers use files. Refuse changes during those reads, and retain native closure bytes.
        Require(inputs.Provenance.Verify(inputs.SourceRoot) == 0, "building extraction changed during load");
        return result;
    }

    internal byte[] Bytes(string path) => _source.Bytes(path).ToArray();
    internal MapAssetClosure ReloadAssets() => MapAssetClosure.Load(NativeDocument.NativeAssets, _source);

    void Validate()
    {
        JsonElement c = Comparison;
        var expectations = c.GetProperty("expectations");
        Shape(expectations, "definitions", "buildings", "originalLeaves", "connectedInteriors", "roofs", "openings", "doorwayRecords", "dedicatedLights");
        foreach (var value in expectations.EnumerateObject()) Require(value.Value.GetInt32() >= 0, "negative expectation");
        Require(expectations.GetProperty("originalLeaves").GetInt32() > 0, "empty building inventory");
        var collision = c.GetProperty("sourceCollisionOptions");
        Shape(collision, "wallThickness", "blockedHeight", "walkSurfaceThickness");
        Require(CollisionOptions.WallThickness > 0 && CollisionOptions.WalkSurfaceThickness > 0 &&
            (CollisionOptions.BlockedHeight is null or > 0), "invalid pinned collision options");
        var objects = World.AllObjects().ToDictionary(o => o.Id);
        var selected = Ids(c, "selectedLeafIds");
        Require(selected.Length > 0 && selected.All(objects.ContainsKey), "unknown selected source leaf");
        var leaves = Array(c, "leaves");
        var byLeaf = leaves.ToDictionary(l => l.GetProperty("sourceId").GetInt64());
        Require(selected.Order().SequenceEqual(byLeaf.Keys.Order()), "unassigned selected leaf");
        Require(leaves.Select(l => Text(l, "worldId")).Distinct(StringComparer.Ordinal).Count() == leaves.Length,
            "duplicate native leaf identity");
        var buildings = Array(c, "buildings").ToDictionary(b => Text(b, "id"), StringComparer.Ordinal);
        var definitions = Array(c, "definitions").ToDictionary(d => Text(d, "id"), StringComparer.Ordinal);
        var instances = NativeDocument.Prefabs!.Instances.ToDictionary(i => i.Id, StringComparer.Ordinal);
        Require(buildings.Keys.Order().SequenceEqual(instances.Keys.Order()), "instance inventory mismatch");
        foreach (var definition in definitions.Values)
        {
            var prefab = Prefabs[Text(definition, "id")];
            string resourceId = Text(definition, "resourceId");
            var nativePrefab = Assets.GetPrefab(resourceId);
            // Use the released codec, including digest-addressed resource IDs. Never derive or rewrite an ID.
            MapPrefabFile.Read(Assets.GetResource(resourceId).Bytes.ToArray(), resourceId);
            var keys = Array(definition, "keys");
            Require(keys.Length == prefab.Objects.Count && keys.Select(k => Text(k, "key")).Distinct().Count() == keys.Length &&
                keys.Select(k => k.GetProperty("objectIndex").GetInt32()).Order().SequenceEqual(Enumerable.Range(0, prefab.Objects.Count)),
                "incomplete definition keys");
            foreach (var key in keys)
            {
                Shape(key, "key", "objectIndex");
                Require(nativePrefab.Children.OfType<MapPrefabPlacementChild>().Any(p => p.Key.Key == Text(key, "key")), "undefined native definition key");
            }
        }
        foreach (var building in buildings.Values)
        {
            Shape(building, "id", "definitionId", "leafIds", "roomIds", "storeyIds", "overrides");
            string id = Text(building, "id");
            var definition = definitions[Text(building, "definitionId")];
            var instance = instances[id];
            Require(!instance.Removed && !instance.Unpacked && instance.ResourceId == Text(definition, "resourceId"),
                "inactive or mismatched instance");
            var assigned = leaves.Where(l => Text(l, "buildingId") == id).ToArray();
            Require(Ids(building, "leafIds").Order().SequenceEqual(assigned.Select(l => l.GetProperty("sourceId").GetInt64()).Order()),
                "incomplete actual instance membership");
            var overrides = Array(building, "overrides");
            Require(overrides.Length == instance.Overrides.Count, "incomplete actual override inventory");
            foreach (var row in overrides)
            {
                Shape(row, "kind", "key", "operation");
                Require(Enum.TryParse<MapPrefabChildKind>(Text(row, "kind"), out var kind) && Enum.IsDefined(kind), "invalid override kind");
                var actual = instance.Overrides.Single(o => o.Child == new MapPrefabChildKey(kind, Text(row, "key")));
                string operation = actual switch
                {
                    MapPrefabReplaceOverride => "replace",
                    MapPrefabPlacementOverride => "placement",
                    MapPrefabTransformOverride => "transform",
                    MapPrefabAddOverride => "add",
                    MapPrefabRemoveOverride => "remove",
                    _ => "unsupported"
                };
                Require(Text(row, "operation") == operation, "mismatched actual override");
            }
            var bindings = instance.Bindings.Entries.Where(b => b.Child.Kind == MapPrefabChildKind.Placement && !b.Removed).ToArray();
            Require(bindings.Length == assigned.Length, "unassigned bound placement");
            foreach (var leaf in assigned)
                Require(bindings.Any(b => b.Child.Key == Text(leaf, "key") && b.WorldId == Text(leaf, "worldId") &&
                    b.NumericId == leaf.GetProperty("sourceId").GetInt64()), "unbound original numeric leaf");
        }
        foreach (var leaf in leaves)
        {
            Shape(leaf, "sourceId", "buildingId", "key", "worldId", "assetId", "meshPath", "selectionPath", "metadata");
            Require(buildings.ContainsKey(Text(leaf, "buildingId")), "unassigned building leaf");
            var archetype = Catalogs.Archetype(objects[leaf.GetProperty("sourceId").GetInt64()].ArchetypeId)
                ?? throw new InvalidDataException("unknown source archetype");
            Require(Text(leaf, "meshPath") == archetype.MeshRef, "mesh correspondence differs from source catalog");
            _source.Bytes(archetype.MeshRef);
            if (archetype.LodMeshRef is { } lod) _source.Bytes(lod);
            if (leaf.GetProperty("selectionPath").ValueKind != JsonValueKind.Null) _source.Bytes(Text(leaf, "selectionPath"));
            Assets.GetAsset(Text(leaf, "assetId"));
            Shape(leaf.GetProperty("metadata"), "source", "license", "category", "textured", "sourceUnitsToMetres", "renderBounds", "lodBounds", "lightBounds");
            foreach (string field in new[] { "renderBounds", "lodBounds", "lightBounds" })
            {
                var bounds = leaf.GetProperty("metadata").GetProperty(field);
                if (bounds.ValueKind == JsonValueKind.Null) { Require(field != "renderBounds", "missing render bounds"); continue; }
                Shape(bounds, "min", "max"); Vector(bounds.GetProperty("min")); Vector(bounds.GetProperty("max"));
            }
        }
        foreach (var material in Catalogs.Materials.Values)
            if (material.Texture is { Length: > 0 } path) _source.Bytes(path);
        ValidateRooms(buildings);
        ValidateAnnotations(byLeaf, buildings);
    }

    void ValidateRooms(Dictionary<string, JsonElement> buildings)
    {
        var rooms = Array(Comparison, "rooms").ToDictionary(r => Text(r, "id"), StringComparer.Ordinal);
        var resolved = MapNativeResolution.Resolve(NativeDocument, Assets, new("private-building-inputs", 1, "building-inputs/1", 2));
        var effective = resolved.EffectiveResources!;
        var records = effective.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapPrismDomain>().ToArray();
        Require(records.Select(d => d.Space.Id).Order().SequenceEqual(rooms.Keys.Order()), "incomplete room inventory");
        // Prefab-local room inventory is checked again against expanded rooms before building either head.
        foreach (var room in rooms.Values)
        {
            Shape(room, "id", "buildingId", "storeyId", "cells");
            var building = buildings[Text(room, "buildingId")];
            Require(Strings(building, "roomIds").Contains(Text(room, "id")) &&
                Strings(building, "storeyIds").Contains(Text(room, "storeyId")), "unknown room or storey owner");
            var cells = Array(room, "cells");
            Require(cells.Length != 0, "partial room manifest");
            var cellKeys = cells.Select(CellKey).ToArray();
            Require(cellKeys.Distinct().Count() == cellKeys.Length, "duplicate room cell");
            foreach (var cell in cells)
            {
                Shape(cell, "x", "z", "plane", "y");
                Number(cell, "y");
            }
            var domain = records.SingleOrDefault(d => d.Space.Id == Text(room, "id"));
            if (domain is not null)
            {
                Require(domain.InstanceId == Text(room, "buildingId") && domain.StoreyId == Text(room, "storeyId"), "room ownership mismatch");
                var transform = effective.Transforms.GetValueOrDefault("Record:" + domain.Id, new(0, 0, 0, 0, 1));
                var kernel = new MapResourceTransform(transform);
                var corners = domain.Footprint.Rects.SelectMany(r => new[]
                { MapLatticeAddress.Corner(r.MinX, r.MinZ), MapLatticeAddress.Corner(r.MaxX, r.MinZ),
                    MapLatticeAddress.Corner(r.MinX, r.MaxZ), MapLatticeAddress.Corner(r.MaxX, r.MaxZ) })
                    .Concat(domain.Footprint.Rings.SelectMany(r => r.Vertices)).Select(v =>
                    { var xz = domain.Footprint.Frame.WorldXz(v); return kernel.Point(new(xz.X, MapExactValue.FromSingle(0), xz.Z)); }).ToArray();
                Require(corners.Length != 0, "empty room footprint");
                int minX = checked((int)Math.Floor(corners.Min(p => p.X.ToDouble()) / World.TileSize));
                int maxX = checked((int)Math.Ceiling(corners.Max(p => p.X.ToDouble()) / World.TileSize));
                int minZ = checked((int)Math.Floor(-corners.Max(p => p.Z.ToDouble()) / World.TileSize));
                int maxZ = checked((int)Math.Ceiling(-corners.Min(p => p.Z.ToDouble()) / World.TileSize));
                Require((long)(maxX - minX) * (maxZ - minZ) <= 1048576, "room cell closure exceeds input budget");
                var membership = new MapSpaceMembership(effective.CompleteView());
                foreach (var layer in cells.Select(CellKey).GroupBy(k => (k.Plane, k.Y)))
                {
                    var expected = new HashSet<(int X, int Z)>();
                    for (int z = minZ; z < maxZ; z++)
                        for (int x = minX; x < maxX; x++)
                        {
                            var point = new Vector3((x + 0.5f) * World.TileSize, layer.Key.Y, -(z + 0.5f) * World.TileSize);
                            var frame = WorldFrame.Nearest(point);
                            var member = membership.Query(new(frame, point - frame.Anchor));
                            Require(member.Status is MapMembershipStatus.Resolved or MapMembershipStatus.Outside, "incomplete room centre geometry");
                            if (member.SpaceId == domain.Space.Id) expected.Add((x, z));
                        }
                    Require(expected.Count != 0 && expected.SetEquals(layer.Select(k => (k.X, k.Z))), "partial room manifest");
                }
            }
        }
        foreach (var building in buildings.Values)
            Require(Strings(building, "roomIds").Order().SequenceEqual(rooms.Values.Where(r => Text(r, "buildingId") == Text(building, "id"))
                .Select(r => Text(r, "id")).Order()), "incomplete room ownership");
        foreach (var floor in Array(Comparison, "floors"))
        {
            Shape(floor, "buildingId", "x", "z", "plane", "underlay", "overlay", "paintId");
            Require(buildings.ContainsKey(Text(floor, "buildingId")) && floor.GetProperty("plane").GetInt32() >= 0 &&
                floor.GetProperty("plane").GetInt32() < World.PlaneCount, "unassigned floor footprint");
            Require(World.GetRegion(RegionCoord.Of(floor.GetProperty("x").GetInt32(), floor.GetProperty("z").GetInt32())) is not null,
                "unknown floor footprint");
        }
    }

    void ValidateAnnotations(Dictionary<long, JsonElement> leaves, Dictionary<string, JsonElement> buildings)
    {
        var rooms = Array(Comparison, "rooms").Select(r => Text(r, "id")).ToHashSet(StringComparer.Ordinal);
        var groups = Array(Comparison, "groups").ToDictionary(g => Text(g, "id"), StringComparer.Ordinal);
        foreach (var group in groups.Values)
        { Shape(group, "id", "rooms"); Require(Strings(group, "rooms").Length != 0 && Strings(group, "rooms").All(rooms.Contains), "dangling concealment group"); }
        var roofs = Array(Comparison, "roofs");
        var sourceRoofs = leaves.Keys.Where(id => Catalogs.Archetype(World.AllObjects().Single(o => o.Id == id).ArchetypeId)!.IsRoof).Order();
        Require(sourceRoofs.SequenceEqual(roofs.Select(r => r.GetProperty("leafId").GetInt64()).Order()), "incomplete roof inventory");
        foreach (var roof in roofs)
        {
            Shape(roof, "leafId", "linkId", "rooms", "groups", "aboveY");
            Require(Strings(roof, "rooms").All(rooms.Contains) && Strings(roof, "groups").All(groups.ContainsKey), "dangling roof link");
            Number(roof, "aboveY");
            if (roof.GetProperty("linkId").ValueKind == JsonValueKind.Null)
                Require(Strings(roof, "rooms").Length == 0 && Strings(roof, "groups").Length == 0, "unlinked roof has concealment");
        }
        var lights = Array(Comparison, "lights");
        Require(lights.Select(l => Text(l, "id")).Distinct().Count() == lights.Length, "duplicate light inventory");
        foreach (var light in lights)
        {
            Shape(light, "id", "ownerLeafId", "dedicated", "position", "colour", "intensity", "radius", "castShadows");
            Require(leaves.ContainsKey(light.GetProperty("ownerLeafId").GetInt64()), "missing original light leaf owner");
            Vector(light.GetProperty("position")); Vector(light.GetProperty("colour"));
            Require(Number(light, "radius") > 0 && Number(light, "intensity") >= 0, "invalid light correspondence");
        }
        var records = Array(Comparison, "doorwayRecords").ToDictionary(r => Text(r, "id"), StringComparer.Ordinal);
        foreach (var record in records.Values)
        {
            Shape(record, "id", "leafId", "key");
            long id = record.GetProperty("leafId").GetInt64();
            Require(leaves.ContainsKey(id) && Text(leaves[id], "key") == Text(record, "key"), "unbound doorway source record");
        }
        var doorways = Array(Comparison, "doorways");
        Require(doorways.SelectMany(d => Strings(d, "recordIds")).Order().SequenceEqual(records.Keys.Order()), "incomplete doorway record correspondence");
        Require(doorways.Length != 0 && doorways.Select(d => Text(d, "id")).Distinct().Count() == doorways.Length, "missing doorway inventory");
        foreach (var door in doorways)
        {
            Shape(door, "id", "buildingId", "leafIds", "recordIds", "radius", "length", "clear", "jamb");
            Require(buildings.ContainsKey(Text(door, "buildingId")) && Ids(door, "leafIds").Length != 0 &&
                Ids(door, "leafIds").All(id => leaves.ContainsKey(id) && Text(leaves[id], "buildingId") == Text(door, "buildingId")), "dangling doorway correspondence");
            Require(Strings(door, "recordIds").Length != 0 && Strings(door, "recordIds").All(id => records.ContainsKey(id) &&
                Ids(door, "leafIds").Contains(records[id].GetProperty("leafId").GetInt64())) && Number(door, "radius") > 0 && Number(door, "length") > 0, "partial doorway");
            foreach (string name in new[] { "clear", "jamb" })
            { var points = Array(door, name); Require(points.Length >= 2, "partial doorway path"); foreach (var point in points) Vector(point); }
        }
        var probes = Array(Comparison, "probes");
        foreach (string required in new[] { "centre", "edge", "storey" })
            Require(probes.Any(p => Text(p, "kind") == required), "missing membership probe class");
        foreach (var probe in probes)
        {
            Shape(probe, "kind", "position", "sourceSpaceId", "spaceId", "hiddenRoofIds");
            Require(Text(probe, "kind") is "centre" or "edge" or "storey" or "boundary", "unknown membership probe class");
            Vector(probe.GetProperty("position"));
            Require(Ids(probe, "hiddenRoofIds").All(id => roofs.Any(r => r.GetProperty("leafId").GetInt64() == id)), "unknown concealed roof");
        }
    }

    internal static JsonElement[] Array(JsonElement parent, string name) => parent.GetProperty(name).EnumerateArray().ToArray();
    internal static string Text(JsonElement parent, string name)
    {
        string? value = parent.GetProperty(name).GetString();
        Require(!string.IsNullOrWhiteSpace(value), "empty comparison text");
        return value!;
    }
    internal static string[] Strings(JsonElement parent, string name)
    {
        var values = Array(parent, name).Select(v => v.GetString() ?? throw new InvalidDataException("null comparison text")).ToArray();
        Require(values.All(v => !string.IsNullOrWhiteSpace(v)) && values.Distinct(StringComparer.Ordinal).Count() == values.Length, "duplicate comparison reference");
        return values;
    }
    internal static long[] Ids(JsonElement parent, string name)
    {
        var values = Array(parent, name).Select(v => v.GetInt64()).ToArray();
        Require(values.All(v => v > 0) && values.Distinct().Count() == values.Length, "duplicate original identity");
        return values;
    }
    internal static float Number(JsonElement parent, string name)
    { float value = parent.GetProperty(name).GetSingle(); Require(float.IsFinite(value), "nonfinite comparison value"); return value; }
    internal static Vector3 Vector(JsonElement value)
    {
        var parts = value.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Require(parts.Length == 3 && parts.All(float.IsFinite), "invalid comparison vector");
        return new(parts[0], parts[1], parts[2]);
    }
    internal static (int X, int Z, int Plane, float Y) CellKey(JsonElement cell) =>
        (cell.GetProperty("x").GetInt32(), cell.GetProperty("z").GetInt32(), cell.GetProperty("plane").GetInt32(), Number(cell, "y"));
    internal static void Require(bool ok, string reason) { if (!ok) throw new InvalidDataException(reason); }
    static void Shape(JsonElement value, params string[] fields) =>
        Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields.Order()), "incomplete comparison object");
    static void RejectDuplicateMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { Require(seen.Add(property.Name), "duplicate comparison member"); RejectDuplicateMembers(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateMembers(item);
    }

    sealed class AttestedSource : IMapAssetSource
    {
        readonly string _root;
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        internal AttestedSource(PrivateOracleInputs inputs)
        {
            _root = inputs.SourceRoot;
            foreach (var path in inputs.Provenance.Paths)
            {
                string full = FullPath(path.Path);
                byte[] bytes = File.ReadAllBytes(full);
                Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == path.Sha256, "stale attested dependency");
                _bytes.Add(path.Path, bytes);
            }
        }
        internal string RelativeTo(string directory, string path)
        {
            Require(!Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl), "unsafe dependency path");
            string full = Path.GetFullPath(Path.Combine(_root, directory, path));
            Require(full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "dependency escapes extraction");
            return Path.GetRelativePath(_root, full).Replace('\\', '/');
        }
        internal string FullPath(string path)
        {
            Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') &&
                !path.Any(char.IsControl) && path.Split('/').All(p => p.Length != 0 && p is not "." and not ".."), "unsafe attested path");
            string current = _root;
            foreach (string part in path.Split('/'))
            {
                current = Path.Combine(current, part);
                Require(new FileInfo(current).LinkTarget is null && new DirectoryInfo(current).LinkTarget is null, "linked attested dependency");
            }
            return current;
        }
        internal byte[] Bytes(string path)
        {
            FullPath(path);
            return _bytes.TryGetValue(path, out var bytes) ? bytes : throw new InvalidDataException("unattested dependency");
        }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => Bytes(reference.Path);
    }
}
