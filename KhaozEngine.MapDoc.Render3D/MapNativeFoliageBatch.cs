using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

internal sealed record MapNativeFoliagePart(MapRenderKey Key, MapBox3 Bounds, MapRigidResource Payload, string Digest);
internal sealed record MapNativeFoliageGroup(string OwnerId, MapSubmissionAnchor Anchor, MapBox3 Bounds,
    GroundCoverBatch Cover, IReadOnlyList<string> Models, int[]? ExplicitIndices, bool Explicit, bool CastsShadows, string Digest,
    MapFoliageColumnOwner.Reader? Instances = null)
{
    internal ReadOnlyMemory<int> Indices => Instances is { } source ? source.Columns.BorrowModelIndices : ExplicitIndices!;
}

/// <summary>Exact CPU foliage inventory and typed registrations. Prepared GPU publication belongs to the renderer.</summary>
public sealed class MapNativeFoliageBatch : IDisposable
{
    readonly MapSceneWitness _witness;
    readonly NativeFoliagePolicy _policy;
    MapNativeFoliageGroup[] _groups;
    MapNativeFoliagePart[] _parts;
    MapFoliageColumnOwner.Reader[] _sources;
    readonly int _identityCount;
    MapPreparedInputLease? _input;
    MapResourceStamp? _stamp;
    MapPreparedResource[]? _registrations;
    bool _disposed;
    public MapPlacementRouting Routing { get; }
    public IReadOnlyList<MapFoliageSampleKey> GeneratedIdentities
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var keys = new MapFoliageSampleKey[_identityCount]; int at = 0;
            foreach (var source in _sources)
                for (int i = 0; i < source.Count; i++) keys[at++] = source.Batch.Key(i);
            return Array.AsReadOnly(keys);
        }
    }
    public IReadOnlyList<string> ExplicitIds { get; }
    public IReadOnlyList<MapFoliageGenerationKey> GenerationKeys { get; }
    public IReadOnlyList<GroundCoverBatch> CoverBatches
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Array.AsReadOnly(_groups.Select(g => new GroundCoverBatch(g.Cover.ToArray())).ToArray());
        }
    }
    public MapBox3? Bounds { get; }
    public string RenderDigest { get; }
    internal MapPreparationReservation? PreparationOwnership { get; private set; }
    internal void Own(MapPreparationReservation ownership)
    { PreparationOwnership = ownership; PreparationChargeBytes = ownership.Bytes; }
    public long PreparationChargeBytes { get; private set; }
    public long RetainedBytes { get; }
    public long InputRetainedBytes => _input?.RetainedBytes ?? 0;
    internal void AttachInput(MapPreparedInputLease input) => _input = input;
    internal MapNativeFoliageBatch(MapSceneWitness witness, MapPlacementRouting routing, NativeFoliagePolicy policy,
        IReadOnlyList<MapNativeFoliageGroup> groups, IReadOnlyList<MapNativeFoliagePart> parts,
        IReadOnlyList<MapFoliageColumnOwner.Reader> sources, IReadOnlyList<string> explicitIds,
        IReadOnlyList<MapFoliageGenerationKey> generations, long charge)
    {
        _witness = witness; Routing = routing; _policy = policy; _groups = groups.ToArray(); _parts = parts.ToArray();
        _sources = sources.ToArray(); _identityCount = _sources.Sum(s => s.Count);
        ExplicitIds = Array.AsReadOnly(explicitIds.ToArray()); GenerationKeys = Array.AsReadOnly(generations.ToArray());
        RetainedBytes = checked(512L + 8L * (ExplicitIds.Count + GenerationKeys.Count) + _sources.Sum(s => s.PlanBytes + 104L) +
            _parts.Sum(p => 256L + MapNativeInventory.Mesh(p.Payload.BorrowMesh, p.Payload.BorrowMaps)) +
            _groups.Sum(g => 256L + (g.Instances is null ? 240L * g.Cover.Count + 128L * g.Models.Count : g.Cover.RetainedViewBytes + 192L * g.Models.Count)));
        PreparationChargeBytes = charge;
        foreach (var group in groups) Bounds = Bounds?.Union(group.Bounds) ?? group.Bounds;
        RenderDigest = MapPlacementDigest.Hash(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/native-foliage-render/1");
            w.WriteStringValue(routing.SceneWitnessDigest); w.WriteStringValue(routing.PolicyDigest); w.WriteStringValue(policy.Digest);
            foreach (var group in groups) w.WriteStringValue(group.Digest);
            foreach (var part in parts) w.WriteStringValue(part.Digest);
            w.WriteEndArray();
        });
    }
    public string UploadKey(MapResourceStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        return MapPlacementDigest.Hash(w =>
        {
            w.WriteStartArray(); w.WriteStringValue("kemap/native-foliage-upload/1"); w.WriteStringValue(RenderDigest);
            w.WriteStringValue(stamp.ClosureHash); w.WriteStringValue(stamp.SourceGeneration); w.WriteStringValue(stamp.ScopeDigest);
            w.WriteStringValue(stamp.PolicyDigest); w.WriteNumberValue(stamp.Generation); w.WriteEndArray();
        });
    }
    public void Register(MapResourceStamp stamp, IMapDrawSink sink) => Register(stamp, sink, true);
    internal void RegisterResident(MapResourceStamp stamp, IMapDrawSink sink) => Register(stamp, sink, false);
    void Register(MapResourceStamp stamp, IMapDrawSink sink, bool detached)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(sink);
        if (stamp.ClosureHash != _witness.ClosureHash || stamp.SourceGeneration != _witness.SourceGeneration || stamp.ScopeDigest != _witness.ScopeDigest)
            throw new MapDocumentException("Foliage registration stamp belongs to another source, closure or scope.");
        if (_stamp is not null && _stamp != stamp) throw new InvalidOperationException("A foliage batch cannot change its registered generation.");
        if (_registrations is null)
        {
            var resources = new List<MapPreparedResource>();
            var references = new Dictionary<string, List<MapResourceReference>>(StringComparer.Ordinal);
            foreach (var part in _parts)
            {
                var reference = new MapResourceReference(part.Key, stamp);
                resources.Add(new(reference, part.Bounds, part.Payload, part.Digest));
                if (!references.TryGetValue(part.Key.OwnerId, out var model)) references.Add(part.Key.OwnerId, model = []);
                model.Add(reference);
            }
            foreach (var group in _groups)
            {
                var models = group.Models.Select(id => new MapFoliageModelBinding(id, references[id],
                    _policy.Models[id].Motion, _policy.Models[id].CastShadows)).ToArray();
                MapFoliageResource payload;
                if (group.Instances is { } source) payload = MapFoliageResource.FromResident(group.Anchor, source, models);
                else
                {
                    var transforms = group.Cover.Select(i => i.Transform).ToArray();
                    var ranks = group.Cover.Select(i => i.ThinningRank).ToArray();
                    payload = group.Explicit ? MapFoliageResource.FromAnchorLocal(group.Anchor, transforms, ranks, group.Indices, models)
                        : new MapFoliageResource(group.Anchor, transforms, ranks, group.Indices, models);
                    payload.MarkResident();
                }
                var key = new MapRenderKey(MapRenderKind.Foliage, group.OwnerId, "instances", 0);
                string digest = MapPlacementDigest.Hash(w =>
                {
                    // Window and batch witnesses belong to aliases. Only this group's rows and bindings own the upload.
                    w.WriteStartArray(); w.WriteStringValue("kemap/native-foliage-instances/1"); w.WriteStringValue(group.Digest);
                    foreach (var model in models)
                    {
                        w.WriteStringValue(model.ModelId);
                        foreach (float value in new[] { model.Motion.BendScale, model.Motion.MaxLean, model.Motion.WindScale, model.Motion.Shiver })
                            w.WriteNumberValue(BitConverter.SingleToInt32Bits(value));
                        w.WriteBooleanValue(model.CastShadows);
                        foreach (var part in _parts.Where(p => p.Key.OwnerId == model.ModelId)) w.WriteStringValue(part.Digest);
                    }
                    w.WriteEndArray();
                });
                resources.Add(new(new(key, stamp), group.Bounds, payload, digest));
            }
            _registrations = resources.ToArray(); _stamp = stamp;
        }
        foreach (var resource in _registrations) sink.Register(detached && resource.Payload is MapFoliageResource foliage
            ? resource with { Payload = foliage.Detached() } : resource);
    }
    public void Record(MapFrameState frame, IMapDrawSink sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); ArgumentNullException.ThrowIfNull(sink);
        if (_registrations is null) throw new InvalidOperationException("Register foliage before recording frames.");
        var state = frame.FoliageState ?? MapFoliageFrameState.From(_policy.Options);
        var options = MapFoliageFrameOptions.From(_policy.Options);
        for (int i = 0; i < _groups.Length; i++)
        {
            var group = _groups[i];
            var resource = _registrations[_parts.Length + i];
            var channels = MapDrawChannels.Colour;
            if (group.CastsShadows) channels |= MapDrawChannels.Shadow;
            var local = LocalState(state, group.Anchor);
            // Authored placements use the ordinary prop radius without generated density thinning.
            var drawOptions = group.Explicit ? options with
            { DrawRadius = Routing.DefaultDrawRadius, QualityDensity = 1, DistantDensity = 1 } : options;
            sink.Write(new(resource.Reference, channels, group.Anchor, Matrix4x4.Identity,
                MapRenderTransform.FocusLocal(frame.Focus, group.Anchor), null, null, null,
                new(drawOptions, local, frame.TimeSeconds, frame.WindSeed)));
        }
    }
    static MapFoliageFrameState LocalState(MapFoliageFrameState state, MapSubmissionAnchor anchor)
    {
        return new(state.WindDirection, state.WindStrength, state.WindSpeed, state.WindSpatialFrequency, state.InteractorCount,
            state.InteractorCount > 0 ? Local(state.I0) : default, state.InteractorCount > 1 ? Local(state.I1) : default,
            state.InteractorCount > 2 ? Local(state.I2) : default, state.InteractorCount > 3 ? Local(state.I3) : default);
        FoliageInteractor Local(FoliageInteractor interactor) => interactor with
        {
            Position = MapSubmissionGeometry.Offset(new(MapExactValue.FromSingle(interactor.Position.X),
                MapExactValue.FromSingle(interactor.Position.Y), MapExactValue.FromSingle(interactor.Position.Z)), anchor)
        };
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_registrations is not null)
            foreach (var resource in _registrations)
                if (resource.Payload is MapFoliageResource foliage) foliage.ReleaseResident();
        foreach (var group in _groups) group.Cover.ReleaseResident();
        _registrations = null; _groups = []; _parts = [];
        foreach (var source in _sources) source.Dispose();
        _sources = []; var input = _input; _input = null; input?.Dispose();
        PreparationOwnership?.Dispose(); PreparationOwnership = null;
    }
}
