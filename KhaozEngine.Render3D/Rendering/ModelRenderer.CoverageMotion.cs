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
        _coverageMotionPipeline?.Dispose(); _coverageDissolveMotionPipeline?.Dispose();
        _coverageMotionPipeline = _coverageDissolveMotionPipeline = null;
        if (_motion is null) return;
        _coverageMotionShaders ??= factory.CreateShadersFromSpirv(ShaderSources.CoverageModelMotionVert,
            ShaderSources.SkinnedModelMotionFrag, new(PreciseMath: true));
        _coverageDissolveMotionShaders ??= factory.CreateShadersFromSpirv(ShaderSources.CoverageModelMotionVert,
            ShaderSources.SkinnedModelDissolveMotionFrag, new(PreciseMath: true));
        _coverageMotionPipeline = Create(_coverageMotionShaders);
        _coverageDissolveMotionPipeline = Create(_coverageDissolveMotionShaders);
        IGpuPipeline Create(IGpuShaderSet shaders) => OpaqueMotionPipeline(factory, outputs, shaders,
            [_skinnedMainLayout, _skinnedFragLayout, _coveragePull.Layout, _motion.FrameLayout], []);
    }

    internal void DrawCoverageMotion(IGpuCommandList commands, in CoveragePulledDraw draw, in Matrix4x4 previousRenderModel)
        => DrawCoverageMotion(commands, draw, previousRenderModel, null);

    internal void DrawCoverageMotion(IGpuCommandList commands, in CoveragePulledDraw draw, in Matrix4x4 previousRenderModel,
        IGpuResourceSet? material)
    {
        _coveragePull.UploadPreviousModel(commands, draw.InstanceSlot, previousRenderModel);
        commands.SetPipeline(_coverageDissolving[draw.InstanceSlot] ? _coverageDissolveMotionPipeline! : _coverageMotionPipeline!);
        commands.SetGraphicsResourceSet(0, _skinnedMainSet!, draw.InstanceSlot * SkinnedMainSlotBytes);
        commands.SetGraphicsResourceSet(1, material ?? _skinnedDefaultFragSet);
        commands.SetGraphicsResourceSet(3, _motion!.FrameSet);
        _coveragePull.Bind(commands, 2, draw.InstanceSlot);
        CoveragePullObserverForTests?.Invoke("motion", draw);
        commands.Draw((uint)draw.CornerCount, 1, 0, 0);
    }

    void DisposeCoverageMotion()
    {
        _coverageMotionPipeline?.Dispose(); _coverageDissolveMotionPipeline?.Dispose();
        _coverageMotionShaders?.Dispose(); _coverageDissolveMotionShaders?.Dispose();
    }
}
