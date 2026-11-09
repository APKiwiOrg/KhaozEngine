using KhaozEngine.Diagnostics;
using KhaozEngine.Primitives;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Bounds = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.Bounds;
using SpatialIndex = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.SpatialIndex;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Derives density, support and owner dependencies before entering the scene's single admission path.</summary>
internal static class MapFoliageWindowCapture
{
    internal static MapWindowFoliageContextResult Capture(MapSceneSource source, MapSceneScope scope, MapFoliageContextLimits limits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxSourceUtf8Bytes < 0 || limits.MaxChunkDescriptors < 0 || limits.MaxPlacements < 0 ||
            limits.MaxSurfacePatches < 0 || limits.MaxPaintTargets < 0) throw new ArgumentOutOfRangeException(nameof(limits));
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(source.Data.Limits, foliageLimits: limits);
        using var activation = admission.Enter();
        using var preparation = admission.BeginPreparation();
        var work = new MapFoliageContextWork(0, 0, 0, 0, 0, 0, 0);
        long expansions = MapPrefabResolver.ThreadExpansions, writes = MapFoliageJson.ThreadPayloadWrites;
        try
        {
            scope.Validate();
            MapSceneSource.CheckManifest(source.Data.Directory, source.Data.ManifestDigest);
            var metadata = source.Data.FoliageMetadata;
            var chunks = new List<(MapFoliageLayer Layer, MapFoliageChunkKey Chunk)>();
            var owners = new HashSet<MapSceneResourceKey>();
            var seats = new Dictionary<MapSceneResourceKey, MapSurfaceScope>();
            Bounds? bounds = null;
            var b = scope.DiscoveryBounds;
            foreach (var layer in source.Data.Foliage?.Layers ?? [])
            {
                foreach (var key in MapFoliageWindowChunks.Discover(layer, source.Data.Assets, b))
                {
                    work = work with { ChunkDescriptors = checked(work.ChunkDescriptors + 1) };
                    if (DemandDiagnostics.Check(source, "MapFoliageWindowCapture.MaxChunkDescriptors.1", "MaxChunkDescriptors", work.ChunkDescriptors - 1, 1, limits.MaxChunkDescriptors, work.ChunkDescriptors > limits.MaxChunkDescriptors, CapacityDemandLifetime.Cumulative, allowanceSource: limits)) throw new MapSceneCapacityException(nameof(MapFoliageContextLimits.MaxChunkDescriptors), work.ChunkDescriptors, limits.MaxChunkDescriptors);
                    admission.ReserveBytes(128);
                    chunks.Add((layer, key));
                    if (MapFoliageDistribution.KnownOffRaster(layer, MapFoliageDistribution.Rectangle(layer, key))) continue;
                    var read = MapFoliageSupport.Scope(layer, key, metadata.FloorsFor(layer, key), metadata.Transforms).WorldRectangle();
                    bounds = Bounds.Union(bounds, Bounds.Of(read));
                    foreach (var owner in metadata.OwnersFor(layer, read))
                    {
                        owners.Add(owner);
                        if (metadata.SeatFor(layer, owner) is { } seat) seats.TryAdd(owner, seat with { Limits = scope.Surfaces.Limits });
                    }
                    Binding(layer.Support);
                }
                void Binding(MapSupportBinding binding)
                {
                    if (binding.SurfaceId is { } id) owners.Add(new("Surface", id));
                    if (binding.SpaceId is { } space) owners.Add(new("Record", space));
                }
            }
            // Source knowledge stays borrowed. Only the selected density payloads and effective dependency product are retained.
            var requested = new MapFoliageWindowRequest(chunks, owners, seats.Values.ToArray());
            Bounds r = bounds ?? new(b.MinX, b.MinZ, b.MaxX, b.MaxZ);
            var frame = WorldFrame.Nearest((float)((r.MinX + r.MaxX) / 2), (float)((r.MinZ + r.MaxZ) / 2));
            double ax = (double)frame.X * WorldFrame.Grid, az = (double)frame.Z * WorldFrame.Grid;
            var surfaces = new MapSurfaceScope(frame, new(MapFoliageSupport.Outward(r.MinX - ax, true), MapFoliageSupport.Outward(r.MinZ - az, true)),
                new(MapFoliageSupport.Outward(r.MaxX - ax, false), MapFoliageSupport.Outward(r.MaxZ - az, false)), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, scope.Surfaces.Limits);
            var dependencyScope = new MapSceneScope(surfaces, new(r.MinX, scope.DrawBounds.MinY, r.MinZ, r.MaxX, scope.DrawBounds.MaxY, r.MaxZ),
                bounds is null ? 0 : MapSceneChannels.Terrain | MapSceneChannels.Rooms | MapSceneChannels.Annotations);
            var acquired = MapSceneAcquisition.Run(source, dependencyScope, admission, requested);
            work = work with
            {
                SourceUtf8Bytes = admission.FoliageSourceUtf8Bytes,
                ChunkDescriptors = Math.Max(work.ChunkDescriptors, admission.FoliageChunks),
                Placements = admission.FoliagePlacements,
                SurfacePatches = admission.FoliagePatches,
                PaintTargets = admission.FoliagePaintTargets
            };
            if (acquired.Scene is not { } scene) return new(Status(acquired.Status), null, Observed()) { Capacity = acquired.Capacity, Findings = acquired.Findings };
            // The admitted effective context owns every acquired patch. Retained reads never return to the live producer.
            var kernel = new MapFoliageInputKernel(scene.CapturedFoliage, source.Data.Assets, scene.Product, scene.Product.SurfaceSource, metadata, scope.Surfaces.Limits);
            work = Observed();
            var witness = acquired.Witness with { ScopeDigest = scope.Digest, WholeWorldComplete = false };
            MapSceneSource.CheckManifest(source.Data.Directory, source.Data.ManifestDigest);
            var context = new MapWindowFoliageContext(kernel, source, scope, witness, chunks, work);
            context.PreparationOwnership = preparation.Complete(context.InventoryBytes);
            return new(MapFoliageStatus.Complete, context, work);
        }
        catch (MapFoliageRefusal refusal) { return new(refusal.Status, null, Observed()); }
        catch (Exception error) when (MapSceneSource.Classify(error) is not null)
        {
            return new(Status(Enum.Parse<MapSceneStatus>(MapSceneSource.Classify(error)!.Value.ToString())), null, Observed())
            { Capacity = MapSceneCapacity.From(error), Findings = [error.Message] };
        }
        MapFoliageContextWork Observed() => work with
        {
            Expansions = MapPrefabResolver.ThreadExpansions - expansions,
            SnapshotSerializations = MapFoliageJson.ThreadPayloadWrites - writes,
        };
    }

    static MapFoliageStatus Status(MapSceneStatus status) => status switch
    {
        MapSceneStatus.CapacityExceeded => MapFoliageStatus.CapacityExceeded,
        MapSceneStatus.NotRepresentable => MapFoliageStatus.NotRepresentable,
        MapSceneStatus.Invalid => MapFoliageStatus.Invalid,
        _ => MapFoliageStatus.Unavailable,
    };
}

