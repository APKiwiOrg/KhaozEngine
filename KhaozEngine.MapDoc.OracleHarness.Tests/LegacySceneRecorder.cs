using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record LegacyScenePlacement(long Id, string ArchetypeId, Matrix4x4 World, bool Roof,
    IReadOnlyList<IReadOnlyList<GltfMeshPart>> Tiers, IReadOnlyList<string> Tags, KhaozEngine.MapDoc.Editing.MapBox3 Bounds);
internal sealed record LegacySceneDraw(GltfMesh Mesh, Matrix4x4 World, bool ShadowOnly, float Dissolve, float Complement,
    bool InvertShadow, bool CastShadows);
internal sealed record LegacySceneFrame(string ProbeId, IReadOnlyList<LegacySceneDraw> Draws, MapFramePoint Focus);
internal sealed record LegacySceneInventory(IReadOnlyList<LegacyScenePlacement> Placements,
    IReadOnlyList<LegacySceneDraw> Ground, IReadOnlyList<WaterPlane> Water,
    IReadOnlyDictionary<string, long> Foliage, IReadOnlyList<LegacySceneFrame> Frames,
    IReadOnlyList<SceneSourceLight> Lights);

/// <summary>CPU-only expected inventory. All inputs come from the original world, catalog and retained source policy.</summary>
[UnsupportedOSPlatform("windows")]
internal static class LegacySceneRecorder
{
    internal static LegacySceneInventory Record(TileWorldDocument document, TileWorldCatalogs catalogs, ShippedSceneInputs inputs)
    {
        var scene = new LegacyRecordingScene();
        var ground = new List<LegacySceneDraw>(); var water = new List<WaterPlane>();
        var placements = new List<LegacyScenePlacement>();
        var full = new Dictionary<string, IReadOnlyList<MeshHandle>>(StringComparer.Ordinal);
        var far = new Dictionary<string, IReadOnlyList<MeshHandle>>(StringComparer.Ordinal);
        var originalParts = new Dictionary<string, IReadOnlyList<GltfMeshPart>>(StringComparer.Ordinal);
        foreach (var row in inputs.Manifest.Placements)
            foreach (var lod in row.Lods)
                if (!originalParts.ContainsKey(lod.SourcePath))
                    originalParts.Add(lod.SourcePath, GltfLoader.LoadPartsWithMaterials(inputs.OriginalMeshes[lod.SourcePath], "attested source mesh"));
        var mappings = inputs.Manifest.Placements.ToDictionary(p => p.SourceObjectId);
        var pathsByArchetype = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var obj in document.AllObjects().OrderBy(o => o.Id))
        {
            var archetype = catalogs.Archetype(obj.ArchetypeId)!;
            var position = TileObjectProps.AnchorPosition(document, archetype, obj);
            var transform = Matrix4x4.CreateRotationY(TileObjectProps.YawRadians(archetype, obj.Rotation)) * Matrix4x4.CreateTranslation(position);
            var mapping = mappings[obj.Id];
            pathsByArchetype.TryAdd(archetype.Id, mapping.Lods[^1].SourcePath);
            var tiers = mapping.Lods.Select(l => originalParts[l.SourcePath]).ToArray();
            var points = tiers.SelectMany(t => t).SelectMany(p => p.Mesh.Vertices).Select(v => Vector3.Transform(v.Position, transform));
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            foreach (var point in points) { min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
            foreach (var light in inputs.SourcePolicy.Lights.Where(l => l.SourceObjectId == obj.Id))
            {
                var point = Vector3.Transform(light.Offset, transform);
                min = Vector3.Min(min, point - new Vector3(light.Radius)); max = Vector3.Max(max, point + new Vector3(light.Radius));
            }
            placements.Add(new(obj.Id, archetype.Id, transform, archetype.IsRoof, Array.AsReadOnly(tiers), (obj.Tags ?? []).AsReadOnly(), new(min.X, min.Y, min.Z, max.X, max.Y, max.Z)));
            if (!full.ContainsKey(archetype.Id)) full.Add(archetype.Id, scene.LoadPropMeshes(tiers[0]));
            if (tiers.Length > 1 && !far.ContainsKey(archetype.Id)) far.Add(archetype.Id, scene.LoadPropMeshes(tiers[1]));
        }
        var material = TileGroundMaterials.Build(catalogs, inputs.SourceTexture);
        foreach (var region in document.Regions.Keys.OrderBy(r => r.Rx).ThenBy(r => r.Rz))
            for (int plane = 0; plane < document.PlaneCount; plane++)
            {
                var mesh = TileGroundMesher.Build(document, catalogs, region, plane, new() { Slots = material });
                if (mesh is not null)
                {
                    scene.DrawMesh(scene.LoadMesh(mesh), TileGroundMesher.WorldMatrix(document, region));
                    ground.Add(scene.Draws[^1]);
                }
                foreach (var item in TileWaterPlanes.Collect(document, catalogs, region, plane, inputs.SourcePolicy.Water))
                { scene.DrawWater(item); water.Add(item); }
            }
        var foliage = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var layer in document.FoliageLayers)
        {
            var surface = new TileFoliageSurface(document, catalogs, layer);
            long count = 0;
            foreach (var region in ShippedFoliageComparison.Regions(document))
            {
                var cover = GroundCoverDistribution.Generate(ShippedFoliageComparison.Area(region), ShippedFoliageComparison.Settings(layer), surface.Sample);
                scene.DrawGroundCover(cover, full, Vector3.Zero, inputs.SourcePolicy.Foliage);
                count += cover.Count;
            }
            foliage.Add(layer.Id, count);
        }
        var frames = new List<LegacySceneFrame>();
        foreach (var probe in inputs.Manifest.Policy.Probes)
        {
            scene.Draws.Clear();
            using var roofPolicy = RoofView(document, probe);
            var window = inputs.Manifest.Windows.Single(w => w.Id == probe.WindowId);
            double fx = probe.FocusX ?? (window.Bounds.MinX / 2 + window.Bounds.MaxX / 2), fz = probe.FocusZ ?? (window.Bounds.MinZ / 2 + window.Bounds.MaxZ / 2);
            double fy = probe.FocusY ?? (document.HeightAt((float)fx, (float)fz, 0) + 2);
            var framedFocus = ShippedSceneInputs.Point(fx, fy, fz);
            Vector3 focus = new((float)fx, (float)fy, (float)fz);
            foreach (var region in document.Regions.Keys)
                for (int plane = 0; plane < document.PlaneCount; plane++)
                {
                    var props = TileObjectProps.Build(document, catalogs, region, plane);
                    var remaining = props.Ground.Where(p => !inputs.SourcePolicy.AnimatedAssets.Contains(p.Id) && !inputs.SourcePolicy.Placements.Layers.Any(l => l.AssetIds.Contains(p.Id))).ToArray();
                    scene.DrawProps(remaining, full, focus, inputs.SourcePolicy.Placements.DefaultDrawRadius);
                    for (int roof = 0; roof < props.Roofs.Count; roof++)
                    {
                        if (roofPolicy.IsRoofHidden(props.RoofFootprints[roof], plane))
                            scene.DrawShadowOnlyProps([props.Roofs[roof]], full, focus, inputs.SourcePolicy.Placements.DefaultDrawRadius);
                        else scene.DrawProps([props.Roofs[roof]], full, focus, inputs.SourcePolicy.Placements.DefaultDrawRadius);
                    }
                    foreach (var policy in inputs.SourcePolicy.Placements.Layers)
                    {
                        var selected = props.Ground.Where(p => policy.AssetIds.Contains(p.Id)).ToArray();
                        var layer = PropLayer.PlacementLayer(selected, full, policy.DrawRadius, lodPartMeshes: far,
                            lodDistance: policy.LodDistances.FirstOrDefault(), castsShadows: policy.CastsShadows).WithLodCrossfade(policy.LodCrossfadeWidth);
                        if (policy.HlodDistance > 0)
                        {
                            var flat = policy.AssetIds.ToDictionary(id => id, id => GltfLoader.LoadFlattenedAlbedo(inputs.OriginalMeshes[pathsByArchetype[id]], "attested source HLOD"));
                            layer = layer.WithHlod(flat, policy.HlodDistance, policy.HlodWeldCell, policy.HlodCrossfadeWidth);
                            using var cluster = new LegacyClusterOwner(scene);
                            var key = new PropClusterKey(policy.Id, region.Rx, region.Rz, plane);
                            var build = cluster.BuildCpu(new(key, 1, new(region.OriginX, -region.OriginZ - 64, region.OriginX + 64, -region.OriginZ), layer, selected));
                            cluster.Apply(key, build); cluster.Draw(focus);
                        }
                        else scene.DrawLayer(selected, layer, focus, 0);
                    }
                }
            frames.Add(new(probe.Id, Array.AsReadOnly(scene.Draws.ToArray()), framedFocus));
        }
        return new(Array.AsReadOnly(placements.ToArray()), Array.AsReadOnly(ground.ToArray()), Array.AsReadOnly(water.ToArray()),
            new ReadOnlyDictionary<string, long>(foliage), Array.AsReadOnly(frames.ToArray()), inputs.SourcePolicy.Lights);
    }
    internal static bool RoofHidden(TileWorldDocument world, TileRect roof, int roofPlane, SceneProbe probe)
    { using var view = RoofView(world, probe); return view.IsRoofHidden(roof, roofPlane); }
    static TileWorldView RoofView(TileWorldDocument world, SceneProbe probe)
    {
        int x = (int)Math.Floor(probe.ObserverX), z = (int)Math.Floor(-probe.ObserverZ);
        var floors = Enumerable.Range(0, world.PlaneCount).Where(p => world.GetUnderlay(x, z, p) != 0)
            .Select(p => (Plane: p, Height: world.HeightAt((float)probe.ObserverX, (float)probe.ObserverZ, p)))
            .Where(p => p.Height <= probe.ObserverY).OrderByDescending(p => p.Height).ToArray();
        if (floors.Length > 1 && floors[0].Height == floors[1].Height) throw new System.IO.InvalidDataException("ambiguous source observer floor");
        return new TileWorldView(new LegacyRecordingScene(), world, TileWorldCatalogs.Empty(), new GreyboxMeshResolver())
        {
            Observer = new(x, z, floors.Length == 0 ? 0 : floors[0].Plane),
            RoofMode = probe.RoofMode switch
            {
                KhaozEngine.MapDoc.Interiors.MapRoofMode.AlwaysHidden => RoofVisibility.AlwaysHidden,
                KhaozEngine.MapDoc.Interiors.MapRoofMode.AlwaysVisible => RoofVisibility.AlwaysVisible,
                _ => RoofVisibility.Interior
            }
        };
    }

}

