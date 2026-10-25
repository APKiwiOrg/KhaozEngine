using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record SceneGenerator(string Path, IReadOnlyList<string> Helpers);
internal sealed record SceneMaterial(ushort SourceMaterialId, string NativeResourceId);
internal sealed record SceneLod(string SourcePath, string NativeResourceId);
internal sealed record ScenePlacement(long SourceObjectId, string NativePlacementId, IReadOnlyList<SceneLod> Lods);
internal sealed record SceneAssetSelection(string SourceArchetypeId, IReadOnlyList<string> NativeAssetIds);
internal sealed record SceneLight(long SourceObjectId, string SourceProfileId, string NativeLightId);
internal sealed record SceneRoof(long SourceObjectId, string NativePlacementId);
internal sealed record SceneWater(string SourceBodyKey, string NativeBodyId, IReadOnlyList<string> NativeSpaceIds);
internal sealed record SceneFoliage(string SourceLayerId, string NativeLayerId);
internal sealed record SceneWindow(string Id, MapBox3 Bounds, IReadOnlyList<string>? SpaceIds, double MotionPaddingMetres);
internal sealed record SceneStep(string Operation, string WindowId);
internal sealed record SceneProbe(string Id, string WindowId, double ObserverX, double ObserverY, double ObserverZ,
    MapRoofMode RoofMode, double TimeSeconds, int WindSeed, double? FocusX, double? FocusY, double? FocusZ);
internal sealed record ScenePolicyPaths(string SourcePolicyPath, string NativePolicyPath, IReadOnlyList<SceneProbe> Probes);
internal sealed record SceneExpectations(long TerrainFaces, long Placements, long Parts, long Lods, long Lights, long Roofs,
    long WaterRegions, long FoliageSamples, long Windows, long SequenceSteps);

