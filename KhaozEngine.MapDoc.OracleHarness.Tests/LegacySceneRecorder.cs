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
    bool InvertShadow, bool CastShadows, MapRenderKey? Key = null, GltfMaterialMaps Maps = default);
internal sealed record LegacySceneFrame(string ProbeId, IReadOnlyList<LegacySceneDraw> Draws, MapFramePoint Focus);
internal sealed record LegacySceneInventory(IReadOnlyList<LegacyScenePlacement> Placements,
    IReadOnlyList<LegacySceneDraw> Ground, IReadOnlyList<WaterPlane> Water,
    IReadOnlyDictionary<string, long> Foliage, IReadOnlyList<LegacySceneFrame> Frames,
    IReadOnlyList<SceneSourceLight> Lights);

/// <summary>CPU-only expected inventory. All inputs come from the original world, catalog and retained source policy.</summary>
[UnsupportedOSPlatform("windows")]
internal static class LegacySceneRecorder
{
    internal static IReadOnlyList<SceneSourceFoliageGeneration> RecordFoliage(ShippedSceneInputs inputs, KhaozEngine.MapDoc.Scene.MapSceneScope scope) =>
        ShippedSceneFoliageRoster.Capture(inputs, scope);
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
        foreach (var obj in document.AllObjects().OrderBy(o => o.Id))
        {
            var archetype = catalogs.Archetype(obj.ArchetypeId)!;
            var position = TileObjectProps.AnchorPosition(document, archetype, obj);
            var transform = Matrix4x4.CreateRotationY(TileObjectProps.YawRadians(archetype, obj.Rotation)) * Matrix4x4.CreateTranslation(position);
            var mapping = mappings[obj.Id];
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
            if (!full.ContainsKey(archetype.Id)) full.Add(archetype.Id, scene.LoadParts(tiers[0], 0));
            if (tiers.Length > 1 && !far.ContainsKey(archetype.Id)) far.Add(archetype.Id, scene.LoadParts(tiers[1], 1));
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
            using var roofPolicy = RoofView(document, probe);
            var window = inputs.Manifest.Windows.Single(w => w.Id == probe.WindowId);
            double fx = probe.FocusX ?? (window.Bounds.MinX / 2 + window.Bounds.MaxX / 2), fz = probe.FocusZ ?? (window.Bounds.MinZ / 2 + window.Bounds.MaxZ / 2);
            double fy = probe.FocusY ?? (document.HeightAt((float)fx, (float)fz, 0) + 2);
            var framedFocus = ShippedSceneInputs.Point(fx, fy, fz);
            Vector3 focus = new((float)fx, (float)fy, (float)fz);
            var groundProps = new List<PropPlacement>();
            var frameOwners = new List<LegacyScenePlacement>();
            var byId = placements.ToDictionary(p => p.Id);
            var scope = ShippedSceneInputs.Scope(window, inputs.Manifest.Limits?.Queries);
            var roofProps = new List<(PropPlacement Prop, TileRect Footprint, int Plane)>();
            foreach (var region in document.Regions.Keys)
                for (int plane = 0; plane < document.PlaneCount; plane++)
                {
                    var props = TileObjectProps.Build(document, catalogs, region, plane);
                    for (int i = 0; i < props.Ground.Count; i++)
                    {
                        var owner = byId[props.GroundObjectIds[i]];
                        groundProps.Add(props.Ground[i]); frameOwners.Add(owner);
                    }
                    for (int roof = 0; roof < props.Roofs.Count; roof++)
                    {
                        var owner = byId[props.RoofObjectIds[roof]];
                        roofProps.Add((props.Roofs[roof], props.RoofFootprints[roof], plane)); frameOwners.Add(owner);
                    }
                }
            scene.BeginFrame(frameOwners, mappings);
            var inWindow = frameOwners.Where(p => ShippedSceneExpectedFrames.InScope(p, scope)).Select(p => (p.ArchetypeId, p.World)).ToHashSet();
            var nativeOrder = frameOwners.GroupBy(p => (p.ArchetypeId, p.World)).ToDictionary(g => g.Key, g => mappings[g.First().Id].NativePlacementId);
            bool Selected(PropPlacement prop) => inWindow.Contains((prop.Id, LegacyRecordingScene.Transform(prop)));
            scene.DrawProps(groundProps.Where(p => Selected(p) && !inputs.SourcePolicy.AnimatedAssets.Contains(p.Id) &&
                !inputs.SourcePolicy.Placements.Layers.Any(l => l.AssetIds.Contains(p.Id))).ToArray(), full, focus, inputs.SourcePolicy.Placements.DefaultDrawRadius);
            foreach (var roof in roofProps.Where(r => Selected(r.Prop) && !inputs.SourcePolicy.Placements.Layers.Any(l => l.AssetIds.Contains(r.Prop.Id))))
                if (roofPolicy.IsRoofHidden(roof.Footprint, roof.Plane)) scene.DrawShadowOnlyProps([roof.Prop], full, focus, inputs.SourcePolicy.Placements.DefaultDrawRadius);
                else scene.DrawProps([roof.Prop], full, focus, inputs.SourcePolicy.Placements.DefaultDrawRadius);
            foreach (var policy in inputs.SourcePolicy.Placements.Layers)
            {
                foreach (bool roofLayer in new[] { false, true })
                {
                    var selected = (roofLayer ? roofProps.Select(r => r.Prop) : groundProps).Where(p => policy.AssetIds.Contains(p.Id) && !inputs.SourcePolicy.AnimatedAssets.Contains(p.Id)).ToArray();
                    if (selected.Length == 0) continue;
                    var layer = PropLayer.PlacementLayer(selected, full, policy.DrawRadius, fadeBandWidth: policy.HlodCrossfadeWidth, lodPartMeshes: far,
                        lodDistance: policy.LodDistances.FirstOrDefault(), castsShadows: policy.CastsShadows).WithLodCrossfade(policy.LodCrossfadeWidth);
                    if (policy.HlodDistance <= 0)
                    {
                        foreach (var prop in selected.Where(Selected))
                        {
                            bool hidden = roofLayer && roofProps.Any(r => r.Prop.Equals(prop) && roofPolicy.IsRoofHidden(r.Footprint, r.Plane));
                            scene.DrawLayer([prop], layer, focus, 0, hidden);
                        }
                        continue;
                    }
                    var flat = policy.AssetIds.ToDictionary(id => id, id =>
                    {
                        var archetype = catalogs.Archetype(id) ?? throw new System.IO.InvalidDataException("missing declared source archetype");
                        string path = ShippedFoliageManifest.SafePath(archetype.LodMeshRef is { Length: > 0 } lod ? lod : archetype.MeshRef);
                        return GltfLoader.LoadFlattenedAlbedo(inputs.OriginalMeshes[path], "attested source HLOD");
                    });
                    layer = layer.WithHlod(flat, policy.HlodDistance, policy.HlodWeldCell, policy.HlodCrossfadeWidth);
                    double edge = 64 * document.TileSize, divisions = Math.Ceiling(edge / 1024);
                    double cellEdge = edge / divisions;
                    (int X, int Z) Cell(PropPlacement p) => (checked((int)Math.Floor(p.X / cellEdge)), checked((int)Math.Floor(p.Z / cellEdge)));
                    var requiredCells = selected.Where(Selected).Select(Cell).ToHashSet();
                    foreach (var cell in selected.GroupBy(Cell).Where(c => requiredCells.Contains(c.Key)))
                    {
                        using var cluster = new LegacyClusterOwner(scene);
                        var key = new PropClusterKey(policy.Id, cell.Key.X, cell.Key.Z, 0);
                        var area = new RectArea((float)(edge * cell.Key.X / divisions), (float)(edge * cell.Key.Z / divisions),
                            (float)(edge * ((long)cell.Key.X + 1) / divisions), (float)(edge * ((long)cell.Key.Z + 1) / divisions));
                        scene.ClusterKey = new(roofLayer ? MapRenderKind.Roof : MapRenderKind.Placement, $"cluster/{key.LayerId}/{key.X}/{key.Z}/{key.Plane}", "hlod", 0);
                        scene.ClusterAnchor = new((long)Math.Floor(area.MinX), (long)Math.Floor(cell.Min(p => p.Y)), (long)Math.Floor(area.MinZ));
                        var members = cell.OrderBy(p => nativeOrder[(p.Id, LegacyRecordingScene.Transform(p))], StringComparer.Ordinal).ToArray();
                        var build = cluster.BuildCpu(new(key, 1, area, layer, members));
                        cluster.Apply(key, build);
                        if (roofLayer)
                        {
                            var hidden = members.Select(p => roofProps.Any(r => r.Prop.Equals(p) && roofPolicy.IsRoofHidden(r.Footprint, r.Plane))).ToArray();
                            cluster.DrawWithVisibility(focus, new LegacyRoofClusterVisibility(key, 1, hidden));
                        }
                        else cluster.Draw(focus);
                    }
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
    readonly Dictionary<MeshHandle, (int Lod, int Part)> _parts = new();
    Dictionary<(string, Matrix4x4), Queue<LegacyScenePlacement>> _owners = new();
    IReadOnlyDictionary<long, ScenePlacement> _mappings = null!;
    internal MapRenderKey ClusterKey { get; set; }
    internal KhaozEngine.MapDoc.Surfaces.MapSubmissionAnchor ClusterAnchor { get; set; }
    internal void BeginFrame(IReadOnlyList<LegacyScenePlacement> owners, IReadOnlyDictionary<long, ScenePlacement> mappings)
    {
        Draws.Clear(); _mappings = mappings;
        _owners = owners.GroupBy(p => (p.ArchetypeId, p.World)).ToDictionary(g => g.Key, g => new Queue<LegacyScenePlacement>(g));
    }
    internal IReadOnlyList<MeshHandle> LoadParts(IReadOnlyList<GltfMeshPart> parts, int lod)
    {
        var result = LoadPropMeshes(parts);
        for (int i = 0; i < result.Count; i++) _parts.Add(result[i], (lod, i));
        return result;
    }
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
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>? far, float distance, float width, Vector3 focus, float radius, bool shadowOnly, bool shadows, float floor, float fadeBand = 0)
    {
        int count = 0;
        foreach (var prop in placements)
        {
            var world = Transform(prop);
            var owner = _owners[(prop.Id, world)].Dequeue();
            count += PropRenderer.EmitParts([prop], parts, far, distance, focus, radius, fadeBand, floor, this,
                (target, handle, matrix, dissolve, complement) =>
                {
                    var part = target._parts[handle];
                    var key = new MapRenderKey(owner.Roof ? MapRenderKind.Roof : MapRenderKind.Placement,
                        _mappings[owner.Id].NativePlacementId, "part/" + part.Part, part.Lod);
                    target.Draws.Add(new(target._meshes[handle], matrix, shadowOnly, dissolve, complement, false, shadows, key, owner.Tiers[part.Lod][part.Part].Maps));
                }, lodCrossfadeWidth: width);
        }
        return count;
    }
    internal static Matrix4x4 Transform(PropPlacement prop) => Matrix4x4.CreateScale(prop.Scale) * Matrix4x4.CreateRotationY(prop.Yaw) * Matrix4x4.CreateTranslation(prop.X, prop.Y, prop.Z);
    internal int DrawLayer(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float floor, bool hidden = false) =>
        hidden && !layer.CastsShadows ? 0 : Emit(placements, layer.PartMeshes!, layer.LodPartMeshes, layer.LodDistance, layer.LodCrossfadeWidth, focus, layer.DrawRadius, hidden, layer.CastsShadows, floor, layer.FadeBandWidth);
    int IPropClusterRenderBackend.DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor) => DrawLayer(placements, layer, focus, dissolveFloor);
    int IPropClusterRenderBackend.DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus, float dissolveFloor) => DrawLayer(placements, layer, focus, dissolveFloor, true);
    public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool complement) => Merged(handle, layer, dissolve, complement, false);
    public void DrawShadowOnlyMerged(MeshHandle handle, PropLayer layer, float dissolve, bool complement) => Merged(handle, layer, dissolve, complement, true);
    void Merged(MeshHandle handle, PropLayer layer, float dissolve, bool complement, bool hidden)
    {
        var mesh = _meshes[handle]; var vertices = mesh.Vertices.ToArray();
        var offset = new Vector3((float)ClusterAnchor.X, (float)ClusterAnchor.Y, (float)ClusterAnchor.Z);
        for (int i = 0; i < vertices.Length; i++) vertices[i].Position -= offset;
        Draws.Add(new(new(vertices, mesh.Indices32.ToArray()), Matrix4x4.CreateTranslation(offset), hidden,
            dissolve, complement ? 1 : 0, false, layer.CastsShadows, ClusterKey));
    }
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
    public PropClusterDrawCounts DrawWithVisibility(Vector3 focus, IPropClusterVisibility visibility) => _renderer.DrawWithVisibility(focus, visibility);
    public bool TryGetDrawState(PropClusterKey key, Vector3 focus, out PropClusterDrawState state) => _renderer.TryGetDrawState(key, focus, out state);
    public void Dispose() => _renderer.Dispose();
}
