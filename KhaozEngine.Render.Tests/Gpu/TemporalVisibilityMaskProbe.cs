using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The current visibility mask of every internal texel, computed inline through the shipped mask functions
/// (<see cref="ShaderSources.TemporalVisibilityMaskGlsl"/>) from this frame's depth and motion, in D's 3x3 order and
/// clamp, one texel per fragment into a single-float target. <see cref="MaskAtGlsl"/> is also the equivalence oracle's
/// way to the mask it hands the shipped decision.
/// </summary>
internal sealed class TemporalVisibilityMaskProbe : IDisposable
{
    /// <summary>The mask at a texel, read from <c>MotionTex</c> and <c>SceneDepth</c> as D's own loop reads them, and
    /// reduced by the shipped functions. A program that includes it declares both textures, <c>LinearClamp</c> and
    /// <c>CurrentDepth</c>, and includes <see cref="ShaderSources.TemporalVisibilityMaskGlsl"/> first.</summary>
    internal const string MaskAtGlsl = @"
uint inlineVisibilityMask(ivec2 centre, ivec2 last) {
    float centreDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), centre, 0).rg,
        texelFetch(sampler2D(SceneDepth, LinearClamp), centre, 0).r);
    uint accepted = 0u;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 at = clamp(centre + ivec2(x, y), ivec2(0), last);
            float depth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg,
                texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r);
            accepted |= temporalVisibilityCurrentBit(depth, centreDepth, (y + 1) * 3 + (x + 1));
        }
    }
    return temporalVisibilityCurrentMask(accepted, centreDepth);
}
";

    const string Header = @"
layout(set=0,binding=0) uniform texture2D SceneDepth;
layout(set=0,binding=1) uniform texture2D MotionTex;
layout(set=0,binding=2) uniform sampler LinearClamp;
layout(set=0,binding=3) uniform Probe { vec4 CurrentDepth; vec4 Sizes; };
";

    const string Main = @"
layout(location=0) out float oMask;
void main() {
    oMask = float(inlineVisibilityMask(ivec2(gl_FragCoord.xy), ivec2(Sizes.xy) - 1));
}
";

    internal static string FragmentSource => "#version 450\n" + Header + ShaderSources.TemporalCommonGlsl
        + ShaderSources.TemporalResolveTuningGlsl + ShaderSources.TemporalVisibilityMaskGlsl + MaskAtGlsl + Main;

    readonly IGpuDevice _gd;
    readonly List<IDisposable> _owned = [];
    readonly IGpuResourceLayout _layout;
    readonly IGpuPipeline _pipeline;
    readonly IGpuFramebuffer _framebuffer;
    readonly IGpuBuffer _uniforms;
    readonly IGpuTexture _depth, _motion, _mask;
    readonly int _width, _height;

    /// <summary>A probe over <paramref name="width"/> by <paramref name="height"/> internal texels on
    /// <paramref name="gd"/>.</summary>
    internal TemporalVisibilityMaskProbe(IGpuDevice gd, int width, int height)
    {
        _gd = gd;
        _width = width;
        _height = height;
        IGpuResourceFactory f = gd.Factory;
        _depth = Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height, GpuPixelFormat.R32Float,
            GpuTextureUsage.Sampled)));
        _motion = Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R16G16Float, GpuTextureUsage.Sampled)));
        _mask = Own(f.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height, GpuPixelFormat.R32Float,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
        _framebuffer = Own(f.CreateFramebuffer(null, _mask));
        _uniforms = Own(f.CreateBuffer(new GpuBufferDescription(32, GpuBufferUsage.UniformBuffer)));
        _layout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("SceneDepth", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("MotionTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("Probe", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment))));
        IGpuShaderSet shaders = Own(f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, FragmentSource));
        _pipeline = Own(f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [_layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = _framebuffer.Outputs,
        }));
    }

    T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    /// <summary>The inline mask of every texel for this NDC depth, motion and depth parameters, rows top to
    /// bottom.</summary>
    internal float[] Run(float[] ndc, float[] motion, System.Numerics.Vector4 currentDepth)
    {
        TemporalTextureIo.Upload(_gd, _depth, ndc);
        TemporalTextureIo.Upload(_gd, _motion, motion);
        _gd.UpdateBuffer(_uniforms, 0, new[] { currentDepth.X, currentDepth.Y, currentDepth.Z, currentDepth.W,
            _width, _height, 0f, 0f });
        using IGpuResourceSet set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _depth, _motion,
            _gd.LinearSampler, _uniforms));
        using (IGpuCommandList commands = _gd.Factory.CreateCommandList())
        {
            using (GpuRecording.Open(_gd, commands, nameof(TemporalVisibilityMaskProbe)))
            {
                commands.SetFramebuffer(_framebuffer);
                commands.SetPipeline(_pipeline);
                commands.SetGraphicsResourceSet(0, set);
                commands.Draw(3);
            }
            _gd.Submit(commands);
            _gd.WaitForIdle();
        }
        return TemporalTextureIo.Read(_gd, _mask);
    }

    public void Dispose()
    {
        _gd.WaitForIdle();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
}