internal sealed record MapFoliageWindowRequest(IReadOnlyList<(MapFoliageLayer Layer, MapFoliageChunkKey Chunk)> Chunks,
    IReadOnlySet<MapSceneResourceKey> Owners, IReadOnlyList<MapSurfaceScope> Seats);

/// <summary>Source-generation metadata only. Floor and clearance proxies have no retained world payloads.</summary>
internal sealed class MapFoliageWindowMetadata
{
    MapFoliageSet? _foliage;
    readonly List<MapSurfaceRef> _floors = new();
    readonly List<Bounds> _floorBounds = new();
    readonly Dictionary<string, MapEffectiveTransform> _transforms = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(MapSceneResourceKey Key, Bounds Bounds)>> _owners = new(StringComparer.Ordinal);
    readonly Dictionary<MapSceneResourceKey, MapEffectiveTransform> _exceptionalPoses = new();
    readonly Dictionary<MapSceneResourceKey, Bounds> _placementProxies = new();
    readonly Dictionary<string, List<Bounds>> _maskProxies = new(StringComparer.Ordinal);
    readonly Dictionary<string, SpatialIndex> _maskIndices = new(StringComparer.Ordinal);
    MapSceneResourceKey[] _placementKeys = Array.Empty<MapSceneResourceKey>();
    SpatialIndex _placementIndex = new(Array.Empty<Bounds>());
    SpatialIndex _floorIndex = new(Array.Empty<Bounds>());
    readonly Dictionary<string, SpatialIndex> _ownerIndices = new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, MapEffectiveTransform> Transforms => _transforms;

