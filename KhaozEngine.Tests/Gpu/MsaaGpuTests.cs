using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Phase 3 of the AA-options work, at the Gpu-abstraction layer: proves the MSAA plumbing added to the engine's
    // GPU abstraction actually works on a live device - a multisampled render target reports its sample count, a
    // framebuffer accepts it, it can be cleared, and ResolveTexture resolves it into a single-sample texture the CPU
    // reads back. (The Scene3D end-to-end "MSAA anti-aliases edges" test lives separately.) Skipped unless
    // KE_GPU_TESTS=1.
    public sealed class MsaaGpuTests
    {
        const int W = 64, H = 64;

        [GpuFact]
        public void Device_reports_an_msaa_limit()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            // Every desktop GPU the engine targets supports at least 4x MSAA; the limit is a power of two >= 1.
            int max = ctx.GpuDevice.Capabilities.MaxMsaaSampleCount;
            Assert.True(max >= 2, $"device should support MSAA, MaxMsaaSampleCount={max}");
            Assert.True((max & (max - 1)) == 0, $"MaxMsaaSampleCount should be a power of two, got {max}");
        }

        // Trivial fullscreen-triangle shaders (one solid colour), so the test exercises the real DRAW -> resolve path
        // (a clear-only MSAA pass can be optimised away on Metal; the scene always draws).
        const string Vert = @"#version 450
void main() { vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }";
        const string Frag = @"#version 450
layout(location=0) out vec4 o; void main() { o = vec4(0.1, 0.6, 0.7, 1.0); }";

        [GpuFact]
        public void Multisampled_target_draws_and_resolves_to_single_sample()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            var f = gd.Factory;

            uint samples = (uint)System.Math.Min(4, gd.Capabilities.MaxMsaaSampleCount);
            Assert.True(samples >= 2);

            // Multisampled colour target (render target only; a multisampled texture cannot be sampled directly)...
            using IGpuTexture ms = f.CreateTexture(new GpuTextureDescription(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget, mipLevels: 1, arrayLayers: 1, sampleCount: samples));
            Assert.Equal(samples, ms.SampleCount);
            using IGpuFramebuffer msFb = f.CreateFramebuffer(null, ms);
            Assert.Equal(samples, (uint)msFb.Outputs.SampleCount);   // the framebuffer's outputs carry the count (drives pipelines)

            // ...resolved into a single-sample texture the CPU can read back.
            using IGpuTexture resolved = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            Assert.Equal(1u, resolved.SampleCount);

            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(Vert, Frag);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription());
            using IGpuPipeline pipe = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new IGpuResourceLayout[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = msFb.Outputs,   // sample count matches the multisampled framebuffer
            });

            using IGpuCommandList cl = f.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(msFb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetPipeline(pipe);
            cl.SetGraphicsResourceSet(0, f.CreateResourceSet(new GpuResourceSetDescription(layout)));
            cl.Draw(3);                          // fill every sample with teal
            cl.ResolveTexture(ms, resolved);     // average of identical samples = teal
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();

            byte[] px = GpuReadback.ToRgba(gd, resolved, W, H);
            int i = ((H / 2) * W + (W / 2)) * 4;
            Assert.InRange(px[i + 0], 15, 45);    // ~0.1 * 255
            Assert.InRange(px[i + 1], 140, 165);  // ~0.6 * 255
            Assert.InRange(px[i + 2], 165, 190);  // ~0.7 * 255
        }
    }
}
