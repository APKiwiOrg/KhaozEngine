using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal sealed class ShippedSceneInputs
{
    internal ShippedSceneManifest Manifest { get; }
    internal TileWorldDocument World { get; }
    internal TileWorldCatalogs Catalogs { get; }
    internal double NativeTileSize { get; private set; }
    internal ShippedScenePolicy SourcePolicy { get; }
    internal ShippedScenePolicy NativePolicy { get; }
    internal IReadOnlyDictionary<string, byte[]> OriginalMeshes { get; }
    internal IReadOnlyDictionary<string, string> SourceModelPaths { get; private set; } = new Dictionary<string, string>();
    internal IReadOnlyDictionary<string, string[]> AssetMappings { get; private set; } = new Dictionary<string, string[]>();
    readonly ShippedFoliageInputs.Snapshot _snapshot;
    readonly byte[]? _document;
    readonly Dictionary<string, byte[]> _textures = new(StringComparer.Ordinal);
    readonly string _tiledDirectory, _scratch;
    internal ShippedBuildingInputs? Buildings { get; private set; }
    internal ShippedMarkerInputs? Markers { get; private set; }
    internal IReadOnlyList<FoliageLayerBinding> FoliageBindings { get; private set; } = [];

    ShippedSceneInputs(ShippedSceneManifest manifest, TileWorldDocument world, TileWorldCatalogs catalogs,
        ShippedScenePolicy sourcePolicy, ShippedScenePolicy nativePolicy, IReadOnlyDictionary<string, byte[]> meshes,
        ShippedFoliageInputs.Snapshot snapshot, byte[]? document, string tiledDirectory, string scratch)
    {
        Manifest = manifest; World = world; Catalogs = catalogs; SourcePolicy = sourcePolicy; NativePolicy = nativePolicy;
        OriginalMeshes = meshes; _snapshot = snapshot; _document = document; _tiledDirectory = tiledDirectory; _scratch = scratch;
    }
    internal static ShippedSceneInputs Read(PrivateOracleInputs inputs)
    {
        var manifest = ShippedSceneManifest.Read(inputs.Provenance.Comparison);
        var snapshot = new ShippedFoliageInputs.Snapshot(inputs);
        snapshot.Bytes(manifest.Generator.Path);
        foreach (string helper in manifest.Generator.Helpers) snapshot.Bytes(helper);
        string worldPath = inputs.Provenance.Paths.Single(p => System.IO.Path.GetFileName(p.Path) == TileWorldFile.ManifestFileName).Path;
        var worldJson = snapshot.Json(worldPath);
        string worldDirectory = System.IO.Path.GetDirectoryName(worldPath)!.Replace('\\', '/');
        foreach (var region in worldJson.GetProperty("regions").EnumerateArray())
        {
            Shape(region, "rx", "rz", "hash");
            string path = snapshot.RelativeTo(worldDirectory, "regions/" + TileWorldFile.RegionFileName(new(region.GetProperty("rx").GetInt32(), region.GetProperty("rz").GetInt32())));
            Require(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(snapshot.Bytes(path))) == Text(region, "hash"), "scene region differs from world manifest");
        }
        var (world, catalogs) = ShippedSourceInventory.ReadWorld(inputs);
        Require(world.TileSize == 1, "scene comparison requires metre source tiles");
        string directory = System.IO.Path.GetDirectoryName(manifest.NativeTiledManifestPath)!.Replace('\\', '/');
        Require(System.IO.Path.GetFileName(manifest.NativeTiledManifestPath) == "map.json" && directory.Length != 0, "invalid scene tiled manifest");
        snapshot.Json(manifest.NativeTiledManifestPath);
        string scratch = System.IO.Path.GetDirectoryName(inputs.ReportPath)!;
        var native = ReloadTiled(snapshot, directory, scratch);
        byte[]? bytes = manifest.NativeDocumentPath is { } documentPath ? snapshot.Bytes(documentPath).ToArray() : null;
        var closure = MapAssetClosure.Load(native.NativeAssets, snapshot);
        ShippedSceneSourceChecks.Storage(native, bytes is null ? null : MapDocumentFile.LoadText(Encoding.UTF8.GetString(bytes)), closure);
        var effective = manifest.Limits is { HasQueryProfile: true } declared
            ? MapPrefabResolver.ExpandWithLimits(native, closure, declared.Queries) : MapPrefabResolver.Expand(native, closure);
        var sourcePolicy = ShippedScenePolicy.Read(snapshot.Json(manifest.Policy.SourcePolicyPath));
        var nativePolicy = ShippedScenePolicy.Read(snapshot.Json(manifest.Policy.NativePolicyPath));
        var objects = world.AllObjects().ToDictionary(o => o.Id);
        Require(objects.Keys.Order().SequenceEqual(manifest.Placements.Select(p => p.SourceObjectId).Order()), "incomplete scene source placement correspondence");
        Require(effective.Placements.Select(p => p.PlacementId).Order(StringComparer.Ordinal).SequenceEqual(manifest.Placements.Select(p => p.NativePlacementId).Order(StringComparer.Ordinal)), "incomplete scene native placement correspondence");
        var meshes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var row in manifest.Placements)
        {
            var archetype = catalogs.Archetype(objects[row.SourceObjectId].ArchetypeId) ?? throw new InvalidDataException("missing scene source archetype");
            string[] paths = new[] { archetype.MeshRef }.Concat(archetype.LodMeshRef is { Length: > 0 } lod ? [lod] : []).ToArray();
            Require(paths.Length == row.Lods.Count, "scene source LOD correspondence differs");
            var placement = effective.Placements.Single(p => p.PlacementId == row.NativePlacementId);
            var asset = closure.GetAsset(placement.AssetId);
            Require(new[] { asset.MeshResourceId }.Concat(asset.LodResourceIds).SequenceEqual(row.Lods.Select(l => l.NativeResourceId)), "scene native LOD correspondence differs");
            for (int i = 0; i < paths.Length; i++)
            {
                string sourcePath = SafePath(paths[i]);
                Require(row.Lods[i].SourcePath == sourcePath, "scene source LOD order differs");
                ValidateOriginalMesh(snapshot.Bytes(sourcePath));
                meshes.TryAdd(sourcePath, snapshot.Bytes(sourcePath).ToArray());
            }
        }
        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var material in catalogs.Materials.Values)
            if (material.Texture is { Length: > 0 } texture)
            {
                string catalogDirectory = System.IO.Path.GetRelativePath(inputs.SourceRoot, System.IO.Path.GetDirectoryName(catalogs.MaterialSource(material.Id)!)!).Replace('\\', '/');
                string relative = snapshot.RelativeTo(catalogDirectory == "." ? "" : catalogDirectory, texture);
                textures.TryAdd(System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(catalogs.MaterialSource(material.Id)!)!, texture)), snapshot.Bytes(relative).ToArray());
            }
        var materialIds = new HashSet<ushort>();
        foreach (var region in world.Regions.Values)
            for (int plane = 0; plane < world.PlaneCount; plane++)
                for (int z = 0; z < TileRegion.Size; z++)
                    for (int x = 0; x < TileRegion.Size; x++)
                    {
                        int wx = region.Coord.Rx * TileRegion.Size + x, wz = region.Coord.Rz * TileRegion.Size + z;
                        materialIds.Add(world.GetUnderlay(wx, wz, plane)); materialIds.Add(world.GetOverlay(wx, wz, plane));
                    }
        materialIds.Remove(0);
        var requiredMaterials = materialIds.Order().ToArray();
        Require(requiredMaterials.SequenceEqual(manifest.Materials.Select(m => m.SourceMaterialId).Order()), "incomplete scene material correspondence");
        foreach (var row in manifest.Materials) closure.GetResource(row.NativeResourceId);
        Require(objects.Values.Where(o => catalogs.Archetype(o.ArchetypeId)?.IsRoof == true).Select(o => o.Id).Order()
            .SequenceEqual(manifest.Roofs.Select(r => r.SourceObjectId).Order()), "incomplete scene source roof correspondence");
        Require(effective.BuildingIntent.Roofs.Select(r => r.Id).Order(StringComparer.Ordinal).SequenceEqual(manifest.Roofs.Select(r => r.NativePlacementId).Order(StringComparer.Ordinal)), "incomplete scene native roof correspondence");
        Require(MapEffectiveAnnotations.Lights(effective).Select(l => l.Id).Order(StringComparer.Ordinal).SequenceEqual(manifest.Lights.Select(l => l.NativeLightId).Order(StringComparer.Ordinal)), "incomplete scene light correspondence");
        Require(sourcePolicy.Lights.Select(l => (l.SourceObjectId, l.SourceProfileId)).Order().SequenceEqual(manifest.Lights.Select(l => (l.SourceObjectId, l.SourceProfileId)).Order()), "incomplete source light policy");
        var sourceWaterIds = new List<string>();
        foreach (var region in world.Regions.Keys)
            for (int plane = 0; plane < world.PlaneCount; plane++)
            {
                var bodies = TileWaterBodies.Collect(world, catalogs, region, plane);
                for (int i = 0; i < bodies.Count; i++) sourceWaterIds.Add(KhaozEngine.Tests.MapDocCompatibility.LegacyWaterConverter.BodyId(region, plane, i));
            }
        Require(sourceWaterIds.Order(StringComparer.Ordinal).SequenceEqual(manifest.Water.Select(w => w.SourceBodyKey).Order(StringComparer.Ordinal)), "incomplete scene source water correspondence");
        Require((effective.Water?.Bodies ?? []).Select(b => b.Id).Order(StringComparer.Ordinal).SequenceEqual(manifest.Water.Select(w => w.NativeBodyId).Order(StringComparer.Ordinal)), "incomplete scene native water correspondence");
        foreach (var row in manifest.Water)
            Require(effective.Water!.Bodies.Single(b => b.Id == row.NativeBodyId).Covers.Select(c => c.Id).Order(StringComparer.Ordinal).SequenceEqual(row.NativeSpaceIds.Order(StringComparer.Ordinal)), "scene water space correspondence differs");
        Require(world.FoliageLayers.Select(l => l.Id).Order(StringComparer.Ordinal).SequenceEqual(manifest.Foliage.Select(f => f.SourceLayerId).Order(StringComparer.Ordinal)), "incomplete scene foliage correspondence");
        Require((manifest.BuildingManifestPath is not null) == (manifest.Roofs.Count > 0 || native.Prefabs?.Instances.Count > 0), "scene building manifest participation differs");
        Require((manifest.MarkerManifestPath is not null) == (world.AllMarkers().Any()), "scene marker manifest participation differs");
        Require((manifest.FoliageManifestPath is not null) == (manifest.Foliage.Count > 0), "scene foliage manifest participation differs");
        var result = new ShippedSceneInputs(manifest, world, catalogs, sourcePolicy, nativePolicy, meshes, snapshot, bytes, directory, scratch);
        result.NativeTileSize = native.TileSize;
        foreach (var pair in textures) result._textures.Add(pair.Key, pair.Value);
        if (manifest.BuildingManifestPath is { } building)
        {
            result.Buildings = ShippedBuildingInputs.Read(inputs, snapshot.Json(building));
            var groups = result.Buildings.Comparison.GetProperty("groups").EnumerateArray().ToArray();
            Require(groups.Select(g => Text(g, "id")).Order(StringComparer.Ordinal).SequenceEqual(effective.BuildingIntent.RoofGroups.Select(g => g.Id).Order(StringComparer.Ordinal)), "scene roof group inventory differs");
            foreach (var group in groups)
            {
                var actual = effective.BuildingIntent.RoofGroups.Single(g => g.Id == Text(group, "id"));
                Require(ShippedSceneManifest.Strings(group.GetProperty("rooms")).Order(StringComparer.Ordinal).SequenceEqual(actual.Rooms.Select(r => r.Id).Order(StringComparer.Ordinal)), "scene roof group rooms differ");
            }
            foreach (var roof in result.Buildings.Comparison.GetProperty("roofs").EnumerateArray())
            {
                long sourceId = roof.GetProperty("leafId").GetInt64();
                string id = manifest.Roofs.Single(r => r.SourceObjectId == sourceId).NativePlacementId;
                var links = effective.BuildingIntent.RoofLinks.Where(l => l.Roof.Id == id).ToArray();
                bool linked = roof.GetProperty("linkId").ValueKind != JsonValueKind.Null;
                Require(links.Length == (linked ? 1 : 0), "scene roof link inventory differs");
                if (!linked) continue;
                Require(links[0].Id == Text(roof, "linkId") && links[0].AboveY == roof.GetProperty("aboveY").GetSingle() &&
                    links[0].Rooms.Select(r => r.Id).Order(StringComparer.Ordinal).SequenceEqual(ShippedSceneManifest.Strings(roof.GetProperty("rooms")).Order(StringComparer.Ordinal)) &&
                    links[0].Groups.Select(r => r.Id).Order(StringComparer.Ordinal).SequenceEqual(ShippedSceneManifest.Strings(roof.GetProperty("groups")).Order(StringComparer.Ordinal)), "scene roof correspondence differs");
            }
        }
        if (manifest.MarkerManifestPath is { } marker) result.Markers = ShippedMarkerInputs.Read(inputs, snapshot.Json(marker), scene: true);
        if (manifest.FoliageManifestPath is { } foliagePath)
        {
            var foliageManifest = ShippedFoliageManifest.Read(snapshot.Json(foliagePath));
            snapshot.Bytes(foliageManifest.GeneratorPath);
            foreach (string helper in foliageManifest.Helpers) snapshot.Bytes(helper);
            var correspondence = new ShippedFoliageCorrespondence(world, catalogs, foliageManifest, native, closure);
            result.FoliageBindings = correspondence.Layers();
            var capture = MapFoliageGenerationContext.Capture(native, closure, manifest.Limits?.Scene.Preparation?.FoliageContext, manifest.Limits is { HasQueryProfile: true } queryProfile ? queryProfile.Queries : null);
            if (capture.Capacity is { } capacity) throw new KhaozEngine.MapDoc.Scene.MapSceneCapacityException(capacity.LimitName, capacity.Requested, capacity.Allowance);
            Require(capture.Context is not null, "scene foliage capture refused");
            correspondence.Owners(capture.Context!);
            Require(manifest.Foliage.All(f => result.FoliageBindings.Any(b => b.Row.SourceLayerId == f.SourceLayerId && b.Row.NativeLayerId == f.NativeLayerId)), "scene foliage mappings differ");
        }
        result.AssetMappings = ShippedSceneSelections.Bind(result, closure, effective);
        var modelPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in manifest.Placements) modelPaths.TryAdd(objects[row.SourceObjectId].ArchetypeId, row.Lods[0].SourcePath);
        foreach (string id in ShippedSceneSelections.DeclaredSourceIds(sourcePolicy))
        {
            var archetype = catalogs.Archetype(id) ?? throw new InvalidDataException("unknown scene source selection");
            string path = SafePath(archetype.MeshRef);
            modelPaths.TryAdd(id, path);
            foreach (string meshPath in new[] { path }.Concat(archetype.LodMeshRef is { Length: > 0 } lod ? [SafePath(lod)] : []))
            {
                byte[] mesh = snapshot.Bytes(meshPath);
                ValidateOriginalMesh(mesh); meshes.TryAdd(meshPath, mesh.ToArray());
            }
        }
        result.SourceModelPaths = modelPaths;
        Require(inputs.Provenance.Verify(inputs.SourceRoot) == 0, "scene extraction changed during load");
        return result;
    }
    internal MapDocument Reload(bool tiled) => tiled ? ReloadTiled(_snapshot, _tiledDirectory, _scratch)
        : _document is { } bytes ? MapDocumentFile.LoadText(Encoding.UTF8.GetString(bytes))
        : throw new InvalidDataException("scene monolithic form was not declared");

    static MapDocument ReloadTiled(ShippedFoliageInputs.Snapshot snapshot, string directory, string scratch)
    {
        string root = System.IO.Path.Combine(scratch, "scene-head-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, PrivateOracleFixtures.SecureDirectoryMode);
        try
        {
            foreach (string path in snapshot.Under(directory))
            {
                string target = System.IO.Path.Combine(root, path[(directory.Length + 1)..]);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!); File.WriteAllBytes(target, snapshot.Bytes(path));
            }
            Require(MapDocumentFile.VerifyTiled(root).Count == 0, "scene tiled store does not verify");
            return MapDocumentFile.LoadTiled(root, new() { VerifyTileHashes = true });
        }
        finally { Directory.Delete(root, true); }
    }
    static void ValidateOriginalMesh(byte[] bytes)
    {
        Require(bytes.Length >= 20 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x46546c67, "scene source mesh must be a self-contained binary glTF");
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        Require(length > 0 && length <= bytes.Length - 20, "invalid scene source mesh JSON");
        using var json = JsonDocument.Parse(bytes.AsMemory(20, length));
        UniqueMembers(json.RootElement);
        foreach (string name in new[] { "buffers", "images" })
            if (json.RootElement.TryGetProperty(name, out var rows))
                foreach (var row in rows.EnumerateArray())
                    Require(!row.TryGetProperty("uri", out var uri) || uri.ValueKind == JsonValueKind.String && uri.GetString()!.StartsWith("data:", StringComparison.Ordinal), "unattested external scene mesh dependency");
    }
    internal KhaozEngine.Render2D.ImageRgba SourceTexture(string path) =>
        _textures.TryGetValue(System.IO.Path.GetFullPath(path), out var bytes) ? KhaozEngine.Render2D.ImageRgba.Decode(bytes)
            : throw new InvalidDataException("unattested scene source texture");
    internal MapAssetClosure Assets(MapDocument document) => MapAssetClosure.Load(document.NativeAssets, _snapshot);
    internal static MapSceneScope Scope(SceneWindow window) => Scope(window, null);
    internal static MapSceneScope Scope(SceneWindow window, MapQueryLimits? limits)
    {
        var b = window.Bounds; var centre = Point(b.MinX / 2 + b.MaxX / 2, 0, b.MinZ / 2 + b.MaxZ / 2);
        return new(new(centre.Frame, new((float)(b.MinX - centre.Frame.X * 128d), (float)(b.MinZ - centre.Frame.Z * 128d)),
            new((float)(b.MaxX - centre.Frame.X * 128d), (float)(b.MaxZ - centre.Frame.Z * 128d)), (float)b.MinY, (float)b.MaxY,
            Enum.GetValues<MapSurfaceRole>(), window.SpaceIds, limits ?? new()), b, MapSceneChannels.All, window.MotionPaddingMetres);
    }
    internal static MapFramePoint Point(double x, double y, double z)
    {
        Require(double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z), "invalid scene framed point");
        double fx = Math.Round(x / WorldFrame.Grid), fz = Math.Round(z / WorldFrame.Grid);
        Require(fx >= short.MinValue && fx <= short.MaxValue && fz >= short.MinValue && fz <= short.MaxValue && float.IsFinite((float)y), "scene framed point outside range");
        return new(new(checked((short)fx), checked((short)fz)), new((float)(x - fx * 128), (float)y, (float)(z - fz * 128)));
    }
}