    internal MapFoliageWindowMetadata(MapFoliageSet? foliage) => _foliage = foliage;
    internal void AddFloor(MapSurfaceRef surface, MapCellRect cells, MapEffectiveTransform pose, MapSceneScan scan)
    {
        if (_foliage is null || surface.Role != MapSurfaceRole.SupportFloor) return;
        scan.Retain(256 + 2L * surface.Id.Length);
        _floors.Add(surface);
        var transform = new MapResourceTransform(pose);
        var corners = new[] { MapLatticeAddress.Corner(cells.MinX, cells.MinZ), MapLatticeAddress.Corner(cells.MaxXExclusive, cells.MinZ),
            MapLatticeAddress.Corner(cells.MaxXExclusive, cells.MaxZExclusive), MapLatticeAddress.Corner(cells.MinX, cells.MaxZExclusive) };
        _floorBounds.Add(Bounds.Of(MapEffectiveGeometry.Bounds(corners.Select(a =>
        {
            var local = surface.Frame.WorldXz(a);
            var world = transform.Point(new(local.X, default, local.Z));
            return new MapExactXz(world.X, world.Z);
        }))));
        _transforms.TryAdd(MapEffectiveContext.Key(MapPrefabChildKind.Surface, surface.Id), pose);
    }

    internal void Add(MapPrefabChild child, MapEffectiveTransform pose, MapSceneScan scan)
    {
        if (_foliage is null) return;
        if (child is MapPrefabSurfaceChild surface)
            foreach (var patch in surface.Patches)
            {
                long x = checked(patch.Key.SlotX * 64 + patch.CellMinX), z = checked(patch.Key.SlotZ * 64 + patch.CellMinZ);
                AddFloor(surface.Surface, new(x, z, checked(x + patch.Width), checked(z + patch.Depth)), pose, scan);
            }
        if (child is MapPrefabMarkerChild marker)
        {
            var p = marker.Value.Position;
            var world = new MapResourceTransform(pose).Point(new(MapExactValue.FromSingle(p.X), MapExactValue.FromSingle(p.Y), MapExactValue.FromSingle(p.Z)));
            pose = pose with { X = world.X.ToDouble(), Y = world.Y.ToDouble(), Z = world.Z.ToDouble(), YawRadians = pose.YawRadians + marker.Value.YawRadians };
        }
        if (child is MapPrefabPlacementChild { Value.Y: not null } or MapPrefabMarkerChild { Value.HeightMode: not MapMarkerHeightMode.Support })
            AddExceptionalPose(child.Key, pose, scan);
        AddOwner(child.Key, (child as MapPrefabPlacementChild)?.Value, pose, scan);
    }

    internal void AddNative(MapPrefabChildKey owner, MapEffectiveTransform pose, MapSceneScan scan)
    {
        if (_foliage is null) return;
        AddExceptionalPose(owner, pose, scan);
        AddOwner(owner, null, pose, scan);
    }

