using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Physics;

public sealed record MapScopedWorldResult(MapSceneStatus Status, MapScopedWorld? World, MapSceneWitness Witness)
{
    public MapSceneCapacity? Capacity { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}

/// <summary>Physical descriptors complete only for their witnessed domain. This is never a complete built world.</summary>
public sealed class MapScopedWorld : IDisposable
{
    internal MapPreparationReservation? PreparationOwnership { get; set; }
    bool _disposed;
    internal void RequireAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    internal void Retain(MapPreparationReservation product)
    {
        RequireAlive();
        using var workspace = Admission.Workspace();
        if (PreparationOwnership is { } prior) workspace.Absorb(prior);
        workspace.Absorb(product);
        PreparationOwnership = workspace.Transfer(workspace.Bytes);
    }
    /// <summary>Releases this world's physical inventory and its derived navigation descriptors.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PreparationOwnership?.Dispose();
    }
    internal MapScopedWorld(MapScopedScene scene, MapSceneScope domain, IReadOnlyList<MapPlacementGeometry> placements, MapTerrainChunkSet terrain,
        IReadOnlyList<MapStaticDescriptor> statics, IReadOnlyList<MapCompiledWaterBody> water, MapSceneWitness witness)
    {
        Witness = witness with { WholeWorldComplete = false };
        Placements = placements; Terrain = terrain; Statics = statics; Water = water;
        Lights = MapEffectiveAnnotations.Lights(scene); Markers = MapEffectiveAnnotations.Markers(scene);
        Surfaces = scene.SurfaceView; Scope = domain; TileSize = scene.Source.Data.TileSize;
        Admission = scene.SurfaceView.Scope.SceneAdmission!;
        var physical = statics.Select(s => s.Bounds).Concat(water.Where(b => b.Bounds is not null).Select(b => b.Bounds!.Value));
        double? minY = null, maxY = null;
        foreach (var bounds in physical)
        {
            minY = minY is null ? bounds.MinY : Math.Min(minY.Value, bounds.MinY);
            maxY = maxY is null ? bounds.MaxY : Math.Max(maxY.Value, bounds.MaxY);
        }
        // Empty coverage keeps the requested vertical domain, never an invented floor at zero.
        Bounds = new(Scope.DrawBounds.MinX, minY ?? Scope.DrawBounds.MinY, Scope.DrawBounds.MinZ,
            Scope.DrawBounds.MaxX, maxY ?? Scope.DrawBounds.MaxY, Scope.DrawBounds.MaxZ);
    }
    public MapSceneWitness Witness { get; }
    public IReadOnlyList<MapPlacementGeometry> Placements { get; }
    public IReadOnlyList<MapStaticDescriptor> Statics { get; }
    public MapTerrainChunkSet Terrain { get; }
    public IReadOnlyList<MapEffectiveLight> Lights { get; }
    public IReadOnlyList<MapEffectiveMarker> Markers { get; }
    public IReadOnlyList<MapCompiledWaterBody> Water { get; }
    public string SourceGeneration => Witness.SourceGeneration;
    public string ScopeIdentity => Witness.ScopeDigest;
    public MapBox3 Bounds { get; }
    /// <summary>The storage tile edge captured with the source generation, in metres.</summary>
    public float TileSize { get; }
    public MapScopedSurfaces Surfaces { get; }
    internal MapSceneScope Scope { get; }
    internal MapSceneAdmission Admission { get; }
}

