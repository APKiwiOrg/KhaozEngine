namespace KhaozEngine.Windowing;

public sealed partial class Frame
{
    ShadowStageProbeRequest _shadowStageRequest;
    // Retain control through completion for recorder checks, then replace it at the next Begin.
    PointShadowAttributionCapture? _shadowStagePointAttribution;
    ShadowStageProbeSnapshot? _shadowStageSnapshot;
    int _shadowStageProducers;
    bool _shadowStageCompleted;

    public ShadowStageProbeRequest ShadowStageRequest => _shadowStageRequest;
    public PointShadowAttributionCapture? ShadowStagePointAttribution => _shadowStagePointAttribution;

    public void PublishShadowStageSnapshot(in ShadowStageProbeSnapshot snapshot) => PublishShadowStageSnapshot(snapshot, null);

    public void PublishShadowStageSnapshot(in ShadowStageProbeSnapshot snapshot, PointShadowAttributionCapture? pointAttribution)
    {
        // Keep the first value as evidence. Additional producers invalidate rather than replace it.
        if (_shadowStageCompleted) { _shadowStageProducers++; return; }
        if (++_shadowStageProducers == 1)
        {
            bool owned = ReferenceEquals(pointAttribution, _shadowStagePointAttribution)
                && (!snapshot.PointAttribution.HasValue || pointAttribution is not null);
            _shadowStageSnapshot = owned ? snapshot : snapshot with { Status = ShadowStageProbeStatus.InvalidInputs };
        }
    }

    internal void BeginShadowStageCallback(in ShadowStageProbeRequest request) => BeginShadowStageCallback(request, null);

    internal void BeginShadowStageCallback(in ShadowStageProbeRequest request, PointShadowAttributionCapture? pointAttribution)
    {
        _shadowStageRequest = request;
        _shadowStagePointAttribution = pointAttribution;
        _shadowStageSnapshot = null;
        _shadowStageProducers = 0;
        _shadowStageCompleted = false;
    }

    internal void CompleteShadowStageCallback() => _shadowStageCompleted = true;

    internal ShadowStageCallbackResult CompletedShadowStageCallback =>
        new(_shadowStageRequest, _shadowStageCompleted, _shadowStageProducers, _shadowStageSnapshot);
}
