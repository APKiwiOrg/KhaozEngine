using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Diagnostics;

/// <summary>Opt-in logical render boundaries. CPU callbacks must not record GPU commands.</summary>
public interface IRenderPassObserver
{
    void BeginPass(IGpuCommandList commands, RenderPassKey key);
    void EndPass(IGpuCommandList commands, RenderPassKey key);
    void SkipPass(IGpuCommandList commands, RenderPassKey key);
    void BeginCpuPhase(string id);
    void EndCpuPhase(string id);
    /// <summary>Marks motion written by this pass's existing MRT draw, without a separate interval.</summary>
    void FusedMotion(IGpuCommandList commands, RenderPassKey key) { }
}
