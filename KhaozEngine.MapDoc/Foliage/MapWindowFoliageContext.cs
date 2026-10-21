using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>A window capture publishes one complete context or no context.</summary>
public sealed record MapWindowFoliageContextResult(MapFoliageStatus Status, MapWindowFoliageContext? Context, MapFoliageContextWork Work)
{
    internal IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}

/// <summary>Immutable foliage inputs for the generation chunks intersecting the captured draw window. Dependencies
/// outside that window are retained only to answer those chunks. No complete-document Resources property is exposed.</summary>
public sealed class MapWindowFoliageContext : IMapFoliageInputs
{
    readonly MapFoliageInputKernel _inputs;
    readonly HashSet<(string Layer, MapFoliageChunkKey Chunk)> _chunks;
    internal MapWindowFoliageContext(MapFoliageInputKernel inputs, MapSceneSource source, MapSceneScope scope, MapSceneWitness witness,
        IReadOnlyList<(MapFoliageLayer Layer, MapFoliageChunkKey Chunk)> chunks, MapFoliageContextWork work)
    {
        _inputs = inputs;
        _chunks = chunks.Select(c => (c.Layer.Id, c.Chunk)).ToHashSet();
        ClosureHash = source.ClosureHash;
        Witness = witness;
        Work = work;
        SnapshotId = MapCanonical.HashHex(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/window-foliage-context/1");
            w.WriteStringValue(source.SourceGeneration); w.WriteStringValue(source.ClosureHash); w.WriteStringValue(scope.Digest);
            w.WriteStringValue(inputs.SnapshotId); w.WriteEndArray();
        });
    }
    public string SnapshotId { get; }
    public string ClosureHash { get; }
    public MapSceneWitness Witness { get; }
    public MapFoliageContextWork Work { get; }
    MapFoliageSet? IMapFoliageInputs.Foliage => _inputs.Foliage;
    MapAssetClosure IMapFoliageInputs.Assets => _inputs.Assets;
    IReadOnlyList<MapSurfaceRef> IMapFoliageInputs.DeclaredSurfaces => _inputs.DeclaredSurfaces;
    IReadOnlyDictionary<string, MapEffectiveTransform> IMapFoliageInputs.Transforms => _inputs.Transforms;
    MapFoliageOwners IMapFoliageInputs.ClearanceOwners => _inputs.ClearanceOwners;
    MapScopedSurfaces IMapFoliageInputs.Acquire(MapSurfaceScope scope) => _inputs.Acquire(scope);
    IReadOnlyDictionary<string, MapPaintRegionSet> IMapFoliageInputs.PaintFor(MapSurfaceScope scope) => _inputs.PaintFor(scope);
    IReadOnlyDictionary<MapFoliageChunkKey, string> IMapFoliageInputs.DensityDigests(MapFoliageLayer layer) => _inputs.DensityDigests(layer);
    IReadOnlyList<MapSurfaceRef> IMapFoliageInputs.SupportFloorsFor(MapFoliageLayer layer, MapFoliageChunkKey chunk) => _inputs.SupportFloorsFor(layer, chunk);
    IReadOnlyList<MapResolvedPlacement> IMapFoliageInputs.PlacementsIn(MapFoliageRect bounds, out long candidates) => _inputs.PlacementsIn(bounds, out candidates);
    MapEffectiveTransform IMapFoliageInputs.Pose(MapPrefabReference owner) => _inputs.Pose(owner);
    void IMapFoliageInputs.RequireChunk(MapFoliageLayer layer, MapFoliageChunkKey chunk)
    {
        if (!_chunks.Contains((layer.Id, chunk))) throw MapFoliageDistribution.Refuse(MapFoliageStatus.Unavailable, "chunk lies outside the captured generation window");
    }
}
