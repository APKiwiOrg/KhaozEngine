using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalDetailUpdateGpuTests
{
    [GpuTheory]
    [InlineData("removed-channel")]
    [InlineData("hdr-overflow")]
    [InlineData("subnormal")]
    [InlineData("maximum")]
    [InlineData("mean-between-halves")]
    public void The_gpu_update_keeps_the_mean_and_format_range_for_adversarial_stored_values(string cell)
    {
        // The last cell's four stored halves have a mean no half float holds. The update subtracts the float32
        // mean from each fetch before writing its four outputs, so rounding it would move every child of that cell.
        float r = cell switch { "removed-channel" or "mean-between-halves" => 1f / 1024f, "hdr-overflow" => 23008f,
            "subnormal" => (float)Half.Epsilon, "maximum" => 65504f, _ => throw new ArgumentOutOfRangeException(nameof(cell)) };
        Vector4 values = cell switch { "removed-channel" => new Vector4(0f, 0.25f, 0.75f, 3f),
            "mean-between-halves" => new Vector4(1f, 1f, 1f, (float)Half.BitIncrement((Half)1f)),
            _ => new Vector4(65504f, 0f, 0f, 0f) };
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = gpu.GpuDevice;
        IGpuResourceFactory f = gd.Factory;
        using IGpuTexture history = Texture(2, 2, GpuPixelFormat.R16G16B16A16Float);
        using IGpuTexture result = Texture(1, 1, GpuPixelFormat.R16G16B16A16Float);
        using IGpuTexture mean = Texture(1, 1, GpuPixelFormat.R32G32B32A32Float);
        using IGpuTexture transport = Texture(1, 1, GpuPixelFormat.R32G32B32A32Float);
        using IGpuTexture target = Texture(2, 2, GpuPixelFormat.R16G16B16A16Float);
        using IGpuTexture child0 = Texture(1, 1, TemporalFormats.HistoryColor);
        using IGpuTexture child1 = Texture(1, 1, TemporalFormats.HistoryColor);
        using IGpuTexture child2 = Texture(1, 1, TemporalFormats.HistoryColor);
        using IGpuTexture child3 = Texture(1, 1, TemporalFormats.HistoryColor);
        using IGpuFramebuffer children = f.CreateFramebuffer(null, child0, child1, child2, child3);
        using IGpuFramebuffer framebuffer = f.CreateFramebuffer(null, target);
        using IGpuShaderSet shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, TransportFragment);
        using IGpuShaderSet copy = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalDetailUpdateFrag);
        using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            T("DetailColor"), T("Result"), T("Mean"), T("Transport"),
            new GpuResourceLayoutElement("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        using IGpuSampler sampler = f.CreateSampler(GpuSamplerDescription.Linear);
        using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout,
            history, result, mean, transport, sampler));
        using IGpuPipeline pipeline = TemporalResolveRenderer.Fullscreen(f, shaders, layout, children.Outputs);
        using IGpuResourceLayout copyLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            T("Child0"), T("Child1"), T("Child2"), T("Child3"),
            new GpuResourceLayoutElement("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        using IGpuResourceSet copySet = f.CreateResourceSet(new GpuResourceSetDescription(copyLayout,
            child0, child1, child2, child3, sampler));
        using IGpuPipeline copyPipeline = TemporalResolveRenderer.Fullscreen(f, copy, copyLayout, framebuffer.Outputs);
        float h = ((values.X + values.Y) + (values.Z + values.W)) * 0.25f;
        TemporalTextureIo.Upload(gd, history,
            [values.X, 2f, 0f, 0f, values.Y, 2f, 0f, 0.25f, values.Z, 2f, 0f, 0.75f, values.W, 2f, 0f, 1f]);
        TemporalTextureIo.Upload(gd, result, [r, 2f, 0f, 0.5f]);
        TemporalTextureIo.Upload(gd, mean, [h, 2f, 0f, 0.5f]);
        TemporalTextureIo.Upload(gd, transport, [0.5f, 0.5f, 0.875f, 1f]);
        using IGpuCommandList cl = f.CreateCommandList();
        using (GpuRecording.Open(gd, cl, nameof(TemporalDetailUpdateGpuTests)))
        {
            cl.SetFramebuffer(children);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, set);
            cl.Draw(3);
            cl.SetFramebuffer(framebuffer);
            cl.SetPipeline(copyPipeline);
            cl.SetGraphicsResourceSet(0, copySet);
            cl.Draw(3);
        }
        gd.Submit(cl);
        gd.WaitForIdle();
        float[] stored = TemporalTextureIo.Read(gd, target);
        Assert.Equal(16, stored.Length);
        Vector4 expected = TemporalDetailMath.Transport(r, values, 0.875f);
        Vector4 alpha = TemporalDetailMath.Transport(0.5f, new Vector4(0f, 0.25f, 0.75f, 1f), 0.875f);
        double sum = 0;
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(stored[i * 4], 0f, 65504f);
            Assert.Equal((Half)expected[i], (Half)stored[i * 4]);
            Assert.Equal(2f, stored[i * 4 + 1]);
            Assert.Equal(0f, stored[i * 4 + 2]);
            Assert.Equal((Half)alpha[i], (Half)stored[i * 4 + 3]);
            sum += stored[i * 4];
        }
        double step = r == 65504f ? 32.0 : (double)(float)Half.BitIncrement((Half)r) - r;
        Assert.True(Math.Abs(sum / 4 - r) <= step, $"{cell}: stored mean {sum / 4:R}, R {r:R}, step {step:R}");

        IGpuTexture Texture(uint width, uint height, GpuPixelFormat format) => f.CreateTexture(
            GpuTextureDescription.Texture2D(width, height, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
    }

    // The production transport helper is now called by the display-grid resolve, then the fine store copies it.
    const string TransportFragment = @"#version 450
layout(set=0, binding=0) uniform texture2D DetailColor;
layout(set=0, binding=1) uniform texture2D Result;
layout(set=0, binding=2) uniform texture2D Mean;
layout(set=0, binding=3) uniform texture2D Transport;
layout(set=0, binding=4) uniform sampler LinearClamp;
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oChild0;
layout(location=1) out vec4 oChild1;
layout(location=2) out vec4 oChild2;
layout(location=3) out vec4 oChild3;
" + TemporalDetailMath.Glsl + ShaderSources.TemporalDetailFetchGlsl + ShaderSources.TemporalDetailChildrenGlsl + @"
void main() {
    vec4 r = texelFetch(sampler2D(Result, LinearClamp), ivec2(0), 0);
    vec4 mean = texelFetch(sampler2D(Mean, LinearClamp), ivec2(0), 0);
    vec4 t = texelFetch(sampler2D(Transport, LinearClamp), ivec2(0), 0);
    detailMeanAt(t.xy, vec2(1.0));
    detailChildren(r, mean, detailA, detailB, detailC, detailD, t.z, oChild0, oChild1, oChild2, oChild3);
    oChild0 += vec4(vUv.x * 1.0e-30);
}";

    static GpuResourceLayoutElement T(string name) => new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
}