/// <summary>Reuses the released physical kernels over an admitted neutral acquisition.</summary>
public static class MapScopedWorldBuilder
{
    public static MapScopedWorldResult Build(MapScopedScene scene, MapAssetClosure assets, MapWorldBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        var witness = scene.Witness with { WholeWorldComplete = false };
        try
        {
            MapWorldBuilder.ValidateOptions(options);
            if (options.Resolve.ResolverVersion != 2 || assets.Hash != witness.ClosureHash)
                throw new ArgumentException("Scoped physics needs native options and the captured asset closure.");
            if (!witness.CompleteForScope || scene.SurfaceView.Status != MapAcquireStatus.Complete)
                return new(MapSceneStatus.Incomplete, null, witness with { CompleteForScope = false });
            var admission = scene.SurfaceView.Scope.SceneAdmission
                ?? throw new ArgumentException("Scoped physics needs an admitted scene.");
            using var admitted = admission.Enter();
            using var preparation = admission.BeginPreparation();
            bool ownsSceneView = false;
            bool ownsWitness = false;
            MapSceneWitness Extend(MapSceneWitness dependency)
            {
                admission.ReserveBytes(checked(MapPhysicsInventory.Witness(witness) + MapPhysicsInventory.Witness(dependency)));
                ownsWitness = true;
                return MapRequiredDomainCoverage.Extend(witness, dependency);
            }
            var domain = scene.Scope;
            var rectangle = domain.Surfaces.WorldRectangle();
            // Draw bounds remain the partition's cell. Acquire the full physical capture separately, including
            // placements and their dependencies in the seam margin, under this same preparation allowance.
            admission.ReserveBytes(512);
            var dependencyScope = domain with
            {
                DrawBounds = domain.DrawBounds with
                {
                    MinX = Math.Min(domain.DrawBounds.MinX, rectangle.MinX.ToDouble()),
                    MinZ = Math.Min(domain.DrawBounds.MinZ, rectangle.MinZ.ToDouble()),
                    MaxX = Math.Max(domain.DrawBounds.MaxX, rectangle.MaxX.ToDouble()),
                    MaxZ = Math.Max(domain.DrawBounds.MaxZ, rectangle.MaxZ.ToDouble()),
                },
                Channels = domain.Channels | MapSceneChannels.Terrain | MapSceneChannels.Placements |
                    MapSceneChannels.Water | MapSceneChannels.Annotations | MapSceneChannels.Rooms,
            };
            if (dependencyScope != domain)
            {
                var dependency = MapSceneAcquisition.Run(scene.Source, dependencyScope, admission);
                witness = Extend(dependency.Witness);
                if (dependency.Scene is null)
                    return new(dependency.Status, null, witness with { CompleteForScope = false }) { Capacity = dependency.Capacity, Findings = dependency.Findings };
                scene = dependency.Scene;
                ownsSceneView = true;
            }
            // Account descriptor containers before preparation. Canonical compilation charges its own face demand.
            admission.ReserveBytes(checked(512L * (scene.Placements.Count + scene.SurfaceView.Witness.Present.Count)));
            var scope = scene.Scope.Surfaces;
            bool MeetsDomain(MapBox3 bounds) => bounds.MaxX >= rectangle.MinX.ToDouble() && bounds.MinX <= rectangle.MaxX.ToDouble() &&
                bounds.MaxZ >= rectangle.MinZ.ToDouble() && bounds.MinZ <= rectangle.MaxZ.ToDouble() &&
                (scope.MinY is null || bounds.MaxY >= scope.MinY) && (scope.MaxY is null || bounds.MinY <= scope.MaxY);
            using var compiled = MapTerrainPhysics.Compile(scene.SurfaceView, options.Chunks);
            admission.ReserveBytes(checked(128L + compiled.Chunks.Count * 8L));
            var terrain = new MapTerrainChunkSet(Array.AsReadOnly(compiled.Chunks.Where(c => MeetsDomain(c.Bounds)).ToArray()),
                compiled.Witness, compiled.LegacyFallbackCellsSkipped);
            var placements = MapPlacementShapes.Prepare(scene.Placements, assets, scene.Transforms);
            IReadOnlyList<MapCompiledWaterBody> water = Array.Empty<MapCompiledWaterBody>();
            if (scene.Water is { Bodies.Count: > 0 } definition)
            {
                var dependency = WaterDependencies(scene, admission);
                witness = Extend(dependency.Witness);
                if (dependency.Scene is null)
                    return new(dependency.Status, null, witness with { CompleteForScope = false }) { Capacity = dependency.Capacity, Findings = dependency.Findings };
                // The released compiler requires an unfiltered, fully held footprint window for each body.
                water = MapWaterCompiler.Compile(dependency.Scene.SurfaceView, definition);
            }
            foreach (var body in water)
            {
                if (body.Status == MapWaterCompileStatus.CapacityExceeded)
                    throw new MapSceneSourceException(MapSceneStatus.CapacityExceeded, body.Detail ?? "Water compilation refused capacity.", body.Capacity);
                if (body.Status == MapWaterCompileStatus.Incomplete)
                    throw new MapSceneSourceException(MapSceneStatus.Incomplete, body.Detail ?? "Incomplete water dependencies.");
                if (body.Status == MapWaterCompileStatus.NotRepresentable)
                    throw new MapSceneSourceException(MapSceneStatus.NotRepresentable, body.Detail ?? "Water is not representable.");
                MapWorldBuilder.RequireWaterBody(body);
            }
            // Neutral discovery can retain distant cosmetic extents and dependency layers. Only physical bounds
            // meeting the capture domain contribute statics or navigation heights.
            admission.ReserveBytes(checked(256L + 16L * placements.Count + 256L * (placements.Count + terrain.Chunks.Count)));
            var physical = placements.Where(p => MeetsDomain(p.ColliderBounds ?? p.Envelope.Bounds)).ToArray();
            admission.ReserveBytes(checked(128L + water.Count * 8L));
            water = Array.AsReadOnly(water.Where(b => b.Bounds is not { } bounds || MeetsDomain(bounds)).ToArray());
            var statics = MapWorldBuilder.Statics(physical, terrain);
            var owners = statics.Select(s => s.OwnerId).ToHashSet(StringComparer.Ordinal);
            foreach (var body in water)
                if (owners.Contains(MapResidencyOwnership.WaterOwnerId(body.BodyId)))
                    throw new MapDocumentException("Water and statics share an owner.");
            var world = new MapScopedWorld(scene, domain, physical, terrain, statics, water, witness);
            long retained = new MapPhysicsInventory().World(world);
            if (ownsWitness) retained = checked(retained + MapPhysicsInventory.Witness(witness));
            if (ownsSceneView)
                retained = checked(retained + new MapSceneInventory().View(scene.SurfaceView) +
                    128L * (world.Lights.Count + world.Markers.Count) + world.Markers.Sum(m => 8L * m.Tags.Count));
            world.PreparationOwnership = preparation.Complete(retained);
            return new(MapSceneStatus.CompleteForScope, world, witness);
        }
        catch (Exception error) when (MapSceneSource.Classify(error) is not null)
        {
            return new(Enum.Parse<MapSceneStatus>(MapSceneSource.Classify(error)!.Value.ToString()), null,
                witness with { CompleteForScope = false, WholeWorldComplete = false })
            { Capacity = MapSceneCapacity.From(error), Findings = [error.Message] };
        }
    }

