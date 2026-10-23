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
    GroundCoverBatch Cover, string[] Models, int[] Indices, bool Explicit, bool CastsShadows, string Digest);

/// <summary>Exact CPU foliage inventory and typed registrations. Prepared GPU publication belongs to the renderer.</summary>
public sealed class MapNativeFoliageBatch : IDisposable
{
    readonly MapSceneWitness _witness;
    readonly NativeFoliagePolicy _policy;
    readonly MapNativeFoliageGroup[] _groups;
    readonly MapNativeFoliagePart[] _parts;
    MapPreparedInputLease? _input;
    MapResourceStamp? _stamp;
    MapPreparedResource[]? _registrations;
    bool _disposed;
    public MapPlacementRouting Routing { get; }
    public IReadOnlyList<MapFoliageSampleKey> GeneratedIdentities { get; }
    public IReadOnlyList<string> ExplicitIds { get; }
    public IReadOnlyList<MapFoliageGenerationKey> GenerationKeys { get; }
    public IReadOnlyList<GroundCoverBatch> CoverBatches { get; }
    public MapBox3? Bounds { get; }
    public string RenderDigest { get; }
    public long PreparationChargeBytes { get; }
    public long InputRetainedBytes => _input?.RetainedBytes ?? 0;
    internal void AttachInput(MapPreparedInputLease input) => _input = input;
    internal MapNativeFoliageBatch(MapSceneWitness witness, MapPlacementRouting routing, NativeFoliagePolicy policy,
        IReadOnlyList<MapNativeFoliageGroup> groups, IReadOnlyList<MapNativeFoliagePart> parts,
        IReadOnlyList<MapFoliageSampleKey> identities, IReadOnlyList<string> explicitIds,
        IReadOnlyList<MapFoliageGenerationKey> generations, long charge)
    {
        _witness = witness; Routing = routing; _policy = policy; _groups = groups.ToArray(); _parts = parts.ToArray();
        GeneratedIdentities = Array.AsReadOnly(identities.ToArray()); ExplicitIds = Array.AsReadOnly(explicitIds.ToArray());
        GenerationKeys = Array.AsReadOnly(generations.ToArray()); CoverBatches = Array.AsReadOnly(_groups.Select(g => g.Cover).ToArray());
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
    public void Register(MapResourceStamp stamp, IMapDrawSink sink)
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
                var transforms = group.Cover.Select(i => i.Transform).ToArray();
                var ranks = group.Cover.Select(i => i.ThinningRank).ToArray();
                var payload = group.Explicit ? MapFoliageResource.FromAnchorLocal(group.Anchor, transforms, ranks, group.Indices, models)
                    : new MapFoliageResource(group.Anchor, transforms, ranks, group.Indices, models);
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
        foreach (var resource in _registrations) sink.Register(resource);
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
            // Authored placements do not participate in generated density thinning.
            var drawOptions = group.Explicit ? options with { QualityDensity = 1, DistantDensity = 1 } : options;
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
    public void Dispose() { if (_disposed) return; _disposed = true; _input?.Dispose(); }
}
