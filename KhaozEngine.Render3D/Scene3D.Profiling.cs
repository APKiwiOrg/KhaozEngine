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
        }
    }

    /// <summary>The first callback failure in the last observed render, including when rendering itself failed.
    /// A successful render with a callback failure throws after recording. An original render exception wins.</summary>
    public Exception? PassObserverFailure => _profiling?.Failure;

    internal void RenderInternal(IGpuCommandList cl, int viewportW, int viewportH, IGpuFramebuffer target)
    {
        _profiling?.Reset();
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

    void ReportSkippedPointRows(IGpuCommandList cl)
    {
        if (_profiling is null) return;
        for (int i = 0; i < _pointRequests.Count; i++)
        {
            var request = _pointRequests[i];
            if (request.Slot >= 0 && !_pointRebuilds.Contains(i))
                SkipProfilePass(cl, PointRowPass(false, request.Mode == LightShadowMode.Dynamic), request.Slot);
        }
    }

    enum PointProfileLeaf { Setup, Rigid, Foliage, CpuSkinned, GpuSkinned }

    RenderProfilingScope ProfilePass(IGpuCommandList cl, RenderPassKey key, bool executed = true) =>
        _profiling?.Pass(cl, key.Id, key.Occurrence, executed) ?? default;

    void SkipProfilePass(IGpuCommandList cl, RenderPassKey key) =>
        _profiling?.Pass(cl, key.Id, key.Occurrence, executed: false);

    RenderPassKey PointLeafKey(in PackedPointShadowSlot packed, PointProfileLeaf leaf, int face = -1)
    {
        bool transient = ReferenceEquals(packed.Atlas, _pointShadowTransientAtlas);
        bool dynamic = packed.RequestIndex >= 0 && _pointRequests[packed.RequestIndex].Mode == LightShadowMode.Dynamic;
        string id = (transient, dynamic, leaf) switch
        {
            (true, _, PointProfileLeaf.Setup) => "shadow.point.transient.setup",
            (true, _, PointProfileLeaf.Rigid) => "shadow.point.transient.rigid",
            (true, _, PointProfileLeaf.Foliage) => "shadow.point.transient.foliage",
            (true, _, PointProfileLeaf.CpuSkinned) => "shadow.point.transient.cpu-skinned",
            (true, _, PointProfileLeaf.GpuSkinned) => "shadow.point.transient.gpu-skinned",
            (false, true, PointProfileLeaf.Setup) => "shadow.point.dynamic.setup",
            (false, true, PointProfileLeaf.Rigid) => "shadow.point.dynamic.rigid",
            (false, true, PointProfileLeaf.Foliage) => "shadow.point.dynamic.foliage",
            (false, true, PointProfileLeaf.CpuSkinned) => "shadow.point.dynamic.cpu-skinned",
            (false, true, PointProfileLeaf.GpuSkinned) => "shadow.point.dynamic.gpu-skinned",
            (false, false, PointProfileLeaf.Setup) => "shadow.point.static.setup",
            (false, false, PointProfileLeaf.Rigid) => "shadow.point.static.rigid",
            (false, false, PointProfileLeaf.Foliage) => "shadow.point.static.foliage",
            (false, false, PointProfileLeaf.CpuSkinned) => "shadow.point.static.cpu-skinned",
            _ => "shadow.point.static.gpu-skinned",
        };
        return new(id, face < 0 ? packed.Row : packed.Row * 6 + face);
    }

    static string PointRowPass(bool transient, bool dynamic) => transient ? "shadow.point.transient"
        : dynamic ? "shadow.point.dynamic" : "shadow.point.static";
    static string PointFacePass(bool transient, bool dynamic) => transient ? "shadow.point.transient.face"
        : dynamic ? "shadow.point.dynamic.face" : "shadow.point.static.face";
}
