using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly TemporalDetailAdmission _temporalDetailAdmission = new();

    void UpdateTemporalDetail(IGpuCommandList cl)
    {
        if (TemporalHistory.DetailStore is not { } detail) return;
        using (ProfilePass(cl, "temporal.detail-store"))
            detail.Update(cl, TemporalHistory.WriteIndex);
        _frameStats.DrawCalls += 2;   // Fine classification and fine-grid copy
    }

    /// <summary>The history grid used by the last frame's first render. Fine is requested by default and takes effect
    /// when admitted and its targets are created. The value is Display before the first render and whenever Fine is
    /// unavailable or Display is requested. See <see cref="TemporalHistoryDetailFallback"/> for the reason.</summary>
    public TemporalHistoryDetail TemporalHistoryDetailInEffect { get; private set; }

    /// <summary>Why the last frame's first render used Display when Fine was requested. None before the first render.</summary>
    public TemporalHistoryDetailFallback TemporalHistoryDetailFallback { get; private set; }

    // Latching a different grid resets history through the frame key.
    void LatchTemporalDetail()
    {
        var requested = Post.Temporal.HistoryDetail;
        var capabilities = requested == TemporalHistoryDetail.Fine && _frameResolves ? _gd.Capabilities : default;
        (TemporalHistoryDetailInEffect, TemporalHistoryDetailFallback) = _temporalDetailAdmission.Latch(requested,
            _frameResolves, _latchedDisplayWidth, _latchedDisplayHeight, capabilities.MaxRenderTexture2DDimension,
            Post.Temporal.HistoryDetailPixelLimit, capabilities.MaxColorAttachments);
    }

    bool EnsureTemporalHistoryTargets(TemporalResolveRenderer resolve, int displayWidth, int displayHeight)
    {
        bool created = TemporalHistory.EnsureTargets(_gd, displayWidth, displayHeight, _res.Width, _res.Height, _retired,
            TemporalHistoryDetailInEffect);
        if (TemporalHistory.DetailStore is not null)
        {
            var failure = resolve.TryBindDetail(TemporalHistory);
            if (failure is null)
            {
                _temporalDetailAdmission.ConfirmFine();
                return created;
            }
            TemporalHistory.DropDetail(failure, _retired);
        }
        if (TemporalHistoryDetailInEffect == TemporalHistoryDetail.Fine) FallBackToDisplayHistory();
        return created;
    }

    void FallBackToDisplayHistory()
    {
        TemporalHistoryDetailInEffect = TemporalHistoryDetail.Display;
        TemporalHistoryDetailFallback = TemporalHistoryDetailFallback.CreationFailed;
        bool logRefusal = _temporalDetailAdmission.RefuseCreation(_latchedDisplayWidth, _latchedDisplayHeight);
        _historyKey = _historyKey with { HistoryDetail = TemporalHistoryDetail.Display };
        if (TemporalHistory.IsValid)
        {
            TemporalHistory.Invalidate(TemporalResetReason.HistoryDetail);
            _lastTemporalDiagnostics = _lastTemporalDiagnostics.WithHistoryReset(TemporalHistory.LastReset);
        }
        if (logRefusal)
        {
            var failure = TemporalHistory.DetailFailure!;
            _shadowReconfigureLogger.Error(
                $"Fine temporal history at {_latchedDisplayWidth} by {_latchedDisplayHeight} failed "
                + $"({failure.GetType().Name}: {failure.Message}). Display stays in effect until the display size changes, "
                + "Display is requested for a frame or the resolve stops.");
        }
    }
}
