using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Ordinary DQ material variants. LBS and DQ use independent programs, pipelines and palette layouts.
/// Resources are created only when a DQ draw reaches this internal consumer.</summary>
internal sealed partial class ModelRenderer
{
    IGpuShaderSet? _dqSkinnedShaders, _dqSkinnedDissolveShaders;
    IGpuPipeline? _dqSkinnedPipeline, _dqSkinnedDissolvePipeline;
    GpuOutputDescription _dqMaterialOutputs;
    GpuVertexLayoutDescription _dqMaterialVertexLayout;

    void SetDualQuaternionOutputs(GpuOutputDescription outputs, GpuVertexLayoutDescription vertices)
    {
        DisposeDualQuaternionPipelines();
        _dqMaterialOutputs = outputs;
        _dqMaterialVertexLayout = vertices;
    }

    void EnsureDualQuaternionPipelines()
    {
        if (_motion is not null)
            throw new NotSupportedException("DQ material motion is unavailable until the DQ temporal consumer is ready.");
        if (_dqSkinnedPipeline is not null) return;
        var factory = _gd.Factory;
        _dqSkinnedShaders ??= factory.CreateShadersFromSpirv(ShaderSources.DqSkinnedModelVert, ShaderSources.SkinnedModelFrag);
        _dqSkinnedDissolveShaders ??= factory.CreateShadersFromSpirv(ShaderSources.DqSkinnedModelVert, ShaderSources.SkinnedModelDissolveFrag);
        IGpuResourceLayout[] layouts = [_skinnedMainLayout, _skinnedFragLayout, _dqPalette.Layout];
        var vertices = new List<GpuVertexLayoutDescription> { _dqMaterialVertexLayout };
        IGpuPipeline ordinary = OpaqueMotionPipeline(factory, _dqMaterialOutputs, _dqSkinnedShaders, layouts, vertices);
        try
        {
            _dqSkinnedDissolvePipeline = OpaqueMotionPipeline(factory, _dqMaterialOutputs, _dqSkinnedDissolveShaders, layouts, vertices);
            _dqSkinnedPipeline = ordinary;
        }
        catch
        {
            ordinary.Dispose();
            throw;
        }
    }

    internal void BindSkinnedMaterialPass(IGpuCommandList commands, SkinningProfile profile, bool dissolve)
    {
        if (profile.Method == SkinningMethod.LinearBlend)
        {
            if (dissolve) BindSkinnedDissolvePass(commands);
            else BindSkinnedPass(commands);
            return;
        }
        EnsureDualQuaternionPipelines();
        commands.SetPipeline(dissolve ? _dqSkinnedDissolvePipeline! : _dqSkinnedPipeline!);
    }

    void DisposeDualQuaternionPipelines()
    {
        _dqSkinnedPipeline?.Dispose();
        _dqSkinnedDissolvePipeline?.Dispose();
        _dqSkinnedPipeline = _dqSkinnedDissolvePipeline = null;
    }

    void DisposeDualQuaternionMaterials()
    {
        DisposeDualQuaternionPipelines();
        _dqSkinnedShaders?.Dispose();
        _dqSkinnedDissolveShaders?.Dispose();
    }
}
