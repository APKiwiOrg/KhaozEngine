using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Veldrid;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>End-to-end smoke of the engine GPU abstraction on the real device (Metal on the dev box): create
    /// a device, a buffer, a render-target texture + framebuffer, a passthrough graphics pipeline (built via
    /// <see cref="IGpuResourceFactory.CreateShadersFromSpirv"/>), and a command list; record + submit an empty
    /// clear pass; read back one pixel; dispose. Proves the Veldrid impl actually works through the seam. Runs
    /// only with <c>KE_GPU_TESTS=1</c>.</summary>
    public class GpuDeviceSmokeTests
    {
        const string Vert = @"#version 450
layout(location=0) in vec2 Pos;
void main() { gl_Position = vec4(Pos, 0.0, 1.0); }";

        const string Frag = @"#version 450
layout(location=0) out vec4 o;
void main() { o = vec4(0.0, 1.0, 0.0, 1.0); }";

        [GpuFact]
        public void Device_CreatesResources_BuildsPipeline_And_SubmitsClearPass()
        {
            var opts = new GraphicsDeviceOptions(false, null, false, ResourceBindingModel.Improved, true, true);
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless(opts);
            IGpuDevice device = ctx.GpuDevice;
            IGpuResourceFactory f = device.Factory;

            const uint w = 16, h = 16;

            // Buffer.
            using IGpuBuffer vb = f.CreateBuffer(new GpuBufferDescription(6 * 8, GpuBufferUsage.VertexBuffer));
            Assert.Equal(48u, vb.SizeInBytes);
            var tri = new[]
            {
                new Vector2(-1, -1), new Vector2(3, -1), new Vector2(-1, 3),
                Vector2.Zero, Vector2.Zero, Vector2.Zero,
            };
            device.UpdateBuffer(vb, 0, tri);

            // Render-target texture + framebuffer + staging readback target.
            using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                w, h, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            Assert.Equal(w, target.Width);
            Assert.Equal(GpuPixelFormat.R8G8B8A8UNorm, target.Format);
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            Assert.Single(fb.Outputs.Colour);
            Assert.Null(fb.Outputs.Depth);

            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                w, h, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));

            // A trivial passthrough pipeline using CreateShadersFromSpirv.
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(Vert, Frag);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription());
            var pd = new GpuPipelineDescription
            {
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    new GpuVertexLayoutDescription(new GpuVertexElement("Pos", GpuVertexElementFormat.Float2)),
                },
                ShaderSet = shaders,
                // THE EMPTY LAYOUT IS DECLARED RATHER THAN DROPPED, and the local above was always built for
                // this. These shaders bind no resources, and the SPIR-V reflection still reports ONE resource
                // layout for them, an empty one. Veldrid tolerates the disagreement, so this read
                // Array.Empty<IGpuResourceLayout>() for years and passed on every backend. The native Metal
                // backend does not: its binding table is keyed on (set, binding, stage) read out of the shader's
                // own decorations, so it requires the declared array to be the same SHAPE as the reflection it
                // built the table from, and a pipeline declaring zero layouts against a reflection reporting one
                // is refused at creation. Declaring the empty layout is what this pipeline always meant.
                //
                // AND DECLARING IT OBLIGES THE DRAW TO BIND ONE, which is the half that has to be got right on
                // BOTH backends at once and was measured rather than reasoned: with the layout declared and no
                // set bound, Veldrid's Metal backend dereferences a null inside ActivateGraphicsResourceSet at
                // the draw. So the empty set below is not decoration. One declared layout, one bound set, on
                // every backend.
                ResourceLayouts = new[] { layout },
                Outputs = fb.Outputs,
            };
            using IGpuPipeline pipeline = f.CreateGraphicsPipeline(pd);
            using IGpuResourceSet emptySet = f.CreateResourceSet(new GpuResourceSetDescription(layout));

            // Record: clear green, draw the fullscreen triangle, copy to staging.
            using IGpuCommandList cl = f.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetFullScissorRects();
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, emptySet);
            cl.SetVertexBuffer(0, vb);
            cl.Draw(3, 1, 0, 0);
            cl.CopyTexture(target, staging);
            cl.End();
            device.Submit(cl);
            device.WaitForIdle();

            // Read back the centre pixel: the passthrough frag writes green.
            MappedData map = device.Map(staging, GpuMapMode.Read);
            int idx = 8 * (int)map.RowPitch + 8 * 4;
            byte g = System.Runtime.InteropServices.Marshal.ReadByte(map.Data, idx + 1);
            device.Unmap(staging);
            Assert.True(g > 200, $"expected green centre pixel, got G={g}");
        }
    }
}
