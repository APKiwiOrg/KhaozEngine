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
    public void The_gpu_update_keeps_the_mean_and_format_range_for_adversarial_stored_values(string cell)
    {
        float r = cell switch { "removed-channel" => 1f / 1024f, "hdr-overflow" => 23008f,
            "subnormal" => (float)Half.Epsilon, "maximum" => 65504f, _ => throw new ArgumentOutOfRangeException(nameof(cell)) };
        Vector4 values = cell == "removed-channel" ? new Vector4(0f, 0.25f, 0.75f, 3f)
            : new Vector4(65504f, 0f, 0f, 0f);
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice gd = gpu.GpuDevice;
        IGpuResourceFactory f = gd.Factory;
        using IGpuTexture history = Texture(2, 2, GpuPixelFormat.R16G16B16A16Float);
        using IGpuTexture result = Texture(1, 1, GpuPixelFormat.R16G16B16A16Float);
        using IGpuTexture mean = Texture(1, 1, GpuPixelFormat.R32G32B32A32Float);
        using IGpuTexture transport = Texture(1, 1, GpuPixelFormat.R32G32B32A32Float);
        using IGpuTexture target = Texture(2, 2, GpuPixelFormat.R16G16B16A16Float);
        using IGpuFramebuffer framebuffer = f.CreateFramebuffer(null, target);
        using IGpuShaderSet shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalDetailUpdateFrag);
        using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            T("DetailColor"), T("Result"), T("Mean"), T("Transport"),
            new GpuResourceLayoutElement("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        using IGpuSampler sampler = f.CreateSampler(GpuSamplerDescription.Linear);
        using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout,
            history, result, mean, transport, sampler));
        using IGpuPipeline pipeline = TemporalResolveRenderer.Fullscreen(f, shaders, layout, framebuffer.Outputs);
        float h = ((values.X + values.Y) + (values.Z + values.W)) * 0.25f;
        TemporalTextureIo.Upload(gd, history,
            [values.X, 2f, 0f, 0f, values.Y, 2f, 0f, 0.25f, values.Z, 2f, 0f, 0.75f, values.W, 2f, 0f, 1f]);
        TemporalTextureIo.Upload(gd, result, [r, 2f, 0f, 0.5f]);
        TemporalTextureIo.Upload(gd, mean, [h, 2f, 0f, 0.5f]);
        TemporalTextureIo.Upload(gd, transport, [0.5f, 0.5f, 0.875f, 1f]);
        using IGpuCommandList cl = f.CreateCommandList();
        using (GpuRecording.Open(gd, cl, nameof(TemporalDetailUpdateGpuTests)))
        {
            cl.SetFramebuffer(framebuffer);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, set);
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

    static GpuResourceLayoutElement T(string name) => new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment);
}
