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

    /// <summary>The history grid latched by the last frame's first render. Fine is requested by default and
    /// takes effect on resolving frames within the device and pixel limits. The value is Display before the first
    /// render, without a resolve, with an explicit opt-out, or when admission fails.</summary>
    public TemporalHistoryDetail TemporalHistoryDetailInEffect { get; private set; }

    // Check device limits and budget only when a resolving frame requests Fine, the default request.
    // Latching a different grid resets history through the frame key.
    void LatchTemporalDetail() => TemporalHistoryDetailInEffect =
        Post.Temporal.HistoryDetail == TemporalHistoryDetail.Fine && _frameResolves
            ? TemporalDetailMath.Mode(TemporalHistoryDetail.Fine, resolves: true, _latchedDisplayWidth,
                _latchedDisplayHeight, _gd.Capabilities.MaxRenderTexture2DDimension,
                Post.Temporal.HistoryDetailPixelLimit, _gd.Capabilities.MaxColorAttachments)
            : TemporalHistoryDetail.Display;
}
