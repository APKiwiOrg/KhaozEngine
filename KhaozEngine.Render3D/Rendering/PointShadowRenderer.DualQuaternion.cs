using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Lazy ordinary DQ point-depth variants. Face scheduling, caster headers and the current palette
/// remain shared with the other passes. No matrix program or buffer is reinterpreted as DQ.</summary>
internal sealed partial class PointShadowRenderer
{
    readonly IGpuShaderSet?[] _dqPointShaders = new IGpuShaderSet[3];
    readonly IGpuPipeline?[] _dqPointPipelines = new IGpuPipeline[3];
    readonly GpuOutputDescription _skinnedOutputs;

    IGpuPipeline DualQuaternionPointPipeline(ShadowCastKind kind, DualQuaternionPaletteBuffer palette)
    {
        int variant = kind switch
        {
            ShadowCastKind.Opaque => 0,
            ShadowCastKind.Dissolving => 1,
            ShadowCastKind.DissolvingInverted => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No caster pipeline exists for None."),
        };
        if (_dqPointPipelines[variant] is { } cached) return cached;
        string vertex = variant == 0 ? ShaderSources.DqPointShadowSkinnedVert : ShaderSources.DqPointShadowSkinnedDissolveVert;
        string fragment = variant switch
        {
            0 => ShaderSources.PointShadowRigidFrag,
            1 => ShaderSources.PointShadowSkinnedDissolveFrag,
            _ => ShaderSources.PointShadowSkinnedDissolveInvertedFrag,
        };
        IGpuShaderSet shaders = _gd.Factory.CreateShadersFromSpirv(vertex, fragment);
        try
        {
            IGpuPipeline pipeline = BuildSkinnedPipeline(_gd.Factory, _skinnedOutputs, shaders, palette.Layout);
            _dqPointShaders[variant] = shaders;
            return _dqPointPipelines[variant] = pipeline;
        }
        catch
        {
            shaders.Dispose();
            throw;
        }
    }

    internal void BeginGpuSkinnedFace(IGpuCommandList cl, PointShadowAtlas atlas, int packedFaceIndex,
        int face, int row, ShadowCastKind kind, SkinningProfile profile, DualQuaternionPaletteBuffer palette)
    {
        if (profile.Method == SkinningMethod.LinearBlend)
        {
            BeginGpuSkinnedFace(cl, atlas, packedFaceIndex, face, row, kind);
            return;
        }
        cl.SetPipeline(DualQuaternionPointPipeline(kind, palette));
        BindSkinnedFaceWindow(cl, atlas, packedFaceIndex, face, row);
    }

    void DisposeDualQuaternionPointResources()
    {
        foreach (IGpuPipeline? pipeline in _dqPointPipelines) pipeline?.Dispose();
        foreach (IGpuShaderSet? shaders in _dqPointShaders) shaders?.Dispose();
    }
}
