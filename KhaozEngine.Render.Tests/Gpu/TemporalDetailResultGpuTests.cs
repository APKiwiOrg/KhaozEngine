using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalDetailResultGpuTests
{
    [GpuFact]
    public void Local_result_conversion_matches_the_history_attachment()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var gd = gpu.GpuDevice;
        var f = gd.Factory;
        using var source = Texture(GpuPixelFormat.R32G32B32A32Float, GpuTextureUsage.Sampled);
        using var raw = Texture(TemporalFormats.HistoryColor, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled);
        using var rounded = Texture(TemporalFormats.HistoryColor, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled);
        using var target = f.CreateFramebuffer(null, raw, rounded);
        using var shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
            "#version 450\n" + (gd.Backend.IsMetal() ? "#define DetailTruncateResult\n" : "") + @"
layout(set=0,binding=0) uniform texture2D Input;
layout(set=0,binding=1) uniform sampler LinearClamp;
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oRaw;
layout(location=1) out vec4 oRound;
" + TemporalDetailMath.Glsl + ShaderSources.TemporalDetailStoredResultGlsl + @"
void main() {
    vec4 v = texelFetch(sampler2D(Input,LinearClamp),ivec2(gl_FragCoord.xy),0);
    oRaw = v + vec4(vUv.x * 1.0e-30);
    oRound = detailStoredResult(v);
}");
        using var layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Input", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        using var sampler = f.CreateSampler(GpuSamplerDescription.Point);
        using var set = f.CreateResourceSet(new GpuResourceSetDescription(layout, source, sampler));
        using var pipeline = TemporalResolveRenderer.Fullscreen(f, shaders, layout, target.Outputs);
        // Straddle normal and subnormal half boundaries. Nearest-even and attachment truncation disagree here.
        float[] values = new float[4096];
        for (int i = 0; i < values.Length; i++)
            values[i] = i < 2048 ? 0.5f + i / 8192f + (i % 7 - 3) / 67108864f
                : (i - 2048) / 16777216f + (i % 7 - 3) / 268435456f;
        TemporalTextureIo.Upload(gd, source, values);
        using var cl = f.CreateCommandList();
        using (GpuRecording.Open(gd, cl, nameof(TemporalDetailResultGpuTests)))
        {
            cl.SetFramebuffer(target);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, set);
            cl.Draw(3);
        }
        gd.Submit(cl);
        gd.WaitForIdle();
        float[] actual = TemporalTextureIo.Read(gd, raw), local = TemporalTextureIo.Read(gd, rounded);
        Assert.Equal(values.Length, actual.Length);
        Assert.Equal(actual, local);

        IGpuTexture Texture(GpuPixelFormat format, GpuTextureUsage usage) =>
            f.CreateTexture(GpuTextureDescription.Texture2D(1024, 1, format, usage));
    }
}
