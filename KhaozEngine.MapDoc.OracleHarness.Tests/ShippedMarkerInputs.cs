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
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record MarkerSourcePoint(string Name, int X, int Z, int Plane, IReadOnlyList<string> Tags);
internal sealed record MarkerRoleRule(string SourceName, string Id, string Role, string Projection,
    double OffsetX, double OffsetZ, string SupportName, MapMarkerHeightMode HeightMode, string HeightRule,
    double Yaw, bool Enabled, string? Archetype);
internal sealed record MarkerCorrespondence(string SourceName, int X, int Z, int Plane, int RegionX, int RegionZ,
    string SupportName, string? SpaceId, IReadOnlyList<string> Parents)
{
    internal string? SceneSpaceId { get; init; }
    internal IReadOnlyList<string> SceneParents { get; init; } = [];
}
internal sealed record MarkerSupportRule(string Name, MapSupportBinding Binding, string SourceKind, int Plane, long? ObjectId);

/// <summary>Closed, snapshotted comparison input. No loader searches outside the attested extraction.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class ShippedMarkerInputs
{
    internal TileWorldDocument World { get; }
    internal TileWorldCatalogs Catalogs { get; }
    internal IReadOnlyList<MarkerSourcePoint> SourceMarkers { get; }
    internal IReadOnlyDictionary<string, MarkerRoleRule> Roles { get; }
    internal IReadOnlyDictionary<string, MarkerCorrespondence> Correspondences { get; }
    internal IReadOnlyDictionary<string, MarkerSupportRule> Supports { get; }
    internal JsonElement Expectations { get; }
    internal string ReportDirectory { get; }
    readonly Snapshot _source;
    readonly byte[] _native;

    ShippedMarkerInputs(TileWorldDocument world, TileWorldCatalogs catalogs, IReadOnlyList<MarkerSourcePoint> markers,
        Dictionary<string, MarkerRoleRule> roles, Dictionary<string, MarkerCorrespondence> correspondences,
        Dictionary<string, MarkerSupportRule> supports, JsonElement expectations, Snapshot source, byte[] native, string reportDirectory)
    {
        World = world; Catalogs = catalogs; SourceMarkers = markers; Roles = roles; Correspondences = correspondences;
        Supports = supports; Expectations = expectations; _source = source; _native = native; ReportDirectory = reportDirectory;
    }

    internal static ShippedMarkerInputs Read(PrivateOracleInputs inputs) => Read(inputs, inputs.Provenance.Comparison);

    internal static ShippedMarkerInputs Read(PrivateOracleInputs inputs, JsonElement comparison, bool scene = false)
    {
        UniqueMembers(comparison);
        Shape(comparison, "schemaVersion", "nativeDocumentPath", "roleManifestPath", "markerManifestPath", "expectations");
        Schema(comparison);
        var expectations = comparison.GetProperty("expectations");
        Shape(expectations, "total", "playerStart", "spawn", "landmark", "label", "negativeX", "supportBound", "enabled");
        foreach (var property in expectations.EnumerateObject()) Require(property.Value.GetInt32() >= 0, "negative marker expectation");
        var source = new Snapshot(inputs);
        string worldPath = inputs.Provenance.Paths.Single(p => Path.GetFileName(p.Path) == TileWorldFile.ManifestFileName).Path;
        var sourceMarkers = ReadSourceInventory(source, worldPath);
        var rolesJson = source.Json(Text(comparison, "roleManifestPath"));
        Shape(rolesJson, "schemaVersion", "generator", "sourcePrefabs", "buildingManifestPath", "supports", "markers");
        Schema(rolesJson);
        var generator = rolesJson.GetProperty("generator");
        Shape(generator, "path", "helpers");
        string generatorPath = Text(generator, "path");
        Require(generatorPath == "generator/build-scaffold.py", "missing retained generator");
        source.Bytes(generatorPath);
        foreach (string helper in Strings(generator, "helpers", true))
        { Require(helper != generatorPath, "duplicate generator input"); source.Bytes(helper); }
        foreach (string prefab in Strings(rolesJson, "sourcePrefabs", true))
        {
            source.Json(prefab);
            TilePrefabFile.Load(source.FullPath(prefab));
        }
        var (world, catalogs) = ShippedSourceInventory.ReadWorld(inputs);
        Require(world.TileSize == 1, "marker source terrain conversion requires metre tiles");
        // The authoritative server ground and walk surfaces describe plane 0 only, so no other plane has a server height.
        Require(sourceMarkers.All(m => m.Plane == 0), "server marker height covers plane 0 only");
        Require(SameSource(sourceMarkers, world.AllMarkers().Select(m => new MarkerSourcePoint(m.Name, m.X, m.Z, m.Plane, (m.Tags ?? []).AsReadOnly())).ToArray()),
            "loaded marker inventory differs from captured regions");
        var supports = ReadSupports(rolesJson, world, catalogs);
        var roles = ReadRoles(rolesJson);
        var markerJson = source.Json(Text(comparison, "markerManifestPath"));
        Shape(markerJson, "schemaVersion", "markers");
        Schema(markerJson);
        var correspondences = ReadCorrespondences(markerJson, scene);
        string[] names = sourceMarkers.Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        Require(names.SequenceEqual(roles.Keys.Order(StringComparer.Ordinal)) && names.SequenceEqual(correspondences.Keys.Order(StringComparer.Ordinal)),
            "incomplete or extra marker correspondence");
        Require(roles.Values.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == roles.Count, "duplicate destination marker");
        Require(supports.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(roles.Values.Select(r => r.SupportName)), "missing or unused named support");
        foreach (var marker in sourceMarkers)
        {
            var row = correspondences[marker.Name];
            var role = roles[marker.Name];
            Require(row.X == marker.X && row.Z == marker.Z && row.Plane == marker.Plane &&
                row.RegionX == RegionCoord.Of(marker.X, marker.Z).Rx && row.RegionZ == RegionCoord.Of(marker.X, marker.Z).Rz &&
                row.SupportName == role.SupportName && supports[row.SupportName].Plane == marker.Plane, "mismatched source marker correspondence");
        }
        byte[] nativeBytes = source.Bytes(Text(comparison, "nativeDocumentPath")).ToArray();
        var nativeJson = source.Json(Text(comparison, "nativeDocumentPath"));
        // Accepted R6 scaffolds declare format 8 and load through the pure migration. Never regenerate them for a number.
        Require(nativeJson.GetProperty("formatVersion").GetInt32() is 8 or MapDocumentFile.CurrentFormatVersion,
            "marker scaffold must declare format eight or the current format");
        var native = MapDocumentFile.LoadText(Encoding.UTF8.GetString(nativeBytes));
        Require(native.ResolverIdentity?.ResolverVersion == 2 && native.SupportRecipe == MapSupportRecipe.AuthoredBindingsV2 &&
            native.Tiles is null && native.Spawns.Count == 0 && native.PlayerSpawns.Count == 0, "marker scaffold requires monolithic adopted resolver two markers");
        MapAssetClosure.Load(native.NativeAssets, source);
        if (NullableText(rolesJson, "buildingManifestPath") is { } buildingPath)
            ShippedBuildingInputs.Read(inputs, source.Json(buildingPath));
        // Released world, catalog and prefab readers use files. Refuse loader-time changes before retaining their result.
        Require(inputs.Provenance.Verify(inputs.SourceRoot) == 0, "marker extraction changed during load");
        return new(world, catalogs, sourceMarkers, roles, correspondences, supports, expectations.Clone(), source, nativeBytes,
            Path.GetDirectoryName(inputs.ReportPath)!);
    }

    internal MapDocument ReloadDocument() => MapDocumentFile.LoadText(Encoding.UTF8.GetString(_native));
    internal MapAssetClosure ReloadAssets() => MapAssetClosure.Load(ReloadDocument().NativeAssets, _source);

    // Explicit heights exist in invented diagnostic fixtures only. Actual imports always name their source support.
    internal void RequireBoundImports()
    {
        Require(Roles.Values.All(r => r.HeightMode == MapMarkerHeightMode.Support), "private imports must be support bound");
        var resolved = MapNativeResolution.Resolve(ReloadDocument(), ReloadAssets(), new("private-marker-inputs", 1, "marker-inputs/1", 2));
        var markers = MapEffectiveAnnotations.Markers(resolved.EffectiveResources!);
        Require(Roles.Values.Select(r => r.Id).ToHashSet(StringComparer.Ordinal).SetEquals(markers.Select(m => m.Id)) &&
            markers.All(m => m.PayloadVersion == 2 && m.HeightProvenance?.Mode == MapMarkerHeightMode.Support), "private imports must be support bound");
    }

    static IReadOnlyList<MarkerSourcePoint> ReadSourceInventory(Snapshot source, string worldPath)
    {
        var world = source.Json(worldPath);
        string directory = Path.GetDirectoryName(worldPath)!.Replace('\\', '/');
        int planes = world.GetProperty("planeCount").GetInt32();
        Require(planes > 0, "missing source planes");
        foreach (var catalog in Array(world, "catalogPaths")) source.Json(source.RelativeTo(directory, catalog.GetString()!));
        var regions = new HashSet<RegionCoord>();
        var markers = new List<MarkerSourcePoint>();
        foreach (var region in Array(world, "regions"))
        {
            Shape(region, "rx", "rz", "hash");
            var coord = new RegionCoord(region.GetProperty("rx").GetInt32(), region.GetProperty("rz").GetInt32());
            Require(regions.Add(coord), "duplicate source region");
            string path = source.RelativeTo(directory, "regions/" + TileWorldFile.RegionFileName(coord));
            Require(Hash(source.Bytes(path)) == Text(region, "hash"), "source region hash differs from manifest");
            var body = source.Json(path);
            Require(body.GetProperty("rx").GetInt32() == coord.Rx && body.GetProperty("rz").GetInt32() == coord.Rz, "source region identity mismatch");
            foreach (var row in Array(body, "markers"))
            {
                var marker = SourcePoint(row);
                Require(marker.Plane >= 0 && marker.Plane < planes && RegionCoord.Of(marker.X, marker.Z) == coord, "source marker has wrong region or plane");
                markers.Add(marker);
            }
        }
        var index = Array(world, "markers").Select(SourcePoint).ToArray();
        Require(markers.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() == markers.Count &&
            index.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() == index.Length, "duplicate source marker");
        Require(SameSource(markers, index), "source marker index differs from region records");
        return markers.AsReadOnly();
    }

    static MarkerSourcePoint SourcePoint(JsonElement row)
    {
        string[] fields = row.TryGetProperty("tags", out var tags) ? ["name", "x", "z", "plane", "tags"] : ["name", "x", "z", "plane"];
        Shape(row, fields);
        var ordered = tags.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? [] : tags.EnumerateArray().Select(t => t.GetString() ?? throw new InvalidDataException("null source tag")).ToArray();
        return new(Text(row, "name"), row.GetProperty("x").GetInt32(), row.GetProperty("z").GetInt32(), row.GetProperty("plane").GetInt32(), System.Array.AsReadOnly(ordered));
    }

    static bool SameSource(IReadOnlyList<MarkerSourcePoint> first, IReadOnlyList<MarkerSourcePoint> second)
    {
        if (first.Count != second.Count) return false;
        var a = first.OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
        var b = second.OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
        return a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.X == p.Second.X && p.First.Z == p.Second.Z &&
            p.First.Plane == p.Second.Plane && p.First.Tags.SequenceEqual(p.Second.Tags));
    }

    static Dictionary<string, MarkerSupportRule> ReadSupports(JsonElement roles, TileWorldDocument world, TileWorldCatalogs catalogs)
    {
        var result = new Dictionary<string, MarkerSupportRule>(StringComparer.Ordinal);
        foreach (var row in Array(roles, "supports"))
        {
            Shape(row, "name", "binding", "source");
            var source = row.GetProperty("source");
            Shape(source, "kind", "plane", "objectId");
            string kind = Text(source, "kind");
            int plane = source.GetProperty("plane").GetInt32();
            long? objectId = source.GetProperty("objectId").ValueKind == JsonValueKind.Null ? null : source.GetProperty("objectId").GetInt64();
            Require(plane >= 0 && plane < world.PlaneCount && kind is "terrain" or "object", "unknown source support rule");
            if (kind == "terrain") Require(objectId is null, "terrain support cannot name an object");
            else
            {
                var owners = world.AllObjects().Where(o => o.Id == objectId).ToArray();
                Require(objectId > 0 && owners.Length == 1, "unknown source support owner");
                var owner = owners[0];
                Require(owner.Plane == plane && catalogs.Archetype(owner.ArchetypeId)?.WalkSurfaces is { Count: > 0 }, "source owner has no standing surface");
            }
            string name = Text(row, "name");
            Require(result.TryAdd(name, new(name, Binding(row.GetProperty("binding")), kind, plane, objectId)), "duplicate named support");
        }
        return result;
    }

    static Dictionary<string, MarkerRoleRule> ReadRoles(JsonElement roles)
    {
        var result = new Dictionary<string, MarkerRoleRule>(StringComparer.Ordinal);
        foreach (var row in Array(roles, "markers"))
        {
            Shape(row, "sourceName", "id", "role", "projection", "offsetX", "offsetZ", "supportName", "heightMode", "heightRule", "yaw", "enabled", "archetype");
            string projection = Text(row, "projection"), rule = Text(row, "heightRule"), mode = Text(row, "heightMode"), name = Text(row, "sourceName");
            Require(projection is "player-start" or "spawn" or "landmark" or "label", "unknown marker projection");
            Require(rule is "terrain" or "standing" && mode is "Explicit" or "Support", "unknown marker height rule");
            Require(result.TryAdd(name, new(name, Text(row, "id"), Text(row, "role"), projection,
                Number(row, "offsetX"), Number(row, "offsetZ"), Text(row, "supportName"), mode == "Explicit" ? MapMarkerHeightMode.Explicit : MapMarkerHeightMode.Support,
                rule, Number(row, "yaw"), row.GetProperty("enabled").GetBoolean(), NullableText(row, "archetype"))), "duplicate source role");
        }
        return result;
    }

    static Dictionary<string, MarkerCorrespondence> ReadCorrespondences(JsonElement manifest, bool scene)
    {
        var result = new Dictionary<string, MarkerCorrespondence>(StringComparer.Ordinal);
        foreach (var row in Array(manifest, "markers"))
        {
            string[] fields = ["sourceName", "x", "z", "plane", "regionX", "regionZ", "supportName", "spaceId", "parents"];
            Shape(row, scene ? fields.Concat(["sceneSpaceId", "sceneParents"]).ToArray() : fields);
            string name = Text(row, "sourceName");
            Require(result.TryAdd(name, new(name, row.GetProperty("x").GetInt32(), row.GetProperty("z").GetInt32(), row.GetProperty("plane").GetInt32(),
                row.GetProperty("regionX").GetInt32(), row.GetProperty("regionZ").GetInt32(), Text(row, "supportName"), NullableText(row, "spaceId"),
                System.Array.AsReadOnly(Strings(row, "parents", true)))
            {
                SceneSpaceId = scene ? NullableText(row, "sceneSpaceId") : null,
                SceneParents = scene ? System.Array.AsReadOnly(Strings(row, "sceneParents", true)) : [],
            }), "duplicate marker correspondence");
        }
        return result;
    }

    static MapSupportBinding Binding(JsonElement value)
    {
        string kind = Text(value, "kind");
        if (kind == "Surface")
        { Shape(value, "kind", "surfaceId"); return new(MapSupportBindingKind.Surface, Text(value, "surfaceId"), null, null, null, null); }
        Require(kind == "Space", "unknown marker support kind");
        Shape(value, "kind", "spaceId", "referenceY", "searchBelow", "searchAbove");
        float reference = Single(value, "referenceY"), below = Single(value, "searchBelow"), above = Single(value, "searchAbove");
        Require(below >= 0 && above >= 0 && below <= 1000000 && above <= 1000000, "invalid marker support search");
        return new(MapSupportBindingKind.Space, null, Text(value, "spaceId"), reference, below, above);
    }

    static void Schema(JsonElement value) => Require(value.GetProperty("schemaVersion").GetInt32() == 1, "unsupported marker comparison schema");
    internal static void Require(bool ok, string reason) { if (!ok) throw new InvalidDataException(reason); }
    static JsonElement[] Array(JsonElement value, string name) => value.GetProperty(name).EnumerateArray().ToArray();
    static string Text(JsonElement value, string name)
    { string? text = value.GetProperty(name).GetString(); Require(!string.IsNullOrWhiteSpace(text), "missing comparison text"); return text!; }
    static string? NullableText(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name);
    static double Number(JsonElement value, string name)
    { double number = value.GetProperty(name).GetDouble(); Require(double.IsFinite(number), "nonfinite marker value"); return number; }
    static float Single(JsonElement value, string name)
    { float number = (float)Number(value, name); Require(float.IsFinite(number), "marker value exceeds float range"); return number; }
    static string[] Strings(JsonElement value, string name, bool unique)
    {
        string[] strings = Array(value, name).Select(v => v.GetString() ?? throw new InvalidDataException("null comparison reference")).ToArray();
        Require(strings.All(s => !string.IsNullOrWhiteSpace(s)) && (!unique || strings.Distinct(StringComparer.Ordinal).Count() == strings.Length), "invalid comparison references");
        return strings;
    }
    static void Shape(JsonElement value, params string[] fields) => Require(value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal)), "incomplete comparison object");
    static void UniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { Require(names.Add(property.Name), "duplicate or case-variant JSON member"); UniqueMembers(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) UniqueMembers(item);
    }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    sealed class Snapshot : IMapAssetSource
    {
        readonly string _root;
        readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        internal Snapshot(PrivateOracleInputs inputs)
        {
            _root = inputs.SourceRoot;
            Require(inputs.Provenance.Verify(_root) == 0, "marker extraction changed before capture");
            foreach (var path in inputs.Provenance.Paths)
            {
                byte[] bytes = File.ReadAllBytes(FullPath(path.Path));
                Require(Hash(bytes) == path.Sha256, "stale attested marker dependency");
                _bytes.Add(path.Path, bytes);
            }
        }
        internal string FullPath(string path)
        {
            SafePath(path);
            string current = _root;
            foreach (string part in path.Split('/'))
            {
                current = Path.Combine(current, part);
                Require(new FileInfo(current).LinkTarget is null && new DirectoryInfo(current).LinkTarget is null, "linked marker dependency");
            }
            return current;
        }
        internal string RelativeTo(string directory, string path)
        {
            Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl), "unsafe dependency path");
            string full = Path.GetFullPath(Path.Combine(_root, directory, path));
            Require(full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "dependency escapes extraction");
            return Path.GetRelativePath(_root, full).Replace('\\', '/');
        }
        internal byte[] Bytes(string path)
        {
            SafePath(path);
            return _bytes.TryGetValue(path, out var bytes) ? bytes : throw new InvalidDataException("unattested marker dependency");
        }
        internal JsonElement Json(string path)
        {
            using var json = JsonDocument.Parse(Bytes(path));
            UniqueMembers(json.RootElement);
            return json.RootElement.Clone();
        }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference)
        {
            byte[] bytes = Bytes(reference.Path);
            // Resource paths need not have an extension. Check every JSON resource without treating binary resources
            // as JSON. A typed resource loader still owns refusal of malformed bytes in its own format.
            int first = 0;
            while (first < bytes.Length && bytes[first] is 9 or 10 or 13 or 32) first++;
            if (first < bytes.Length && bytes[first] is (byte)'{' or (byte)'[')
            {
                try { Json(reference.Path); }
                catch (JsonException) when (!reference.Path.EndsWith(".json", StringComparison.Ordinal)) { }
            }
            return bytes;
        }
        static void SafePath(string path) => Require(!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
            !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) && path.Split('/').All(p => p.Length != 0 && p is not "." and not ".."), "unsafe attested marker path");
    }
}
