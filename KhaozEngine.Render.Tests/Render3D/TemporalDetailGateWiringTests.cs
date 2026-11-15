using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalDetailGateWiringTests
{
    [Theory]
    [InlineData("out vec4 oClassification;")]
    [InlineData("layout(location=3) out vec4 oTransport;")]
    [InlineData("ivec2 texel = ivec2(gl_FragCoord.xy), pixel = texel / 2;")]
    public void Fine_programs_use_the_renderer_source_rewrite(string programMarker)
    {
        using var device = new FakeGpuDevice();
        using var color = Texture(GpuPixelFormat.R16G16B16A16Float);
        using var opaque = Texture(GpuPixelFormat.R16G16B16A16Float);
        using var depth = Texture(GpuPixelFormat.R32Float);
        using var motion = Texture(GpuPixelFormat.R16G16Float);
        var history = new TemporalHistory();
        history.EnsureTargets(device, 96, 64, 96, 64, detail: TemporalHistoryDetail.Fine);
        try
        {
            using var renderer = new TemporalResolveRenderer(device, source =>
                source.Contains(programMarker, StringComparison.Ordinal) ? throw new GateRewriteObserved() : source);
            var inputs = new TemporalResolveInputs(color, opaque, depth, motion);
            Assert.Throws<GateRewriteObserved>(() => renderer.BindInputs(inputs, history));
        }
        finally { history.ReleaseTargets(); }

        IGpuTexture Texture(GpuPixelFormat format) => device.Factory.CreateTexture(
            GpuTextureDescription.Texture2D(96, 64, format, GpuTextureUsage.Sampled));
    }

    sealed class GateRewriteObserved : Exception { }
}
