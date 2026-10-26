using System;
using KhaozEngine.MapDoc.Scene;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

internal sealed record MapPlacementPart(MapRenderKey Key, MeshHandle Handle, MapSubmissionAnchor Anchor, MapBox3 Bounds, MapRigidResource Payload, string Digest);

/// <summary>Frozen placement inventory and CPU resources. Accepted HLOD handles are borrowed from publication.</summary>
public sealed partial class MapPlacementRenderBatch : IDisposable
{
    readonly PropLayer[] _ordinary, _roofs;
    readonly Dictionary<(string Owner, int Index, int Generation), int> _partIndices;
    readonly int[] _clusterParts;
    readonly Dictionary<string, int> _roofOutlineParts = new(StringComparer.Ordinal);
    readonly MapRoofCohort?[] _roofCohorts;
    readonly Dictionary<string, (MapRoofCohort Cohort, int Index)> _roofPolicies = new(StringComparer.Ordinal);
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
    internal MapPreparationReservation? PreparationOwnership { get; private set; }
    internal void Own(MapPreparationReservation ownership)
    { PreparationOwnership = ownership; PreparationChargeBytes = ownership.Bytes; }
    public long PreparationChargeBytes { get; private set; }
    internal long SharedMembershipBytes => _roofCohorts.Sum(c => c?.Evaluation.Counts.SharedCapacityBytes ?? 0);
    public long RetainedBytes
    {
        get
        {
            var inventory = new MapNativeInventory();
            return checked(1024L + 128L * (Inventory.Count + Markers.Count) + 256L * _parts.Count + 32L * Clusters.Count +
                _parts.Sum(p => MapNativeInventory.Mesh(p.Payload.BorrowMesh, p.Payload.BorrowMaps)) +
                _ordinary.Concat(_roofs).Sum(inventory.Layer) + Clusters.Sum(c => MapNativeInventory.Mesh(c.BorrowMesh) +
                    inventory.Layer(c.Request.BorrowLayer) + 16L * c.Request.Instances.Count +
                    8L * c.BorrowContributions.Count + c.BorrowContributions.Sum(m => MapNativeInventory.Mesh(m))) + FrameRetainedBytes);
        }
    }
    long FrameRetainedBytes => checked(4L * _clusterParts.LongLength + IntPtr.Size * _roofCohorts.LongLength +
        64L * _partIndices.EnsureCapacity(0) + 64L * _roofOutlineParts.EnsureCapacity(0) +
        64L * _roofPolicies.EnsureCapacity(0) + _roofCohorts.Sum(c => c?.RetainedBytes ?? 0));
    public long InputRetainedBytes => _input?.RetainedBytes ?? 0;
    internal void AttachInput(MapPreparedInputLease input) => _input = input;
    internal MapPlacementRenderBatch(IReadOnlyList<MapResolvedPlacement> inventory, IReadOnlyList<MapEffectiveMarker> markers, IReadOnlyList<PropLayer> ordinary, IReadOnlyList<PropLayer> roofs,
        IReadOnlyList<MapPlacementPart> parts, MapPlacementRouting routing, IReadOnlyList<NativePropClusterCpuBuild> clusters, long charge, MapPlacementCellInventory cells)
    {
        Inventory = Array.AsReadOnly(inventory.ToArray()); Markers = Array.AsReadOnly(markers.ToArray()); _ordinary = ordinary.ToArray(); _roofs = roofs.ToArray();
        _parts = Array.AsReadOnly(parts.ToArray()); Routing = routing;
        Clusters = Array.AsReadOnly(clusters.ToArray()); PreparationChargeBytes = charge;
        _partIndices = new();
        for (int i = 0; i < _parts.Count; i++)
        {
            _partIndices.Add((_parts[i].Key.OwnerId, _parts[i].Handle.Index, _parts[i].Handle.Generation), i);
            if (_parts[i].Key.PartId == "outline-hlod") _roofOutlineParts.Add(_parts[i].Key.OwnerId, i);
        }
        _clusterParts = new int[Clusters.Count];
        _roofCohorts = new MapRoofCohort?[Clusters.Count];
        for (int i = 0; i < Clusters.Count; i++)
        {
            if (Clusters[i].Request.Key.Cohort == 1)
            {
                var cohort = new MapRoofCohort(Clusters[i], cells.Get(Clusters[i].Request.Key).RoofEvaluation
                    ?? throw new MapDocumentException("Roof cohort requires admitted policy metadata."));
                _roofCohorts[i] = cohort;
                foreach (string id in Clusters[i].RepresentedIds) _roofPolicies.Add(id, (cohort, cohort.Indices[id]));
            }
            string owner = MapPlacementCells.Owner(Clusters[i].Request.Key);
            _clusterParts[i] = -1;
            for (int p = 0; p < _parts.Count; p++)
                if (_parts[p].Key.OwnerId == owner && _parts[p].Key.PartId == "hlod" &&
                    _parts[p].Key.Kind == (Clusters[i].Request.Key.Cohort == 1 ? MapRenderKind.Roof : MapRenderKind.Placement)) { _clusterParts[i] = p; break; }
        }
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
    internal void AcceptPublishedClusters()
    {
        foreach (var build in Clusters)
        {
            var part = _parts.SingleOrDefault(p => p.Key.OwnerId == MapPlacementCells.Owner(build.Request.Key) && p.Key.PartId == "hlod" &&
                p.Key.Kind == (build.Request.Key.Cohort == 1 ? MapRenderKind.Roof : MapRenderKind.Placement));
            if (part is not null) AcceptCluster(build.Request.Key, build, part.Handle);
        }
    }
    readonly record struct Emission(MapPlacementRenderBatch Batch, MapFrameState Frame, IMapDrawSink Sink,
        MapRoofEvaluationBuffer? Roofs, IReadOnlyDictionary<string, int>? RoofIndices);
    public void Record(MapFrameState frame, IMapDrawSink sink)
    {
        RequireRecord(sink);
        var emission = new Emission(this, frame, sink, null, null);
        foreach (var layer in _ordinary) Emit(layer, emission, 0);
        for (int i = 0; i < Clusters.Count; i++)
            if (_roofCohorts[i] is null) RecordCluster(i, emission);
    }
    void RecordCluster(int index, Emission emission)
    {
        var request = Clusters[index].Request;
        var anchor = new MapSubmissionAnchor(request.Anchor.X, request.Anchor.Y, request.Anchor.Z);
        var decision = ResolveCluster(index, emission.Frame, out var mergedDraw);
        var state = decision.State;
        bool shadowOnly = decision.MergedShadowOnly;
        if (state.DrawsIndividuals) Emit(request.BorrowLayer, emission, state.IndividualDissolveFloor);
        int part = _clusterParts[index];
        if (state.DrawsMerged && part >= 0 && (!shadowOnly || mergedDraw.CastsShadows))
            Write(_registered![part].Reference, anchor, mergedDraw,
                shadowOnly ? MapDrawChannels.Shadow | MapDrawChannels.Sky | MapDrawChannels.Physical : Channels(mergedDraw.CastsShadows), emission.Frame, emission.Sink);
    }
    PropClusterVisibilityDecision ResolveCluster(int index, MapFrameState frame, out RigidInstanceDraw mergedDraw)
    {
        var request = Clusters[index].Request;
        var focus = MapRenderTransform.FocusLocal(frame.Focus, new(request.Anchor.X, request.Anchor.Y, request.Anchor.Z));
        var state = _clusters.TryGetNativeEmission(request.Key, focus, out var accepted, out mergedDraw) ? accepted
            : PropClusterRenderer.NativeDrawState(request, false, focus);
        return _roofCohorts[index] is { } cohort ? PropClusterVisibilityDecision.Resolve(request.Key, state, cohort)
            : new(state, false, 0, false);
    }
    internal void UpdateRoofs(MapFrameState frame)
    { foreach (var cohort in _roofCohorts) cohort?.Evaluate(frame); }
    internal MapRoofIntent? RoofIntent(string id) => _roofPolicies.TryGetValue(id, out var row)
        ? row.Cohort.Evaluation.Roofs[row.Index] : null;
    internal void RecordRoofs(MapFrameState frame, MapRoofEvaluationBuffer roofs, IReadOnlyDictionary<string, int> indices, IMapDrawSink sink)
    {
        RequireRecord(sink);
        var emission = new Emission(this, frame, sink, roofs, indices);
        foreach (var layer in _roofs) Emit(layer, emission, 0);
        for (int i = 0; i < Clusters.Count; i++)
            if (_roofCohorts[i] is { } cohort)
            {
                cohort.Evaluate(frame);
                RecordCluster(i, new(this, frame, sink, cohort.Evaluation, cohort.Indices));
            }
    }
    void RequireRecord(IMapDrawSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(sink);
        if (_registered is null) throw new InvalidOperationException("Register placements before recording frames.");
    }
    static void Emit(PropLayer layer, Emission state, float floor) => PropRenderer.EmitNative(layer, default, state,
        static (e, instance, draw) =>
        {
            var anchor = new MapSubmissionAnchor(instance.Anchor.X, instance.Anchor.Y, instance.Anchor.Z);
            var channels = Channels(draw.CastsShadows);
            if (e.Roofs is not null)
            {
                var intent = e.Roofs.Roofs[e.RoofIndices![instance.Id]];
                channels = MapDrawChannels.Physical;
                if (intent.ColourVisible) channels |= MapDrawChannels.Colour | MapDrawChannels.Selection;
                if (intent.CastShadows && draw.CastsShadows) channels |= MapDrawChannels.Shadow;
                if (intent.CameraBoomBlocks) channels |= MapDrawChannels.Camera;
                if (intent.SkyPickingBlocks) channels |= MapDrawChannels.Sky;
            }
            if ((channels & (MapDrawChannels.Colour | MapDrawChannels.Shadow)) == 0) return;
            int part = e.Batch._partIndices[(instance.Id, draw.Mesh.Index, draw.Mesh.Generation)];
            Write(e.Batch._registered![part].Reference, anchor, draw, channels, e.Frame, e.Sink);
        }, floor, resolveFocus: static (e, a) => MapRenderTransform.FocusLocal(e.Frame.Focus, new(a.X, a.Y, a.Z)));
    static void Write(MapResourceReference reference, MapSubmissionAnchor anchor, RigidInstanceDraw draw, MapDrawChannels channels, MapFrameState frame, IMapDrawSink sink) =>
        sink.Write(new(reference, channels, anchor, draw.World,
            MapRenderTransform.FocusLocal(frame.Focus, anchor), new MapRigidFrame(draw.Tint, draw.Material,
                draw.Dissolve, draw.DissolveEdgeWidth, draw.DissolveEdgeColor, draw.InvertShadowDissolve,
                draw.DissolveComplement, draw.Motion), null, null, null));
    static MapDrawChannels Channels(bool shadows) => MapDrawChannels.Colour | MapDrawChannels.Camera | MapDrawChannels.Sky |
        MapDrawChannels.Selection | MapDrawChannels.Physical | (shadows ? MapDrawChannels.Shadow : MapDrawChannels.None);
    public void Dispose() { if (_disposed) return; _disposed = true; _clusters.Dispose(); _input?.Dispose(); PreparationOwnership?.Dispose(); }
}
