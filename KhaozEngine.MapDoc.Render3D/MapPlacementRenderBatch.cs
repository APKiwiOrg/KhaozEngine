using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

internal sealed record MapPlacementPart(MapRenderKey Key, MeshHandle Handle, MapSubmissionAnchor Anchor, MapBox3 Bounds, MapRigidResource Payload, string Digest);

/// <summary>Frozen placement inventory and CPU resources. Accepted HLOD handles are borrowed from publication.</summary>
public sealed class MapPlacementRenderBatch : IDisposable
{
    readonly IReadOnlyList<PropLayer> _ordinary;
    readonly IReadOnlyList<MapPlacementPart> _parts;
    MapPreparedInputLease? _input;
    readonly PropClusterRenderer _clusters = PropClusterRenderer.CreateCpuOnly();
    MapPreparedResource[]? _registered;
    MapResourceStamp? _stamp;
    bool _disposed;
    public IReadOnlyList<MapResolvedPlacement> Inventory { get; }
    public MapPlacementRouting Routing { get; }
    public IReadOnlyList<MapEffectiveMarker> Markers { get; }
    public IReadOnlyList<NativePropClusterCpuBuild> Clusters { get; }
    public long PreparationChargeBytes { get; }
    public long InputRetainedBytes => _input?.RetainedBytes ?? 0;
    internal void AttachInput(MapPreparedInputLease input) => _input = input;
    internal MapPlacementRenderBatch(IReadOnlyList<MapResolvedPlacement> inventory, IReadOnlyList<MapEffectiveMarker> markers, IReadOnlyList<PropLayer> ordinary,
        IReadOnlyList<MapPlacementPart> parts, MapPlacementRouting routing, IReadOnlyList<NativePropClusterCpuBuild> clusters, long charge)
    {
        Inventory = Array.AsReadOnly(inventory.ToArray()); Markers = Array.AsReadOnly(markers.ToArray()); _ordinary = Array.AsReadOnly(ordinary.ToArray());
        _parts = Array.AsReadOnly(parts.ToArray()); Routing = routing;
        Clusters = Array.AsReadOnly(clusters.ToArray()); PreparationChargeBytes = charge;
    }
    public void Register(MapResourceStamp stamp, IMapDrawSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(sink);
        if (_stamp is not null && _stamp != stamp) throw new InvalidOperationException("A placement batch cannot change its registered generation.");
        _stamp ??= stamp;
        _registered ??= _parts.Select(p => new MapPreparedResource(new(p.Key, stamp), p.Bounds, p.Payload, p.Digest)).ToArray();
        foreach (var resource in _registered) sink.Register(resource);
    }
    public void AcceptCluster(PropClusterKey key, NativePropClusterCpuBuild build, MeshHandle acceptedMesh)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Clusters.Any(c => ReferenceEquals(c, build)) || build.Request.Key != key || build.BorrowMesh.TriangleCount == 0)
            throw new MapDocumentException("Cluster publication belongs to another prepared inventory.");
        _clusters.ApplyNative(key, build, acceptedMesh);
    }
    public void Record(MapFrameState frame, IMapDrawSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(sink);
        if (_registered is null) throw new InvalidOperationException("Register placements before recording frames.");
        var references = _parts.Select((p, i) => (p, _registered[i].Reference)).ToDictionary(p => (p.p.Key.OwnerId, p.p.Handle), p => p.Reference);
        foreach (var layer in _ordinary) Emit(layer, 0);
        foreach (var cluster in Clusters)
        {
            var request = cluster.Request; var layer = request.BorrowLayer;
            var anchor = new MapSubmissionAnchor(request.Anchor.X, request.Anchor.Y, request.Anchor.Z);
            var focus = MapRenderTransform.FocusLocal(frame.Focus, anchor);
            var state = _clusters.TryGetNativeEmission(request.Key, focus, out var accepted, out var mergedDraw) ? accepted
                : PropClusterRenderer.NativeDrawState(request, false, focus);
            if (state.DrawsIndividuals) Emit(layer, state.IndividualDissolveFloor);
            if (state.DrawsMerged)
            {
                int part = _parts.ToList().FindIndex(p => p.Key.OwnerId == MapPlacementCells.Owner(request.Key) && p.Key.PartId == "hlod");
                if (part >= 0) Write(_registered[part].Reference, anchor, mergedDraw);
            }
        }

        void Emit(PropLayer layer, float floor) => PropRenderer.EmitNative(layer, default, sink,
            (_, instance, draw) =>
            {
                var anchor = new MapSubmissionAnchor(instance.Anchor.X, instance.Anchor.Y, instance.Anchor.Z);
                Write(references[(instance.Id, draw.Mesh)], anchor, draw);
            }, floor, a => MapRenderTransform.FocusLocal(frame.Focus, new(a.X, a.Y, a.Z)));
        void Write(MapResourceReference reference, MapSubmissionAnchor anchor, RigidInstanceDraw draw) =>
            sink.Write(new(reference, Channels(draw.CastsShadows), anchor, draw.World,
                MapRenderTransform.FocusLocal(frame.Focus, anchor), new MapRigidFrame(draw.Tint, draw.Material,
                    draw.Dissolve, draw.DissolveEdgeWidth, draw.DissolveEdgeColor, draw.InvertShadowDissolve,
                    draw.DissolveComplement, draw.Motion), null, null, null));
    }
    static MapDrawChannels Channels(bool shadows) => MapDrawChannels.Colour | MapDrawChannels.Camera | MapDrawChannels.Sky |
        MapDrawChannels.Selection | MapDrawChannels.Physical | (shadows ? MapDrawChannels.Shadow : MapDrawChannels.None);
    public void Dispose() { if (_disposed) return; _disposed = true; _clusters.Dispose(); _input?.Dispose(); }
}