    void AddOwner(MapPrefabChildKey owner, MapPlacement? placement, MapEffectiveTransform pose, MapSceneScan scan)
    {
        if (_foliage is null) return;
        foreach (var layer in _foliage.Layers)
        {
            Bounds? proxy = null;
            if (placement is not null)
                foreach (var rule in layer.ClearanceRules)
                    if (rule.AssetId == placement.AssetId && (rule.PlacementTag is not { } tag ||
                        placement.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))))
                        proxy = Bounds.Union(proxy, MapFoliageGenerationContext.Proxy(pose, rule.Footprint,
                            rule.Kind == MapFoliageClearanceKind.Door ? layer.DoorClearance : null));
            foreach (var mask in layer.ExceptionMasks)
                if (mask.Owner.Kind == owner.Kind && mask.Owner.Id == owner.Key)
                {
                    Bounds maskProxy = MapFoliageGenerationContext.Proxy(pose, mask.Footprint,
                        mask.Kind == MapFoliageClearanceKind.Door ? layer.DoorClearance : null);
                    proxy = Bounds.Union(proxy, maskProxy);
                    scan.Retain(96);
                    if (!_maskProxies.TryGetValue(layer.Id, out var masks)) _maskProxies.Add(layer.Id, masks = new());
                    masks.Add(maskProxy);
                }
            if (proxy is not { } value) continue;
            scan.Retain(192 + 2L * (layer.Id.Length + owner.Key.Length));
            if (!_owners.TryGetValue(layer.Id, out var list)) _owners.Add(layer.Id, list = new());
            var key = new MapSceneResourceKey(owner.Kind.ToString(), owner.Key);
            list.Add((key, value));
            if (placement is not null)
                _placementProxies[key] = Bounds.Union(_placementProxies.TryGetValue(key, out var old) ? old : null, value);
        }
    }

    void AddExceptionalPose(MapPrefabChildKey owner, MapEffectiveTransform pose, MapSceneScan scan)
    {
        if (_foliage?.Layers.Any(l => l.OwnerSupports.Any(o => o.Owner.Kind == owner.Kind && o.Owner.Id == owner.Key)) != true) return;
        scan.RetainBytes(checked(128 + 2L * owner.Key.Length));
        _exceptionalPoses.Add(new(owner.Kind.ToString(), owner.Key), pose);
    }

    internal MapSurfaceScope? SeatFor(MapFoliageLayer layer, MapSceneResourceKey owner)
    {
        if (!layer.OwnerSupports.Any(o => o.Owner.Kind.ToString() == owner.Kind && o.Owner.Id == owner.Id) ||
            !_exceptionalPoses.TryGetValue(owner, out var pose)) return null;
        return MapFoliageOwners.SeatScope(new MapResourceTransform(pose).Point(new(default, default, default)));
    }

    internal void Finish()
    {
        // The builder input can own resident density. Finished knowledge retains only its metadata indexes.
        _foliage = null;
        _floorIndex = new(_floorBounds);
        _placementKeys = _placementProxies.Keys.ToArray();
        _placementIndex = new(_placementKeys.Select(k => _placementProxies[k]).ToArray());
        foreach (var (id, values) in _maskProxies) _maskIndices.Add(id, new(values));
        foreach (var (id, values) in _owners) _ownerIndices.Add(id, new(values.Select(v => v.Bounds).ToArray()));
    }
    internal IReadOnlyList<MapSurfaceRef> FloorsFor(MapFoliageLayer layer, MapFoliageChunkKey chunk) =>
        MapFoliageSupport.FloorsFor(layer, chunk, _transforms, bounds => _floorIndex.Query(bounds, out _).Select(i => _floors[i]));
    internal long PlacementCandidates(Bounds rect)
    {
        _placementIndex.Query(rect, out long candidates);
        return candidates;
    }
    internal long MaskCandidates(string layer, MapFoliageRect rect)
    {
        if (!_maskIndices.TryGetValue(layer, out var index)) return 0;
        index.Query(new(rect.MinX, rect.MinZ, rect.MaxX, rect.MaxZ), out long candidates);
        return candidates;
    }
    internal IEnumerable<MapSceneResourceKey> OwnersFor(MapFoliageLayer layer, MapExactRect bounds)
    {
        foreach (int i in _placementIndex.Query(Bounds.Of(bounds), out _)) yield return _placementKeys[i];
        if (!_ownerIndices.TryGetValue(layer.Id, out var index)) yield break;
        foreach (int i in index.Query(Bounds.Of(bounds), out _))
        {
            var key = _owners[layer.Id][i].Key;
            yield return key;
            foreach (var binding in layer.OwnerSupports.Where(o => o.Owner.Kind.ToString() == key.Kind && o.Owner.Id == key.Id).Select(o => o.Support)
                .Concat(layer.ExceptionMasks.Where(m => m.Owner.Kind.ToString() == key.Kind && m.Owner.Id == key.Id).Select(m => m.Support)))
            {
                if (binding.SurfaceId is { } id) yield return new("Surface", id);
                if (binding.SpaceId is { } space) yield return new("Record", space);
            }
        }
    }
}
