using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    void PrepareCoverageDraws(IGpuCommandList commands, bool cascades)
    {
        if (_coverageDraws.Count == 0) return;
        for (int i = 0; i < _coverageDraws.Count; i++)
        {
            var draw = _coverageDraws[i];
            var instance = _coverageInstances[i];
            _model.PackCoverageSurface(draw);
            if (!cascades || draw.ShadowKind == ShadowCastKind.None) continue;
            for (int c = 0; c < _cascadeCount; c++)
                _model.PackCoverageCascade(draw, instance, c, _coverageDraws.Count, _cascadeDepthVps[c],
                    _frameOrigin, _cascadeNoiseScales[c]);
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
                _shadowPassSkinnedDraws++;
                CountSkinnedDraw(draw.CornerCount);
            }
    }
}
