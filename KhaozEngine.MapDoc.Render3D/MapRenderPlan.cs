using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Render3D;
using KhaozEngine.Primitives;
using KhaozEngine.MapDoc.Interiors;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>A complete immutable window inventory. Successful preparation takes the caller's selected input lease.</summary>
public sealed class MapRenderPlan : IDisposable
{
    readonly MapPreparedResource[] _resources;
    readonly MapDrawRecord[] _lights;
    MapRenderContextLease? _contextOwner;
    bool _disposed;
    internal MapPresentationPicker PresentationPicker { get; }
    public MapPresentationGeometry PresentationGeometry { get; }
    public MapRoofEvaluationBuffer RoofEvaluation { get; }
    internal IReadOnlyDictionary<string, int> RoofIndices { get; }
    public MapResourceStamp Key { get; }
    public MapPreparedInputLease Input { get; }
    public MapScopedScene Scene { get; }
    public MapPlacementRouting Routing { get; }
    public MapRenderInventory Inventory { get; }
    internal MapPreparationReservation? PreparationOwnership { get; private set; }
    public long PreparationBytes { get; private set; }
    internal MapWindowFoliageContext FoliageContext { get; }
    internal MapRenderContextLease? RetainContext() => _contextOwner?.Retain();
    internal IReadOnlyList<MapPreparedResource> Resources => _resources;
    MapRenderPlan(MapResourceStamp key, MapScopedScene scene, MapPreparedInputLease input, MapPlacementRouting routing,
        MapRenderInventory inventory, MapWindowFoliageContext context, long bytes, MapPreparedResource[] resources, MapDrawRecord[] lights, MapPresentationGeometry geometry, MapRoofEvaluationBuffer roofs, IReadOnlyDictionary<string, int> roofIndices)
    { Key = key; Scene = scene; Input = input; Routing = routing; Inventory = inventory; FoliageContext = context; PreparationBytes = bytes; _resources = resources; _lights = lights; PresentationGeometry = geometry; RoofEvaluation = roofs; RoofIndices = roofIndices; PresentationPicker = new(this); }
    public static MapRenderPlan Prepare(MapSceneSource source, MapScopedScene scene, MapPreparedInputLease input,
        MapRenderPolicy policy, long generation)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(scene);
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(source.Data.Limits);
        using var activation = admission.Enter();
        return Prepare(source, scene, input, policy, generation, admission, null);
    }
    // Nested roof preparation can share this allowance before any retained allocation.
    internal static MapRenderPlan Prepare(MapSceneSource source, MapScopedScene scene, MapPreparedInputLease input,
        MapRenderPolicy policy, long generation, MapSceneAdmission admission, MapWindowFoliageContext? captured,
        MapRenderContextLease? contextOwner = null, Func<long, IDisposable>? retainSamples = null, MapPlacementCellInventory? cells = null)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(policy);
        if (!ReferenceEquals(source, scene.Source) || !scene.Witness.CompleteForScope || input.ClosureHash != source.ClosureHash ||
            input.ProofDigest != source.RenderExtentsDigest || scene.Witness.RenderExtentsDigest != input.ProofDigest)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Stale, "Render plan requires its exact proof-bound source, scene and input.");
        using var preparation = admission.BeginPreparation();
        MapTerrainRenderBatch? terrain = null; MapPlacementRenderBatch? placements = null; MapNativeFoliageBatch? foliage = null; MapWaterRenderBatch? water = null;
        using var activation = admission.Enter();
        try
        {
            admission.ReserveBytes(1024);
            var routing = MapPlacementRouting.Create(scene, policy.Placements, policy.AnimatedAssets).Routing
                ?? throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Native render routing refused caller policy.");
            cells ??= MapPlacementCellInventory.Capture(scene, routing);
            var context = captured ?? CaptureFoliage(source, scene.Scope);
            terrain = NativeMapTerrain.Prepare(scene, input, policy.TerrainMaterials);
            placements = NativeMapPlacements.Prepare(scene, input, policy.Placements, routing, cells);
            using (var compiledWater = MapWaterScopedCompiler.Compile(scene, limits: source.Data.Limits.Preparation?.Water))
                water = NativeMapWater.Prepare(compiledWater, policy.Water.ToWaterLook(), new WaterSettings());
            foliage = NativeMapFoliage.Prepare(scene, context, input, policy.Foliage, routing, retainSamples);
            var geometry = MapPresentationGeometry.Prepare(scene, input.Assets.Closure, terrain.CanonicalFaces);
            var roofResult = MapRoofEvaluationBuffer.Prepare(scene, admission);
            var roofs = roofResult.Buffer ?? throw new MapSceneSourceException(roofResult.Status, roofResult.Detail ?? "Roof preparation refused.", roofResult.Capacity);
            admission.ReserveBytes(checked(128L + roofs.Roofs.Count * 128L));
            var roofIndices = roofs.Roofs.Select((r, i) => (r.RoofPlacementId, i)).ToDictionary(r => r.RoofPlacementId, r => r.i, StringComparer.Ordinal);
            var stamp = new MapResourceStamp(source.ClosureHash, source.SourceGeneration, scene.Scope.Digest, policy.Digest, generation);
            MapPreparedResource[] resources;
            MapDrawRecord[] lights;
            using (var recording = admission.BeginPreparation())
            {
                admission.ReserveBytes(checked(128L * scene.EffectiveLights.Count));
                var recorder = new MapDrawRecorder();
                terrain.Register(stamp, recorder); placements.Register(stamp, recorder); water.Register(stamp, recorder); foliage.Register(stamp, recorder);
                var point = new MapFramePoint(WorldFrame.Origin, default);
                NativeMapLights.Record(scene, new(WorldFrame.Origin, point, MapRoofMode.AlwaysVisible, 0, 0, point), recorder, stamp);
                var snapshot = recorder.Snapshot();
                admission.ReserveBytes(checked(IntPtr.Size * (long)snapshot.Resources.Count + MapRecordingAdmission.DrawBytes * snapshot.Draws.Count));
                resources = snapshot.Resources.ToArray(); lights = snapshot.Draws.ToArray();
                recording.Complete(checked(128L * resources.Length + MapRecordingAdmission.DrawBytes * lights.Length));
            }
            var inventory = new MapRenderInventory(scene.Witness, terrain, placements, MapEffectiveAnnotations.Lights(scene), water,
                foliage, MapEffectiveAnnotations.Markers(scene));
            var result = new MapRenderPlan(stamp, scene, input, routing, inventory, context, 0, resources, lights, geometry, roofs, roofIndices) { _contextOwner = contextOwner };
            long retained = checked(1024L + terrain.RetainedBytes + placements.RetainedBytes + water.RetainedBytes + foliage.RetainedBytes +
                geometry.RetainedBytes + roofs.RetainedBytes + 128L * roofIndices.Count + 128L * resources.Length + MapRecordingAdmission.DrawBytes * result._lights.Length +
                (captured is null ? context.InventoryBytes : 0));
            result.PreparationOwnership = preparation.Complete(retained - water.CanonicalPreparationBytes); result.PreparationBytes = retained;
            return result;
        }
        catch
        { foliage?.Dispose(); placements?.Dispose(); terrain?.Dispose(); water?.Dispose(); throw; }
    }
    internal static MapWindowFoliageContext CaptureFoliage(MapSceneSource source, MapSceneScope scope)
    {
        var result = MapFoliageGenerationContext.CaptureWindow(source, scope, source.Data.Limits.Preparation?.FoliageContext);
        if (result.Capacity is { } capacity) throw new MapSceneCapacityException(capacity.LimitName, capacity.Requested, capacity.Allowance);
        return result.Context ?? throw new MapRenderAssetRefusal(result.Status == MapFoliageStatus.CapacityExceeded
            ? MapRenderAssetStatus.CapacityExceeded : MapRenderAssetStatus.Invalid, "Window foliage dependencies refused preparation. " + string.Join("\n", result.Findings));
    }
    internal static string[] SelectedAssets(MapScopedScene scene, MapWindowFoliageContext context, MapPlacementCellInventory? cells = null) => scene.Placements.Select(p => p.AssetId)
        .Concat(cells?.Cells.SelectMany(c => c.Members.Select(p => p.AssetId)) ?? [])
        .Concat(scene.Scope.Channels.HasFlag(MapSceneChannels.Foliage)
            ? (((IMapFoliageInputs)context).Foliage?.Layers ?? []).Where(l => l.ChunkDigests.Count > 0).SelectMany(l => l.Models.Select(m => m.AssetId)) : [])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    internal static string[] SelectedMaterials(MapScopedScene scene, MapRenderPolicy policy)
    {
        if (!scene.Scope.Channels.HasFlag(MapSceneChannels.Terrain)) return [];
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        var surfaces = scene.Surfaces;
        var referenced = surfaces.Patches.Values.SelectMany(p => p.Cells).SelectMany(c => new[] { c.Underlay, c.Overlay })
            .Where(id => id != 0).Distinct().ToHashSet();
        foreach (var strip in surfaces.AllRecords().OfType<MapWallStrip>()) referenced.Add(strip.MaterialId);
        // Paint resources are acquired through the same canonical surface closure.
        foreach (var region in scene.PaintRegions.Values.SelectMany(p => p.Regions))
        { referenced.Add(region.Material.Underlay); referenced.Add(region.Material.Overlay); }
        preparation?.Reserve(checked(IntPtr.Size * (long)policy.TerrainMaterials.Count));
        var selected = policy.TerrainMaterials.Where(p => referenced.Contains(p.Key)).Select(p => p.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        preparation?.Complete(checked(IntPtr.Size * (long)selected.Length));
        return selected;
    }
    public void Register(IMapDrawSink sink)
    { ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(sink); foreach (var resource in _resources) sink.Register(resource); }
    public void Update(MapFrameState frame)
    { ObjectDisposedException.ThrowIf(_disposed, this); RoofEvaluation.Evaluate(frame.Observer, frame.RoofMode); Inventory.Placements.UpdateRoofs(frame); }
    public void Record(MapFrameState frame, IMapDrawSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(sink);
        Update(frame);
        Inventory.Terrain.Record(frame, sink); Inventory.Placements.Record(frame, sink); Inventory.Water.Record(frame, sink);
        Inventory.Foliage.Record(frame, sink);
        NativeMapRoofs.RecordEvaluated(this, frame, sink);
        foreach (var light in _lights) sink.Write(light with { LocalFocus = MapRenderTransform.FocusLocal(frame.Focus, light.Anchor) });
    }
    public void Dispose()
    { if (_disposed) return; _disposed = true; Inventory.Foliage.Dispose(); Inventory.Placements.Dispose(); Inventory.Terrain.Dispose(); Input.Dispose(); _contextOwner?.Dispose(); if (_contextOwner is null) MapFoliageRenderCache.For(FoliageContext).Clear(); Inventory.Water.Dispose(); PreparationOwnership?.Dispose(); Scene.PreparationOwnership?.Dispose(); }
}
