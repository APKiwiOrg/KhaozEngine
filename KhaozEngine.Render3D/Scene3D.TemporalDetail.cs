using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>The history grid latched by the last frame's first render. Display before the first render,
    /// without the temporal resolve, or when Fine exceeds its pixel budget or the device dimension limit.</summary>
    public TemporalHistoryDetail TemporalHistoryDetailInEffect { get; private set; }

    void LatchTemporalDetail() => TemporalHistoryDetailInEffect = TemporalDetailMath.Mode(
        Post.Temporal.HistoryDetail, _frameResolves, _latchedDisplayWidth, _latchedDisplayHeight,
        _gd.Capabilities.MaxRenderTexture2DDimension, Post.Temporal.HistoryDetailPixelLimit);
}
