using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    void PrepareCoverageDraws(IGpuCommandList commands, bool cascades)
    {
        if (_coverageDraws.Count == 0) return;
        foreach (var draw in _coverageDraws)
        {
            var instance = _coverageInstances[(int)draw.InstanceSlot];
            _model.PackCoverageSurface(draw);
            if (!cascades || draw.ShadowKind == ShadowCastKind.None) continue;
            for (int c = 0; c < _cascadeCount; c++)
                _model.PackCoverageCascade(draw, instance, c, _coverageDraws.Count,
                    _cascadeDepthVps[c], _frameOrigin, _cascadeNoiseScales[c]);
        }
        _model.UploadCoverageSurfaces(commands);
        if (cascades) _model.UploadCoverageCascades(commands);
    }

    void DrawCoverageCascades(IGpuCommandList commands, int count)
    {
        for (int c = 0; c < count; c++)
            foreach (var draw in _coverageDraws)
            {
                if (draw.ShadowKind == ShadowCastKind.None) continue;
                _model.DrawCoverageCascade(commands, draw, c, draw.ShadowKind != ShadowCastKind.Opaque,
                    draw.ShadowKind == ShadowCastKind.DissolvingInverted);
                _shadowPassSkinnedDraws++; CountSkinnedDraw(draw.CornerCount);
            }
    }

    bool PointRequestTouchesCoverage(in PointShadowRequest request)
    {
        foreach (var draw in _coverageDraws)
            if (draw.ShadowKind != ShadowCastKind.None && draw.Sphere.TouchesShadowingShell(request.Position,
                request.Radius, request.NearRadius, request.ExclusionMin, request.ExclusionMax)) return true;
        return false;
    }

    void PrepareCoveragePointCasters(IGpuCommandList commands, PointShadowRenderer renderer)
    {
        if (_coverageDraws.Count == 0) return;
        renderer.CoveragePullObserverForTests = _coverageObservePass;
        foreach (var draw in _coverageDraws)
        {
            renderer.PackCoverage(draw);
            var instance = _coverageInstances[(int)draw.InstanceSlot];
            renderer.PackSkinnedCaster((uint)_gpuSkinnedDraws.Count + draw.InstanceSlot,
                instance.Model, instance.Dissolve.X, instance.DissolveComplement);
        }
        renderer.UploadCoverage(commands);
        renderer.UploadSkinnedCasters(commands);
    }

    int DrawCoveragePointFace(IGpuCommandList commands, PointShadowRenderer renderer,
        in PackedPointShadowSlot packed, int face, in PointShadowRequest request)
    {
        int count = 0;
        foreach (var draw in _coverageDraws)
        {
            if (draw.ShadowKind == ShadowCastKind.None || !draw.Sphere.TouchesShadowingShell(request.Position,
                request.Radius, request.NearRadius, request.ExclusionMin, request.ExclusionMax)) continue;
            renderer.BeginCoverageFace(commands, packed.Atlas, packed.PackedIndex * PointShadowMath.FaceCount + face,
                face, packed.Row, draw.ShadowKind);
            renderer.DrawCoverageCaster(commands, draw, (uint)_gpuSkinnedDraws.Count + draw.InstanceSlot);
            count++;
        }
        return count;
    }
}
