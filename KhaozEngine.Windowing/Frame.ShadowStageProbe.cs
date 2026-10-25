namespace KhaozEngine.Windowing;

public sealed partial class Frame
{
    ShadowStageProbeRequest _shadowStageRequest;
    ShadowStageProbeSnapshot? _shadowStageSnapshot;
    int _shadowStageProducers;
    bool _shadowStageCompleted;

    public ShadowStageProbeRequest ShadowStageRequest => _shadowStageRequest;

    public void PublishShadowStageSnapshot(in ShadowStageProbeSnapshot snapshot)
    {
        // Keep the first value as evidence. Additional producers invalidate rather than replace it.
        if (_shadowStageCompleted) { _shadowStageProducers++; return; }
        if (++_shadowStageProducers == 1) _shadowStageSnapshot = snapshot;
    }

    internal void BeginShadowStageCallback(in ShadowStageProbeRequest request)
    {
        _shadowStageRequest = request;
        _shadowStageSnapshot = null;
        _shadowStageProducers = 0;
        _shadowStageCompleted = false;
    }

    internal void CompleteShadowStageCallback() => _shadowStageCompleted = true;

    internal ShadowStageCallbackResult CompletedShadowStageCallback =>
        new(_shadowStageRequest, _shadowStageCompleted, _shadowStageProducers, _shadowStageSnapshot);
}
