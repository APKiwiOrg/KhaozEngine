using KhaozEngine.Render3D.Internal;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    void UpdateTemporalDetail(IGpuCommandList cl)
    {
        if (TemporalHistory.DetailStore is not { } detail) return;
        using (ProfilePass(cl, "temporal.detail-store"))
        {
            if (TemporalHistory.IsValid) detail.Update(cl, TemporalHistory.WriteIndex);
            else detail.Replicate(cl, TemporalHistory.WriteIndex);
        }
        _frameStats.DrawCalls += 2;   // classification and update, beyond the ordinary resolve and depth store
    }

    /// <summary>The history grid latched by the last frame's first render. Display before the first render,
    /// without the temporal resolve, when Fine exceeds its pixel or dimension limits, or when the device has fewer
    /// than six colour attachments.</summary>
    public TemporalHistoryDetail TemporalHistoryDetailInEffect { get; private set; }

    // Admission is evaluated only when Fine is requested on a resolving frame, so the default reads neither the
    // device limit nor the budget. Leaving Fine still latches Display here, which is what resets the history.
    void LatchTemporalDetail() => TemporalHistoryDetailInEffect =
        Post.Temporal.HistoryDetail == TemporalHistoryDetail.Fine && _frameResolves
            ? TemporalDetailMath.Mode(TemporalHistoryDetail.Fine, resolves: true, _latchedDisplayWidth,
                _latchedDisplayHeight, _gd.Capabilities.MaxRenderTexture2DDimension,
                Post.Temporal.HistoryDetailPixelLimit, _gd.Capabilities.MaxColorAttachments)
            : TemporalHistoryDetail.Display;
}
