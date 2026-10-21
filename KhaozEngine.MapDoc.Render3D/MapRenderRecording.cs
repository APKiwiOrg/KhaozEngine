using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Render3D;

public readonly record struct MapDrawRecord(MapResourceReference Resource, MapDrawChannels Channels,
    MapSubmissionAnchor Anchor, Matrix4x4 LocalTransform, Vector3 LocalFocus, MapRigidFrame? Rigid,
    MapLightFrame? Light, MapWaterFrame? Water, MapFoliageFrame? Foliage);

public interface IMapDrawSink
{
    void Register(MapPreparedResource resource);
    void Write(in MapDrawRecord draw);
}

public sealed class MapDrawSet
{
    public IReadOnlyList<MapPreparedResource> Resources { get; }
    public IReadOnlyList<MapDrawRecord> Draws { get; }
    public MapDrawSet(IReadOnlyList<MapPreparedResource> resources, IReadOnlyList<MapDrawRecord> draws)
    {
        ArgumentNullException.ThrowIfNull(resources); ArgumentNullException.ThrowIfNull(draws);
        Resources = Array.AsReadOnly(resources.ToArray()); Draws = Array.AsReadOnly(draws.ToArray());
    }
}

/// <summary>Records complete typed submissions without creating a device or resolving a policy by digest.</summary>
public sealed class MapDrawRecorder : IMapDrawSink
{
    readonly Dictionary<MapResourceReference, MapPreparedResource> _resources = new();
    readonly List<MapDrawRecord> _draws = [];
    public void Register(MapPreparedResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource); ArgumentNullException.ThrowIfNull(resource.Payload);
        bool matches = resource.Payload switch
        {
            MapLightResource => resource.Reference.Key.Kind == MapRenderKind.Light,
            MapWaterResource => resource.Reference.Key.Kind == MapRenderKind.Water,
            MapFoliageResource => resource.Reference.Key.Kind == MapRenderKind.Foliage,
            MapTerrainResource => resource.Reference.Key.Kind == MapRenderKind.Terrain,
            MapRigidResource => resource.Reference.Key.Kind is MapRenderKind.Terrain or MapRenderKind.Placement or MapRenderKind.Roof or MapRenderKind.Marker or MapRenderKind.Foliage,
            _ => false
        };
        if (!matches || resource.Reference.Stamp is null) throw new ArgumentException("Registration kind does not match its payload.", nameof(resource));
        if (resource.Payload is MapFoliageResource foliage)
            foreach (var model in foliage.Models)
                foreach (var part in model.Parts)
                    if (!_resources.TryGetValue(part, out var mesh) || mesh.Payload is not MapRigidResource)
                        throw new InvalidOperationException("Foliage model part has no rigid registration.");
        if (_resources.TryGetValue(resource.Reference, out var existing))
        {
            if (existing.PayloadDigest != resource.PayloadDigest || existing.Bounds != resource.Bounds)
                throw new ArgumentException("A registered reference cannot change its prepared payload.", nameof(resource));
            return;
        }
        _resources.Add(resource.Reference, resource);
    }

    public void Write(in MapDrawRecord draw)
    {
        if (!_resources.TryGetValue(draw.Resource, out var resource)) throw new InvalidOperationException("Draw references an unregistered resource.");
        int frames = (draw.Rigid.HasValue ? 1 : 0) + (draw.Light.HasValue ? 1 : 0) +
            (draw.Water.HasValue ? 1 : 0) + (draw.Foliage.HasValue ? 1 : 0);
        bool matches = resource.Payload switch
        {
            MapRigidResource => draw.Rigid.HasValue,
            MapTerrainResource => draw.Rigid.HasValue,
            MapLightResource => draw.Light.HasValue,
            MapWaterResource => draw.Water.HasValue,
            MapFoliageResource => draw.Foliage.HasValue,
            _ => false
        };
        if (frames != 1 || !matches || (draw.Channels & ~(MapDrawChannels)63) != 0)
            throw new ArgumentException("Draw requires exactly the frame matching its registration and known channels.", nameof(draw));
        if (resource.Payload is MapFoliageResource foliage)
        {
            if (foliage.Anchor != draw.Anchor) throw new ArgumentException("Foliage frame and registration anchors differ.", nameof(draw));
            foreach (var model in foliage.Models)
                foreach (var part in model.Parts)
                    if (!_resources.TryGetValue(part, out var mesh) || mesh.Payload is not MapRigidResource)
                        throw new InvalidOperationException("Foliage model part has no rigid registration.");
        }
        _draws.Add(draw);
    }
    public MapDrawSet Snapshot() => new(_resources.Values.ToArray(), _draws.ToArray());
}
