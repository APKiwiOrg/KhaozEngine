using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    RenderPassProfiling? _profiling;

    /// <summary>Optional render boundaries. Set before recording, null by default. Callbacks may record markers
    /// but must not submit, wait, rebind targets or change scene state. CPU callbacks record no GPU commands.</summary>
    public IRenderPassObserver? PassObserver
    {
        get => _profiling?.Observer;
        set
        {
            _profiling = value is null ? null : new(value);
            _atmosphere.Profiling = _profiling;
            _water.Profiling = _profiling;
            _post.Profiling = _profiling;
            _transitions.Profiling = _profiling;
            _targetOutlines.Profiling = _profiling;
            if (_temporalResolve is not null) _temporalResolve.Profiling = _profiling;
            if (_laterRenderPost is not null) _laterRenderPost.Profiling = _profiling;
        }
    }

    /// <summary>The first callback failure in the last observed render, including when rendering itself failed.
    /// A successful render with a callback failure throws after recording. An original render exception wins.</summary>
    public Exception? PassObserverFailure => _profiling?.Failure;

    internal void RenderInternal(IGpuCommandList cl, int viewportW, int viewportH, IGpuFramebuffer target)
    {
        RenderCore(cl, viewportW, viewportH, target);
        _profiling?.ThrowIfFailed();
    }

    RenderProfilingScope ProfilePass(IGpuCommandList cl, string id, int occurrence = 0,
        bool executed = true, bool fusedMotion = false) =>
        _profiling?.Pass(cl, id, occurrence, executed, fusedMotion) ?? default;

    RenderProfilingScope ProfileCpu(string id) => _profiling?.Cpu(id) ?? default;

    void SkipProfilePass(IGpuCommandList cl, string id, int occurrence = 0) =>
        _profiling?.Pass(cl, id, occurrence, executed: false);

    static string RigidShadowPass(ShadowDepthVariant variant) => variant switch
    {
        ShadowDepthVariant.Cutout or ShadowDepthVariant.CutoutInverted => "shadow.rigid.cutout",
        ShadowDepthVariant.Dissolve or ShadowDepthVariant.DissolveInverted => "shadow.rigid.dissolve",
        _ => "shadow.rigid.opaque",
    };

}
