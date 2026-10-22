using System;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>The viewport and capture share one residency, publication and typed submission path.</summary>
public sealed class NativeMapView : IDisposable
{
    readonly Scene3D _scene;
    readonly MapRenderResidency _residency;
    MapRenderReadiness _readiness;
    MapFrameState? _frame;
    long _acceptedEpoch;
    public NativeMapView(Scene3D scene, MapRenderResidency residency)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene)); _residency = residency ?? throw new ArgumentNullException(nameof(residency));
        _readiness = Readiness(residency.Update());
    }
    MapRenderReadiness Readiness(MapRenderReadiness raw)
    {
        int ready = _residency.AcceptedPlans.Count;
        if (_readiness is { } old && old.Transition == raw.Transition && old.Pending == raw.Pending && old.Ready == ready &&
            old.CpuBytes == raw.CpuBytes && old.GpuBytes == raw.GpuBytes && old.QueuedBytes == raw.QueuedBytes) return old;
        return raw with { Ready = ready };
    }
    public MapRenderReadiness Update(MapFrameState frame)
    {
        TrackFrame(frame); return _readiness = Readiness(_residency.Update());
    }
    public void Draw(MapFrameState frame)
    {
        TrackFrame(frame);
        if (_residency.Device is not IMapDrawSink sink) throw new InvalidOperationException("Native view requires a typed draw device.");
        if (sink is MapScene3DSink native) native.BeginFrame();
        _scene.EffectTimeSeconds = (float)frame.TimeSeconds;
        _residency.Record(frame, sink);
    }
    void TrackFrame(MapFrameState frame)
    {
        long epoch = _residency.CurrentTransition.AcceptedWitness?.Epoch ?? 0;
        if (_frame is { } old && (old.RoofMode != frame.RoofMode || old.Frame != frame.Frame) || _acceptedEpoch != 0 && epoch != _acceptedEpoch) _scene.CameraCut();
        _acceptedEpoch = epoch; _frame = frame;
    }
    public MapPresentationHit? Pick(MapPresentationRay ray, MapPresentationChannel channel, MapFrameState frame)
    {
        MapPresentationHit? nearest = null;
        var plans = _residency.AcceptedPlans;
        for (int i = 0; i < plans.Count; i++)
            if (plans[i].PresentationPicker.Pick(ray, channel, frame) is { } hit && (nearest is null || hit.Distance < nearest.Value.Distance)) nearest = hit;
        return nearest;
    }
    public MapRenderReadiness SettleForCapture(int maxOperations)
    {
        if (maxOperations < 0) throw new ArgumentOutOfRangeException(nameof(maxOperations));
        _readiness = Readiness(_residency.InspectReadiness());
        for (int operation = 0; operation < maxOperations && _readiness.Pending > 0; operation++)
        {
            _residency.CompletePreparation();
            if (_residency.Device is IMapRenderUploadDependencies dependencies) dependencies.CompleteUploads();
            _readiness = Readiness(_residency.Update());
        }
        return _readiness;
    }
    public MapRenderTransition Replace(MapRenderSourceState state)
    { var transition = _residency.RequestReplacement(state); _readiness = Readiness(_residency.Update()); return transition; }
    public MapRenderTransition Invalidate(MapRenderSourceState state, MapNativeEditEffects effects)
    { var transition = _residency.Invalidate(state, effects); _readiness = Readiness(_residency.Update()); return transition; }
    public void Dispose() => _residency.Dispose();
}
