using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>
/// The <see cref="SceneDebugView.MotionVectors"/> pass: one fullscreen triangle that replaces the final image with the
/// motion target (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 5). Built the first time the view is drawn, so a scene
/// that never selects it creates nothing. Mirrors the fullscreen plumbing of <see cref="TransitionRenderer"/>.
/// </summary>
internal sealed class MotionVectorsView : IDisposable
{
    readonly IGpuDevice _gd;
    readonly IGpuShaderSet _shaders;
    readonly IGpuResourceLayout _layout;
    readonly IGpuPipeline _pipeline;
    IGpuResourceSet? _set;
    IGpuTexture? _source;

    public MotionVectorsView(IGpuDevice gd, GpuOutputDescription targetOutput)
    {
        _gd = gd;
        IGpuResourceFactory f = gd.Factory;
        _shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.MotionVectorsViewFrag);
        _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Motion", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        _pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },   // replaces the final image
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: false, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { _layout },
            ShaderSet = _shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>(),   // fullscreen triangle from gl_VertexIndex
            Outputs = targetOutput,
        });
    }

    /// <summary>Paint <paramref name="motion"/> over <paramref name="target"/>. The resource set follows the motion
    /// texture, which a resize replaces, the way <see cref="PixelPostProcess"/> rebinds its sets.</summary>
    public void Draw(IGpuCommandList cl, IGpuTexture motion, IGpuFramebuffer target)
    {
        if (!ReferenceEquals(motion, _source))
        {
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, motion, _gd.PointSampler));
            _source = motion;
        }
        cl.SetFramebuffer(target);
        cl.SetPipeline(_pipeline);
        cl.SetGraphicsResourceSet(0, _set!);
        cl.Draw(3);
    }

    public void Dispose()
    {
        _set?.Dispose();
        _pipeline.Dispose();
        _layout.Dispose();
        _shaders.Dispose();
    }
}
