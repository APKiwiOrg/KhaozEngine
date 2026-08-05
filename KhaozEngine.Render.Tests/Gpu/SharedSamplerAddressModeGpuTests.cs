using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SEAM CONTRACT ON <see cref="IGpuDevice.LinearSampler"/>, ON A REAL DEVICE, ON WHATEVER BACKEND IS
    /// RUNNING: the shared pair the device owns is WRAP-addressed, so a tap past the last texel centre blends
    /// toward the texel on the OTHER side of the texture rather than holding the edge.
    ///
    /// <para><b>The arithmetic, which is why the texture is 2x1.</b> Two texels, centres at u = 0.25 and u = 0.75.
    /// The draw samples at u = 1.25. Wrapped, that is u = 0.25, exactly texel 0's centre, so the result is texel 0
    /// alone. Clamped, it is u = 1.0, past texel 1's centre with nothing beyond it, so the result is texel 1 alone.
    /// Texel 0 is red and texel 1 is blue, which makes the two answers the two extremes of the same channel pair
    /// rather than a shade apart, and the assertion a wide one.</para>
    ///
    /// <para><b>Why it is worth a device.</b> This is exactly what the native Direct3D 11 backend got wrong: it
    /// built its shared pair from the engine's <c>GpuSamplerDescription.Point</c> / <c>.Linear</c> statics, which
    /// are documented CLAMP on every axis, where the incumbent's pair comes from Veldrid's identically named
    /// built-ins, which are WRAP. Nothing throws, nothing logs, and the only witnesses were two goldens on CI run
    /// 30963173087 (<c>scene3d_texbillboard</c> worst 0.393, <c>scene3d_particles_flipbook</c> worst 0.359). This
    /// test fails on the pre-fix native backend and passes on Metal, Vulkan and both Direct3D 11 paths, because
    /// wrap is the cross-backend contract rather than one backend's habit.</para>
    ///
    /// <para>Deliberately minimal: one 2x1 texture, one fullscreen draw through the DEVICE's own shared sampler
    /// (not a sampler this test creates, which would test nothing), one readback.</para>
    /// </summary>
    public sealed class SharedSamplerAddressModeGpuTests
    {
        const uint Size = 8;

        /// <summary>Samples the 2x1 source at a FIXED coordinate a full texture past the last texel centre. The
        /// coordinate is a literal so the derivatives are zero and the tap is on mip 0, and it is the same for
        /// every fragment so any pixel of the readback answers the question.</summary>
        const string PastTheEdgeFrag = @"#version 450
layout(set = 0, binding = 0) uniform texture2D Src;
layout(set = 0, binding = 1) uniform sampler Samp;
layout(location = 0) out vec4 oColor;

void main() {
    oColor = texture(sampler2D(Src, Samp), vec2(1.25, 0.5));
}
";

        [GpuFact]
        public void TheDevicesSharedLinearSamplerWrapsPastTheLastTexel()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            IGpuResourceFactory f = dev.Factory;

            // Texel 0 red, texel 1 blue. One mip, so there is no coarser level for the tap to slide onto.
            using IGpuTexture src = f.CreateTexture(GpuTextureDescription.Texture2D(
                2, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            dev.UpdateTexture(src, new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, 0, 0, 2, 1);

            using IGpuTexture colour = f.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, colour);

            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Src", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(
                layout, src, dev.LinearSampler));
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(ComputeShaders.FullscreenVert, PastTheEdgeFrag);
            using IGpuPipeline pipe = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = fb.Outputs,
            });

            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, Color.Black);
                cl.SetPipeline(pipe);
                cl.SetGraphicsResourceSet(0, set);
                cl.Draw(3);
                cl.End();
                dev.Submit(cl);
                dev.WaitForIdle();
            }

            byte[] rgba = GpuReadback.ToRgba(dev, colour, (int)Size, (int)Size);
            int i = ((int)(Size / 2) * (int)Size + (int)(Size / 2)) * 4;
            byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];

            Assert.True(r > 200 && b < 60,
                $"{dev.Backend}: the shared linear sampler read ({r},{g},{b}) at u=1.25 on a 2x1 red/blue "
                + "texture. Wrapped that is texel 0 (255,0,0). Clamped it is texel 1 (0,0,255). A blue answer "
                + "means the device's shared sampler pair is clamp-addressed, which is the defect CI run "
                + "30963173087 caught as two moved goldens.");
        }
    }
}