/// <summary>Records the released legacy emission routes with fake handles and no Scene3D, window or provider.</summary>
internal sealed class LegacyRecordingScene : ITileWorldScene, IPropClusterRenderBackend
{
    readonly Dictionary<MeshHandle, GltfMesh> _meshes = new();
    int _next;
    internal List<LegacySceneDraw> Draws { get; } = [];
    public MeshHandle LoadMesh(GltfMesh mesh) { var handle = new MeshHandle(++_next, 1); _meshes.Add(handle, mesh); return handle; }
    public void UnloadMesh(MeshHandle handle) { if (!_meshes.Remove(handle)) throw new InvalidOperationException("stale legacy handle"); }
    public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) => Array.AsReadOnly(parts.Select(p => LoadMesh(p.Mesh)).ToArray());
    public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { foreach (var handle in handles) UnloadMesh(handle); }
    public void DrawMesh(MeshHandle handle, Matrix4x4 world) => Draws.Add(new(_meshes[handle], world, false, 0, 0, false, true));
    public int DrawProps(IReadOnlyList<PropPlacement> placements, IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) =>
        Emit(placements, parts, null, 0, 0, focus, drawRadius, false, true, 0);
    public int DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements, IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) =>
        Emit(placements, parts, null, 0, 0, focus, drawRadius, true, true, 0);
    public int DrawGroundCover(IReadOnlyList<GroundCoverInstance> cover, IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, GroundCoverRenderOptions options) => cover.Count;
    public void DrawWater(in WaterPlane plane) { }
    int Emit(IReadOnlyList<PropPlacement> placements, IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? far, float distance, float width, Vector3 focus, float radius, bool shadowOnly, bool shadows, float floor) =>
        PropRenderer.EmitParts(placements, parts, far, distance, focus, radius, 0, floor, this,
            (target, handle, matrix, dissolve, complement) => target.Draws.Add(new(target._meshes[handle], matrix, shadowOnly, dissolve, complement, false, shadows)), lodCrossfadeWidth: width);
    internal void DrawLayer(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float floor) =>
        Emit(placements, layer.PartMeshes!, layer.LodPartMeshes, layer.LodDistance, layer.LodCrossfadeWidth, focus, layer.DrawRadius, false, layer.CastsShadows, floor);
    void IPropClusterRenderBackend.DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor) => DrawLayer(placements, layer, focus, dissolveFloor);
    public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool invertShadowDissolve) =>
        Draws.Add(new(_meshes[handle], Matrix4x4.Identity, false, dissolve, 0, invertShadowDissolve, layer.CastsShadows));
}

internal sealed class LegacyClusterOwner : ITileWorldPropClusterOwner
{
    readonly PropClusterRenderer _renderer;
    internal LegacyClusterOwner(LegacyRecordingScene scene) => _renderer = new(scene, PropHlod.BuildMergedMeshMeasured, (_, error) => throw error);
    public PropClusterCpuBuild BuildCpu(PropClusterBuildRequest request) => _renderer.BuildCpu(request);
    public void Apply(PropClusterKey key, PropClusterCpuBuild build) => _renderer.Apply(key, build);
    public void Invalidate(PropClusterKey key) => _renderer.Invalidate(key);
    public void Unload(PropClusterKey key) => _renderer.Unload(key);
    public void Draw(Vector3 focus) => _renderer.Draw(focus);
    public bool TryGetDrawState(PropClusterKey key, Vector3 focus, out PropClusterDrawState state) => _renderer.TryGetDrawState(key, focus, out state);
    public void Dispose() => _renderer.Dispose();
}
