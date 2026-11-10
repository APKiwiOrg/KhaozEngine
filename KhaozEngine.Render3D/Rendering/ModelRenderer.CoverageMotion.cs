using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    IGpuShaderSet? _coverageMotionShaders, _coverageDissolveMotionShaders;
    IGpuPipeline? _coverageMotionPipeline, _coverageDissolveMotionPipeline;

    void BuildCoverageMotionPipelines(IGpuResourceFactory factory, GpuOutputDescription outputs)
    {
        _retired.Retire(_coverageMotionPipeline); _retired.Retire(_coverageDissolveMotionPipeline);
        _coverageMotionPipeline = _coverageDissolveMotionPipeline = null;
        if (_motion is null) return;
        _coverageMotionShaders ??= factory.CreateShadersFromSpirv(ShaderSources.CoverageModelMotionVert,
            ShaderSources.SkinnedModelMotionFrag, CoverageVertexOptions);
        _coverageDissolveMotionShaders ??= factory.CreateShadersFromSpirv(ShaderSources.CoverageModelMotionVert,
            ShaderSources.SkinnedModelDissolveMotionFrag, CoverageVertexOptions);
        _coverageMotionPipeline = Create(_coverageMotionShaders);
        _coverageDissolveMotionPipeline = Create(_coverageDissolveMotionShaders);
        IGpuPipeline Create(IGpuShaderSet shaders) => OpaqueMotionPipeline(factory, outputs,
            _coverageVisibility.TrySelect(factory, outputs, ShaderSources.CoverageModelMotionVert,
                ReferenceEquals(shaders, _coverageDissolveMotionShaders) ? ShaderSources.SkinnedModelDissolveMotionFrag : ShaderSources.SkinnedModelMotionFrag,
                false, out var visibility, CoverageVertexOptions) ? visibility : shaders,
            [_skinnedMainLayout, _skinnedFragLayout, _coveragePull.Layout, _motion.FrameLayout], []);
    }

    internal void DrawCoverageMotion(IGpuCommandList commands, in CoveragePulledDraw draw, in Matrix4x4 previousRenderModel,
        IGpuResourceSet? material)
    {
        _coveragePull.UploadPreviousModel(commands, draw.BindingSlot, previousRenderModel);
        commands.SetPipeline(_coverageDissolving[draw.InstanceSlot] ? _coverageDissolveMotionPipeline! : _coverageMotionPipeline!);
        commands.SetGraphicsResourceSet(0, _skinnedMainSet!, (_coverageMainSlotOffset + draw.InstanceSlot) * SkinnedMainSlotBytes);
        commands.SetGraphicsResourceSet(1, material ?? _skinnedDefaultFragSet);
        commands.SetGraphicsResourceSet(3, _motion!.FrameSet);
        _coveragePull.Bind(commands, 2, draw.BindingSlot);
        commands.Draw((uint)draw.CornerCount, 1, 0, 0);
        CoveragePullObserverForTests?.Invoke("motion", new(draw));
    }

    void DisposeCoverageMotion(GpuRetireQueue? retired = null)
    {
        ResourceRetirement.Free(_coverageMotionPipeline, retired); ResourceRetirement.Free(_coverageDissolveMotionPipeline, retired);
        ResourceRetirement.Free(_coverageMotionShaders, retired); ResourceRetirement.Free(_coverageDissolveMotionShaders, retired);
        _coverageMotionPipeline = _coverageDissolveMotionPipeline = null;
        _coverageMotionShaders = _coverageDissolveMotionShaders = null;
    }
}
