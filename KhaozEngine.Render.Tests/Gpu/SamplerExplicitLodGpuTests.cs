using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// AN EXPLICIT LOD OF 1 READS MIP LEVEL 1, THROUGH EVERY KIND OF SAMPLER, ON WHATEVER BACKEND IS RUNNING
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/1192).
    /// <para>
    /// The native Direct3D 11 backend handed the driver Vortice's <c>SamplerDescription</c>, whose layout put a
    /// maximum LOD of 0 where the driver reads it, so every sampler clamped to level 0, on WARP and on hardware.
    /// This isolates exactly that. The source is 2x2 with two levels, level 0 black and level 1 white, and level 1
    /// is uploaded directly with no <c>GenerateMips</c>, so nothing but the sampler's LOD range decides the
    /// answer. Clamped, the tap reads black. Correct, it reads white.
    /// </para>
    /// <para>
    /// Three samplers, because the defect sat under all of them: the device's shared linear and point pair, which
    /// are built at device creation, and one the factory builds on request in the ground's shape (anisotropic 16x,
    /// wrap, mip LOD bias 1, the shared terrain sampler's description). Its bias asks for level 2, and the texture
    /// has no level past 1, so it still reads level 1 where the backend honours a bias and where it drops one. The
    /// coordinate and the LOD are literals, so every fragment asks the same question and any pixel of the readback
    /// answers it.
    /// </para>
    /// </summary>
    public sealed class SamplerExplicitLodGpuTests
    {
        const uint Size = 4;

        const string Lod1Frag = @"#version 450
layout(set = 0, binding = 0) uniform texture2D Src;
layout(set = 0, binding = 1) uniform sampler Samp;
layout(location = 0) out vec4 oColor;

void main() {
    oColor = textureLod(sampler2D(Src, Samp), vec2(0.5, 0.5), 1.0);
}
";

        [GpuFact]
        public void An_explicit_lod_of_one_reads_mip_level_one_through_every_sampler()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            IGpuResourceFactory f = dev.Factory;

            using IGpuTexture src = f.CreateTexture(new GpuTextureDescription(
                2, 2, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, mipLevels: 2));
            byte[] black = { 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255 };
            dev.UpdateTexture(src, black, 0, 0, 2, 2, mipLevel: 0, arrayLayer: 0);
            dev.UpdateTexture(src, new byte[] { 255, 255, 255, 255 }, 0, 0, 1, 1, mipLevel: 1, arrayLayer: 0);

            using IGpuSampler factorySampler = f.CreateSampler(new GpuSamplerDescription(GpuSamplerFilter.Anisotropic,
                GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, maximumAnisotropy: 16,
                mipLodBias: 1));

            using IGpuTexture colour = f.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, colour);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Src", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(ComputeShaders.FullscreenVert, Lod1Frag);
            using IGpuPipeline pipe = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = fb.Outputs,
            });

            var samplers = new (string Name, IGpuSampler Sampler)[]
            {
                ("device LinearSampler", dev.LinearSampler),
                ("device PointSampler", dev.PointSampler),
                ("factory anisotropic sampler", factorySampler),
            };

            var reads = new List<string>();
            bool allWhite = true;
            foreach ((string name, IGpuSampler sampler) in samplers)
            {
                using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, src, sampler));
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
                reads.Add($"{name} ({r},{g},{b})");
                allWhite &= r > 200 && g > 200 && b > 200;
            }

            Assert.True(allWhite,
                $"{dev.Backend}: an explicit LOD of 1 on a 2x2 texture whose level 0 is black and level 1 is white "
                + "read [" + string.Join(", ", reads) + "]. White is level 1. Black means the sampler reached the "
                + "driver with a maximum LOD of 0 and clamped to level 0, which is issue #1192 on Direct3D 11.");
        }
    }
}
