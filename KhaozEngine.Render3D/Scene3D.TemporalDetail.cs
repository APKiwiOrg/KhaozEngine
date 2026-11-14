using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>The history grid latched by the last frame's first render. Display before the first render,
    /// without the temporal resolve, or when Fine exceeds its pixel budget or the device dimension limit.</summary>
    public TemporalHistoryDetail TemporalHistoryDetailInEffect { get; private set; }

    // Admission is evaluated only when Fine is requested on a resolving frame, so the default reads neither the
    // device limit nor the budget. Leaving Fine still latches Display here, which is what resets the history.
    void LatchTemporalDetail() => TemporalHistoryDetailInEffect =
        Post.Temporal.HistoryDetail == TemporalHistoryDetail.Fine && _frameResolves
            ? TemporalDetailMath.Mode(TemporalHistoryDetail.Fine, resolves: true, _latchedDisplayWidth,
                _latchedDisplayHeight, _gd.Capabilities.MaxRenderTexture2DDimension,
                Post.Temporal.HistoryDetailPixelLimit)
            : TemporalHistoryDetail.Display;
}
