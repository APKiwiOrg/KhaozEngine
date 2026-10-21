using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    enum PointProfilePass { Row, Face, Setup, Rigid, Foliage, Skinned }

    RenderProfilingScope ProfilePointRow(IGpuCommandList cl, in PackedPointShadowSlot packed, ref int requestCursor)
    {
        if (_profiling is not { } profiling) return default;
        int before = ReferenceEquals(packed.Atlas, _pointShadowTransientAtlas) ? _pointRequests.Count : packed.RequestIndex;
        ReportSkippedPointRows(cl, profiling, before, ref requestCursor);
        var key = PointPassKey(packed, PointProfilePass.Row);
        return profiling.Pass(cl, key.Id, key.Occurrence);
    }

    void ReportRemainingPointRows(IGpuCommandList cl, ref int requestCursor)
    {
        if (_profiling is not { } profiling) return;
        ReportSkippedPointRows(cl, profiling, _pointRequests.Count, ref requestCursor);
    }

    // Interleave cached rows at their admitted request positions. Never change the packed draw traversal.
    void ReportSkippedPointRows(IGpuCommandList cl, RenderPassProfiling profiling, int before, ref int requestCursor)
    {
        for (; requestCursor < before; requestCursor++)
        {
            var request = _pointRequests[requestCursor];
            if (request.Slot < 0 || _pointRebuilds.Contains(requestCursor)) continue;
            string id = request.Mode == LightShadowMode.Dynamic ? "shadow.point.dynamic" : "shadow.point.static";
            profiling.Pass(cl, id, request.Slot, executed: false);
        }
    }

    RenderProfilingScope ProfilePointPass(IGpuCommandList cl, in PackedPointShadowSlot packed,
        PointProfilePass pass, int face = -1, bool executed = true)
    {
        if (_profiling is not { } profiling) return default;
        var key = PointPassKey(packed, pass, face);
        return profiling.Pass(cl, key.Id, key.Occurrence, executed);
    }

    RenderPassKey PointPassKey(in PackedPointShadowSlot packed, PointProfilePass pass, int face = -1)
    {
        bool transient = ReferenceEquals(packed.Atlas, _pointShadowTransientAtlas);
        bool dynamic = packed.RequestIndex >= 0 && _pointRequests[packed.RequestIndex].Mode == LightShadowMode.Dynamic;
        string id = (transient, dynamic, pass) switch
        {
            (true, _, PointProfilePass.Row) => "shadow.point.transient",
            (true, _, PointProfilePass.Face) => "shadow.point.transient.face",
            (true, _, PointProfilePass.Setup) => "shadow.point.transient.setup",
            (true, _, PointProfilePass.Rigid) => "shadow.point.transient.rigid",
            (true, _, PointProfilePass.Foliage) => "shadow.point.transient.foliage",
            (true, _, _) => UseGpuSkinning ? "shadow.point.transient.gpu-skinned" : "shadow.point.transient.cpu-skinned",
            (false, true, PointProfilePass.Row) => "shadow.point.dynamic",
            (false, true, PointProfilePass.Face) => "shadow.point.dynamic.face",
            (false, true, PointProfilePass.Setup) => "shadow.point.dynamic.setup",
            (false, true, PointProfilePass.Rigid) => "shadow.point.dynamic.rigid",
            (false, true, PointProfilePass.Foliage) => "shadow.point.dynamic.foliage",
            (false, true, _) => UseGpuSkinning ? "shadow.point.dynamic.gpu-skinned" : "shadow.point.dynamic.cpu-skinned",
            (false, false, PointProfilePass.Row) => "shadow.point.static",
            (false, false, PointProfilePass.Face) => "shadow.point.static.face",
            (false, false, PointProfilePass.Setup) => "shadow.point.static.setup",
            (false, false, PointProfilePass.Rigid) => "shadow.point.static.rigid",
            (false, false, PointProfilePass.Foliage) => "shadow.point.static.foliage",
            _ => UseGpuSkinning ? "shadow.point.static.gpu-skinned" : "shadow.point.static.cpu-skinned",
        };
        return new(id, face < 0 ? packed.Row : packed.Row * 6 + face);
    }
}