/// <summary>Closed correspondence only. No expected meshes, bounds, matrices or draw lists are accepted here.</summary>
internal sealed record ShippedSceneManifest(SceneGenerator Generator, string? NativeDocumentPath, string NativeTiledManifestPath,
    string? BuildingManifestPath, string? MarkerManifestPath, string? FoliageManifestPath,
    IReadOnlyList<SceneAssetSelection> AssetSelections,
    IReadOnlyList<SceneMaterial> Materials, IReadOnlyList<ScenePlacement> Placements, IReadOnlyList<SceneLight> Lights,
    IReadOnlyList<SceneRoof> Roofs, IReadOnlyList<SceneWater> Water, IReadOnlyList<SceneFoliage> Foliage,
    IReadOnlyList<SceneWindow> Windows, IReadOnlyList<SceneStep> Sequence, ScenePolicyPaths Policy, SceneExpectations Expectations)
{
    internal SceneLimitsProfile? Limits { get; init; }
    internal static ShippedSceneManifest Read(JsonElement json)
    {
        UniqueMembers(json);
        Require(json.ValueKind == JsonValueKind.Object, "invalid scene manifest");
        string[] fields = ["schemaVersion", "generator", "nativeDocumentPath", "nativeTiledManifestPath", "buildingManifestPath",
            "markerManifestPath", "foliageManifestPath", "assetSelections", "materials", "placements", "lights", "roofs", "water", "foliage",
            "windows", "sequence", "policy", "expectations"];
        bool hasLimits = json.TryGetProperty("limits", out var limits);
        Shape(json, hasLimits ? fields.Concat(["limits"]).ToArray() : fields);
        var profile = hasLimits ? SceneLimitsProfile.Read(limits) : null;
        Require(json.GetProperty("schemaVersion").TryGetInt32(out int schema) && schema == 1, "unsupported scene schema");
        var generator = json.GetProperty("generator"); Shape(generator, "path", "helpers");
        string generatorPath = Path(generator, "path");
        string[] helpers = Strings(generator.GetProperty("helpers")).Select(SafePath).ToArray();
        Require(!helpers.Contains(generatorPath, StringComparer.Ordinal), "duplicate scene generator input");
        var assetSelections = Rows(json, "assetSelections", r =>
        {
            Shape(r, "sourceArchetypeId", "nativeAssetIds");
            var ids = Strings(r.GetProperty("nativeAssetIds"));
            Require(ids.Length > 0, "scene selection needs native assets");
            return new SceneAssetSelection(Text(r, "sourceArchetypeId"), ids);
        });
        Unique(assetSelections.Select(r => r.SourceArchetypeId), "scene selection source");
        var materials = Rows(json, "materials", r =>
        {
            Shape(r, "sourceMaterialId", "nativeResourceId");
            return new SceneMaterial((ushort)Integer(r, "sourceMaterialId", 1, ushort.MaxValue), Text(r, "nativeResourceId"));
        });
        Unique(materials.Select(r => r.SourceMaterialId), "scene material source"); Unique(materials.Select(r => r.NativeResourceId), "scene material native");
        var placements = Rows(json, "placements", r =>
        {
            Shape(r, "sourceObjectId", "nativePlacementId", "lods");
            var lods = Rows(r, "lods", l => { Shape(l, "sourcePath", "nativeResourceId"); return new SceneLod(Path(l, "sourcePath"), Text(l, "nativeResourceId")); });
            Require(lods.Count > 0, "scene placement needs base geometry");
            Unique(lods.Select(l => l.NativeResourceId), "scene LOD native");
            return new ScenePlacement(ObjectId(r), Text(r, "nativePlacementId"), lods);
        });
        Unique(placements.Select(r => r.SourceObjectId), "scene placement source"); Unique(placements.Select(r => r.NativePlacementId), "scene placement native");
        var lights = Rows(json, "lights", r =>
        {
            Shape(r, "sourceObjectId", "sourceProfileId", "nativeLightId");
            return new SceneLight(ObjectId(r), Text(r, "sourceProfileId"), Text(r, "nativeLightId"));
        });
        Unique(lights.Select(r => (r.SourceObjectId, r.SourceProfileId)), "scene light source"); Unique(lights.Select(r => r.NativeLightId), "scene light native");
        var roofs = Rows(json, "roofs", r => { Shape(r, "sourceObjectId", "nativePlacementId"); return new SceneRoof(ObjectId(r), Text(r, "nativePlacementId")); });
        Unique(roofs.Select(r => r.SourceObjectId), "scene roof source"); Unique(roofs.Select(r => r.NativePlacementId), "scene roof native");
        Require(roofs.All(r => placements.Any(p => p.SourceObjectId == r.SourceObjectId && p.NativePlacementId == r.NativePlacementId)), "dangling scene roof");
        var water = Rows(json, "water", r =>
        { Shape(r, "sourceBodyKey", "nativeBodyId", "nativeSpaceIds"); return new SceneWater(Text(r, "sourceBodyKey"), Text(r, "nativeBodyId"), Strings(r.GetProperty("nativeSpaceIds"))); });
        Unique(water.Select(r => r.SourceBodyKey), "scene water source"); Unique(water.Select(r => r.NativeBodyId), "scene water native");
        var foliage = Rows(json, "foliage", r =>
        { Shape(r, "sourceLayerId", "nativeLayerId"); return new SceneFoliage(Text(r, "sourceLayerId"), Text(r, "nativeLayerId")); });
        Unique(foliage.Select(r => r.SourceLayerId), "scene foliage source"); Unique(foliage.Select(r => r.NativeLayerId), "scene foliage native");
        var windows = Rows(json, "windows", Window); Unique(windows.Select(w => w.Id), "scene window");
        Require(windows.Count > 0, "missing scene windows");
        var sequence = Rows(json, "sequence", r =>
        {
            Shape(r, "operation", "windowId"); string operation = Text(r, "operation"), window = Text(r, "windowId");
            Require(operation is "Load" or "Unload" or "Recenter", "invalid scene operation");
            Require(windows.Any(w => w.Id == window), "unknown scene sequence window");
            return new SceneStep(operation, window);
        });
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in sequence)
        {
            if (step.Operation == "Load") Require(active.Add(step.WindowId), "scene window already loaded");
            else if (step.Operation == "Unload") Require(active.Remove(step.WindowId), "scene window is not loaded");
            else { Require(active.Count > 0 && !active.Contains(step.WindowId), "invalid scene recenter"); active.Add(step.WindowId); }
        }
        Require(sequence.Count > 0 && active.Count == 0, "scene sequence must finish empty");
        var policy = json.GetProperty("policy"); Shape(policy, "sourcePolicyPath", "nativePolicyPath", "probes");
        var probes = Rows(policy, "probes", r => Probe(r, windows)); Unique(probes.Select(p => p.Id), "scene probe");
        Require(probes.Count > 0, "missing scene policy probes");
        var counts = json.GetProperty("expectations");
        Shape(counts, "terrainFaces", "placements", "parts", "lods", "lights", "roofs", "waterRegions", "foliageSamples", "windows", "sequenceSteps");
        long Count(string name) => Integer(counts, name, 0, long.MaxValue);
        return new(new(generatorPath, helpers), NullablePath(json, "nativeDocumentPath"), Path(json, "nativeTiledManifestPath"),
            NullablePath(json, "buildingManifestPath"), NullablePath(json, "markerManifestPath"), NullablePath(json, "foliageManifestPath"),
            assetSelections, materials, placements, lights, roofs, water, foliage, windows, sequence, new(Path(policy, "sourcePolicyPath"), Path(policy, "nativePolicyPath"), probes),
            new(Count("terrainFaces"), Count("placements"), Count("parts"), Count("lods"), Count("lights"), Count("roofs"), Count("waterRegions"), Count("foliageSamples"), Count("windows"), Count("sequenceSteps")))
        { Limits = profile };
    }

    static SceneWindow Window(JsonElement r)
    {
        Shape(r, "id", "minX", "minY", "minZ", "maxX", "maxY", "maxZ", "spaceIds", "motionPaddingMetres");
        var bounds = new MapBox3(Number(r, "minX"), Number(r, "minY"), Number(r, "minZ"), Number(r, "maxX"), Number(r, "maxY"), Number(r, "maxZ"));
        double padding = Number(r, "motionPaddingMetres");
        Require(bounds.MinX <= bounds.MaxX && bounds.MinY <= bounds.MaxY && bounds.MinZ <= bounds.MaxZ && padding >= 0, "invalid scene window");
        return new(Text(r, "id"), bounds, r.GetProperty("spaceIds").ValueKind == JsonValueKind.Null ? null : Strings(r.GetProperty("spaceIds")), padding);
    }
    static SceneProbe Probe(JsonElement r, IReadOnlyList<SceneWindow> windows)
    {
        Shape(r, "id", "windowId", "observerX", "observerY", "observerZ", "roofMode", "timeSeconds", "windSeed", "focusX", "focusY", "focusZ");
        string window = Text(r, "windowId"), roof = Text(r, "roofMode");
        Require(windows.Any(w => w.Id == window) && roof is "Auto" or "AlwaysVisible" or "AlwaysHidden", "invalid scene probe");
        bool absent = r.GetProperty("focusX").ValueKind == JsonValueKind.Null;
        Require(absent == (r.GetProperty("focusY").ValueKind == JsonValueKind.Null) && absent == (r.GetProperty("focusZ").ValueKind == JsonValueKind.Null), "partial scene focus");
        return new(Text(r, "id"), window, Number(r, "observerX"), Number(r, "observerY"), Number(r, "observerZ"), Enum.Parse<MapRoofMode>(roof),
            Number(r, "timeSeconds"), (int)Integer(r, "windSeed", int.MinValue, int.MaxValue), absent ? null : Number(r, "focusX"), absent ? null : Number(r, "focusY"), absent ? null : Number(r, "focusZ"));
    }
    static IReadOnlyList<T> Rows<T>(JsonElement json, string name, Func<JsonElement, T> read)
    {
        var value = json.GetProperty(name); Require(value.ValueKind == JsonValueKind.Array, "missing scene list");
        return Array.AsReadOnly(value.EnumerateArray().Select(read).ToArray());
    }
    internal static double Number(JsonElement json, string name)
    {
        var v = json.GetProperty(name); Require(v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double number) && double.IsFinite(number), "invalid scene number");
        return v.GetDouble();
    }
    static long Integer(JsonElement json, string name, long min, long max)
    {
        var v = json.GetProperty(name); Require(v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long number) && number >= min && number <= max, "invalid scene integer"); return v.GetInt64();
    }
    static long ObjectId(JsonElement row)
    {
        string text = Text(row, "sourceObjectId");
        Require(text.Length <= 19 && text[0] is >= '1' and <= '9' && text.All(c => c is >= '0' and <= '9') &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0, "invalid scene object identity");
        return long.Parse(text, CultureInfo.InvariantCulture);
    }
    internal static string[] Strings(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Array, "missing scene references");
        var values = value.EnumerateArray().Select(v => { Require(v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()), "invalid scene reference"); return v.GetString()!; }).ToArray();
        Unique(values, "scene reference"); return values;
    }
    static void Unique<T>(IEnumerable<T> keys, string category) { var all = keys.ToArray(); Require(all.Distinct().Count() == all.Length, "duplicate " + category); }
    static string Path(JsonElement value, string name) => SafePath(Text(value, name));
    static string? NullablePath(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Path(value, name);
}
