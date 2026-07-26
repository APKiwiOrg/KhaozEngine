using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Proof test (b) for the compute seam, and the one that matters most: a compute pass writes a storage texture
    /// and a GRAPHICS pass then samples it, on a real device, readback-verified. That handoff is exactly what a
    /// GPU-generated map (an FFT ocean's displacement/normal cascade, a baked spectrum, a procedural mask) does every
    /// frame, and it is the one place where Metal, Vulkan and Direct3D11 handle the hazard by three different
    /// mechanisms, none of them a call the caller makes.
    ///
    /// The pattern this test pins down, per the ordering contract on <see cref="IGpuCommandList"/>:
    /// <list type="bullet">
    ///   <item>the texture is created <c>Storage | Sampled</c> (on Vulkan the Sampled flag is what ARMS the
    ///   automatic layout restore, so a Storage-only texture would silently not get one),</item>
    ///   <item>the dispatch and the draw are recorded in ONE command list (Vulkan's restore is per-command-list
    ///   state, so splitting it across two lists silently skips the barrier),</item>
    ///   <item>the compute resource set is left bound and the pipeline simply switched (all three backends cope:
    ///   Vulkan queues the restore, Metal ends the compute encoder, Direct3D11 unbinds the UAV as the SRV binds).</item>
    /// </list>
    ///
    /// The pattern is exact by construction: the compute shader stores <c>x / 255.0</c> and <c>y / 255.0</c>, which
    /// re-quantize to exactly <c>x</c> and <c>y</c> in UNorm8, so both readbacks assert every texel rather than a
    /// tolerance. The fragment shader derives its UV from <c>gl_FragCoord</c>, which is top-left origin on all three
    /// backends, so the assertion is immune to their clip-space Y disagreement.
    /// </summary>
    public sealed class ComputeTextureHandoffGpuTests
    {
        const uint Size = 64;

        [StructLayout(LayoutKind.Sequential)]
        struct SizeParams
        {
            public uint Size;
            public uint Pad0, Pad1, Pad2;
        }

        [GpuFact]
        public void ComputeWritesAStorageTextureThatAGraphicsPassThenSamples()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");
            IGpuResourceFactory f = dev.Factory;

            // Storage | Sampled: written by compute as an image, read by graphics as a texture.
            using IGpuTexture storage = f.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Storage | GpuTextureUsage.Sampled));
            using IGpuTexture colour = f.CreateTexture(GpuTextureDescription.Texture2D(
                Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, colour);

            using IGpuBuffer sizeBuf = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            dev.UpdateBuffer(sizeBuf, 0, new SizeParams { Size = Size });

            using IGpuComputeShader computeShader = f.CreateComputeShaderFromSpirv(ComputeShaders.WriteImage);
            Assert.Equal(8u, computeShader.ThreadGroupSizeX);
            Assert.Equal(8u, computeShader.ThreadGroupSizeY);

            using IGpuResourceLayout computeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Dst", GpuResourceKind.TextureReadWrite, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute)));
            using IGpuResourceSet computeSet = f.CreateResourceSet(new GpuResourceSetDescription(computeLayout, storage, sizeBuf));
            using IGpuComputePipeline computePipe = f.CreateComputePipeline(
                new GpuComputePipelineDescription(computeShader, computeLayout));

            using IGpuResourceLayout drawLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Src", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            using IGpuResourceSet drawSet = f.CreateResourceSet(new GpuResourceSetDescription(
                drawLayout, storage, dev.PointSampler, sizeBuf));
            using IGpuShaderSet drawShaders = f.CreateShadersFromSpirv(ComputeShaders.FullscreenVert, ComputeShaders.SampleFrag);
            using IGpuPipeline drawPipe = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { drawLayout },
                ShaderSet = drawShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = fb.Outputs,
            });

            uint groups = (Size + computeShader.ThreadGroupSizeX - 1) / computeShader.ThreadGroupSizeX;

            // ONE command list: dispatch, then the draw that samples what it wrote.
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetComputePipeline(computePipe);
                cl.SetComputeResourceSet(0, computeSet);
                cl.Dispatch(groups, groups, 1);

                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, Color.Black);
                cl.SetPipeline(drawPipe);
                cl.SetGraphicsResourceSet(0, drawSet);
                cl.Draw(3);
                cl.End();
                dev.Submit(cl);
                dev.WaitForIdle();
            }

            // The handoff: what the graphics pass sampled.
            AssertAddressPattern(GpuReadback.ToRgba(dev, colour, (int)Size, (int)Size), "sampled by the graphics pass");

            // And the compute write itself, read straight off the storage texture, so a failure above can be told
            // apart from a compute pass that never wrote anything.
            AssertAddressPattern(GpuReadback.ToRgba(dev, storage, (int)Size, (int)Size), "read back from the storage texture");
        }

        static void AssertAddressPattern(byte[] rgba, string what)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = (y * (int)Size + x) * 4;
                    Assert.True(rgba[i + 0] == x && rgba[i + 1] == y && rgba[i + 2] == 0 && rgba[i + 3] == 255,
                        $"{what}: texel ({x},{y}) is ({rgba[i]},{rgba[i + 1]},{rgba[i + 2]},{rgba[i + 3]}), " +
                        $"expected ({x},{y},0,255)");
                }
            }
        }
    }
}