    static MapScopedSceneResult WaterDependencies(MapScopedScene scene, MapSceneAdmission admission)
    {
        var request = scene.Scope.Surfaces;
        var rectangle = request.WorldRectangle();
        double minX = rectangle.MinX.ToDouble(), minZ = rectangle.MinZ.ToDouble();
        double maxX = rectangle.MaxX.ToDouble(), maxZ = rectangle.MaxZ.ToDouble();
        long bytes = 512;
        foreach (var body in scene.Water!.Bodies)
        {
            if (!scene.Source.Data.MetadataByKey.TryGetValue(new("WaterBody", body.Id), out var metadata))
                throw new MapSceneSourceException(MapSceneStatus.Missing, "Water has no attested dependency domain.");
            minX = Math.Min(minX, metadata.Bounds.MinX); minZ = Math.Min(minZ, metadata.Bounds.MinZ);
            maxX = Math.Max(maxX, metadata.Bounds.MaxX); maxZ = Math.Max(maxZ, metadata.Bounds.MaxZ);
            bytes = checked(bytes + 512L + body.Domain.Rects.Count * 64L + body.Covers.Count * 64L);
            foreach (var ring in body.Domain.Rings) bytes = checked(bytes + 128L + ring.Vertices.Count * 32L);
        }
        admission.ReserveBytes(bytes);
        double anchorX = (double)request.Frame.X * WorldFrame.Grid, anchorZ = (double)request.Frame.Z * WorldFrame.Grid;
        var dependency = scene.Scope with
        {
            Surfaces = request with
            {
                LocalMin = new Vector2(Outward(minX - anchorX, false), Outward(minZ - anchorZ, false)),
                LocalMax = new Vector2(Outward(maxX - anchorX, true), Outward(maxZ - anchorZ, true)),
                MinY = null,
                MaxY = null,
                Roles = Enum.GetValues<MapSurfaceRole>(),
                SpaceIds = null,
            },
            Channels = MapSceneChannels.Water | MapSceneChannels.Rooms,
        };
        return MapSceneAcquisition.Run(scene.Source, dependency, admission);

        static float Outward(double value, bool upper)
        {
            float result = (float)value;
            return upper && result < value ? MathF.BitIncrement(result) : !upper && result > value ? MathF.BitDecrement(result) : result;
        }
    }

}
