using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>A neutral immutable scope snapshot. It makes no complete-document resource claim.</summary>
public sealed class MapScopedScene
{
    internal MapPreparationReservation? PreparationOwnership { get; set; }
    internal long InventoryBytes
    {
        get
        {
            var inventory = new MapSceneInventory();
            return checked(512L + 4L * Scope.Surfaces.Roles.Count + 8L * (Scope.Surfaces.SpaceIds?.Count ?? 0) +
                inventory.Product(_effective) + inventory.Surfaces(_surfaces) + inventory.View(SurfaceView) +
                64L * (Witness.Required.Count + Witness.Present.Count + Witness.KnownEmpty.Count + Witness.Unavailable.Count) +
                (_foliage?.Layers.Sum(l => MapSceneResourceBounds.FoliageMetadataBytes(l) +
                    l.Chunks.Count * (long)MapFoliageChunk.ByteLength) ?? 0));
        }
    }
    readonly MapSceneSource _source;
    readonly MapEffectiveResolutionProduct _effective;
    internal MapEffectiveResolutionProduct Product => _effective;
    readonly MapSurfaceSet _surfaces;
    readonly MapFoliageSet? _foliage;
    internal MapScopedScene(MapSceneSource source, MapSceneScope scope, MapSceneWitness witness,
        MapEffectiveResolutionProduct effective, MapScopedSurfaces view, MapFoliageSet? foliage)
    {
        _source = source; Scope = scope; Witness = witness; _effective = effective; SurfaceView = view; _foliage = foliage;
        _surfaces = effective.Surfaces;
        foreach (var surface in view.Surfaces) if (!_surfaces.Refs.Exists(s => s.Id == surface.Id)) _surfaces.Refs.Add(surface);
        foreach (var patch in view.Witness.Present) if (!_surfaces.Patches.ContainsKey(patch.Key)) _surfaces.Patches.Add(patch.Key, view.Patch(patch.Key).Patch!);
    }
    public MapSceneScope Scope { get; }
    public MapSceneWitness Witness { get; }
    public MapSurfaceSet Surfaces => _surfaces.Clone();
    public MapScopedSurfaces SurfaceView { get; }
    public IReadOnlyList<MapResolvedPlacement> Placements => _effective.Placements;
    public IReadOnlyDictionary<string, MapEffectiveTransform> Transforms => _effective.Transforms;
    public IReadOnlyDictionary<string, MapPaintRegionSet> PaintRegions => _effective.PaintRegions;
    public MapWaterSet? Water => _effective.Water;
    public MapBuildingIntent BuildingIntent => _effective.BuildingIntent;
    internal MapBuildingIntent CapturedBuildingIntent => _effective.CapturedBuildingIntent;
    internal MapFoliageSet? CapturedFoliage => _foliage;
    public MapFoliageSet? Foliage => _foliage?.Clone();
    internal IReadOnlyList<MapEffectiveLight> EffectiveLights => _effective.EffectiveLights;
    internal IReadOnlyList<MapEffectiveMarker> EffectiveMarkers => _effective.EffectiveMarkers;
    internal MapSceneSource Source => _source;
    public MapScopedSceneResult AcquireDependencies(MapSceneScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var prior = Scope.Surfaces.Limits;
        var requested = scope.Surfaces.Limits;
        var priorRefinement = prior.Refinement ?? new MapRefinementLimits();
        var requestedRefinement = requested.Refinement ?? new MapRefinementLimits();
        if (requested.MaxCandidatePatches > prior.MaxCandidatePatches || requested.MaxInspectedFaces > prior.MaxInspectedFaces ||
            requested.MaxSupportIntersections > prior.MaxSupportIntersections || requested.MaxPageReads > prior.MaxPageReads ||
            requested.MaxRecordReads > prior.MaxRecordReads || requested.MaxRecordDepth > prior.MaxRecordDepth ||
            requestedRefinement.MaxSourceFacesPerBound > priorRefinement.MaxSourceFacesPerBound ||
            requestedRefinement.MaxPairChecks > priorRefinement.MaxPairChecks ||
            requestedRefinement.MaxRefinementFaces > priorRefinement.MaxRefinementFaces ||
            requestedRefinement.MaxRefinementVertices > priorRefinement.MaxRefinementVertices ||
            requestedRefinement.MaxContextPatches > priorRefinement.MaxContextPatches ||
            requestedRefinement.MaxContextFaces > priorRefinement.MaxContextFaces)
            return new(MapSceneStatus.Invalid, null, Witness with
            {
                ScopeDigest = scope.Digest,
                Required = Array.Empty<MapSceneResourceKey>(),
                Present = Array.Empty<MapSceneResourceKey>(),
                KnownEmpty = Array.Empty<MapSceneResourceKey>(),
                Unavailable = Array.Empty<MapSceneResourceKey>(),
                CompleteForScope = false,
                WholeWorldComplete = false
            }, new(0, 0, 0, 0, 0, 0, 0, 0))
            { Findings = Array.AsReadOnly(new[] { "Dependency acquisition cannot widen captured query budgets." }) };
        var admission = MapSceneAdmission.Current;
        using var diagnostics = admission?.AcquisitionDiagnostics?.Enter(MapAcquisitionStage.Dependency, MapAcquisitionCaller.Dependency);
        admission?.AcquisitionDiagnostics?.Scene(MapAcquisitionCaller.Dependency);
        return MapSceneAcquisition.Run(_source, scope, admission);
    }
}
