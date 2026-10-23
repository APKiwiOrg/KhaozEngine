using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// VERTEX PULLING FROM A STORAGE BUFFER BY VERTEX INDEX: a draw with no vertex layout and no vertex buffer
    /// whose vertex stage fetches its own entry from a read-only structured buffer at
    /// <c>Base + gl_VertexIndex</c>, then the same draws after a dispatch in the same list rewrites the entries.
    /// <para>
    /// THE BASE IS A UNIFORM AND BOTH DRAWS START AT ZERO. A pulled draw is issued with first vertex 0 and base
    /// vertex 0, and any storage base is an explicit per-draw value the shader adds, because Direct3D 11's
    /// <c>SV_VertexID</c> leaves out the draw's bases under the pinned SPIRV-Cross options while Vulkan's
    /// <c>gl_VertexIndex</c> includes them. <see cref="VertexIndexOnDirect3D11LeavesOutTheDrawBases"/> pins that
    /// reason on every leg, the same way <c>VertexStageBufferSeamGpuTests</c> pins it for the instance index.
    /// </para>
    /// </summary>
    public sealed class ComputeVertexPullingGpuTests
    {
        const int Size = 16;
        const int Entries = 6;
        const uint StorageBase = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct Entry
        {
            public Vector4 Place;
            public Vector4 Colour;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PullParams
        {
            public uint Base;
            public uint Pad0, Pad1, Pad2;
        }

        const string PullVert = @"#version 450
struct Entry { vec4 Place; vec4 Colour; };
layout(set = 0, binding = 0) uniform Params { uvec4 Base; };
layout(std430, set = 0, binding = 1) readonly buffer EntryBuf { Entry E[]; };
layout(location = 0) out vec4 vColour;
void main() {
    Entry e = E[Base.x + uint(gl_VertexIndex)];
    gl_Position = vec4(e.Place.xy, 0.0, 1.0);
    vColour = e.Colour;
}";

        const string Frag = @"#version 450
layout(location = 0) in vec4 vColour;
layout(location = 0) out vec4 oColour;
void main() { oColour = vColour; }";

        // Rewrites the colour of entries 3 to 5 to blue, leaving the positions alone.
        const string Recolour = @"#version 450
layout(local_size_x = 4) in;
struct Entry { vec4 Place; vec4 Colour; };
layout(std430, set = 0, binding = 0) buffer EntryBuf { Entry E[]; };
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i < 3u) { E[3u + i].Colour = vec4(0.0, 0.0, 1.0, 1.0); }
}";

        static readonly Vector2[] Triangle = { new(-1f, -1f), new(3f, -1f), new(-1f, 3f) };

        [GpuFact]
        public void DrawAndDrawIndexedUseTheStorageVertexIndex()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            Assert.True(device.Capabilities.SupportsCompute, $"{device.Backend} reports no compute support");
            IGpuResourceFactory f = device.Factory;

            uint entryBytes = (uint)Marshal.SizeOf<Entry>();
            var entries = new Entry[Entries];
            for (int k = 0; k < Entries; k++)
            {
                Vector2 corner = Triangle[k % 3];
                entries[k] = new Entry
                {
                    Place = new Vector4(corner, 0f, 1f),
                    Colour = k < 3 ? new Vector4(1f, 0f, 0f, 1f) : new Vector4(0f, 1f, 0f, 1f),
                };
            }

            // READ-WRITE usage so the dispatch below can write it, bound READ-ONLY to the vertex stage.
            using IGpuBuffer entryBuffer = f.CreateBuffer(new GpuBufferDescription(
                Entries * entryBytes, GpuBufferUsage.StructuredBufferReadWrite, entryBytes));
            using IGpuBuffer parameters = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer indexBuffer = f.CreateBuffer(new GpuBufferDescription(8, GpuBufferUsage.IndexBuffer));

            using IGpuTexture drawTarget = Target(f);
            using IGpuTexture indexedTarget = Target(f);
            using IGpuFramebuffer drawFb = f.CreateFramebuffer(null, drawTarget);
            using IGpuFramebuffer indexedFb = f.CreateFramebuffer(null, indexedTarget);

            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
                new GpuResourceLayoutElement("EntryBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));
            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, parameters, entryBuffer));
            using IGpuShaderSet shaders = f.CreateShadersFromSpirv(PullVert, Frag);
            using IGpuPipeline pipeline = f.CreateGraphicsPipeline(Pipeline(shaders, drawFb, layout));

            using IGpuComputeShader recolourShader = f.CreateComputeShaderFromSpirv(Recolour);
            using IGpuResourceLayout recolourLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("EntryBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet recolourSet = f.CreateResourceSet(new GpuResourceSetDescription(recolourLayout, entryBuffer));
            using IGpuComputePipeline recolour = f.CreateComputePipeline(
                new GpuComputePipelineDescription(recolourShader, recolourLayout));

            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.UpdateBuffer<Entry>(entryBuffer, 0, entries);
                cl.UpdateBuffer(parameters, 0, new PullParams { Base = StorageBase });
                cl.UpdateBuffer<ushort>(indexBuffer, 0, new ushort[] { 0, 1, 2, 0 });
                DrawBoth(cl, pipeline, set, indexBuffer, drawFb, indexedFb);
                cl.End();
                device.Submit(cl);
                device.WaitForIdle();
            }

            Assert.Equal(new byte[] { 0, 255, 0, 255 }, Centre(device, drawTarget));
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, Centre(device, indexedTarget));

            // THE COMPUTE WRITER AND BOTH DRAWS IN ONE LIST, with no drain between the write and the reads.
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetComputePipeline(recolour);
                cl.SetComputeResourceSet(0, recolourSet);
                cl.Dispatch(1, 1, 1);
                DrawBoth(cl, pipeline, set, indexBuffer, drawFb, indexedFb);
                cl.End();
                device.Submit(cl);
                device.WaitForIdle();
            }

            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Centre(device, drawTarget));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Centre(device, indexedTarget));
        }

        [Fact]
        public void VertexIndexOnDirect3D11LeavesOutTheDrawBases()
        {
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(PullVert, Frag, "vertex pulling probe");

            // SV_VertexID with no base term: a draw at first vertex 3 or base vertex 3 would read entry 0 here and
            // entry 3 on Vulkan, which is why the storage base travels in the uniform.
            Assert.Contains("SV_VertexID", pair.VertexSource);
            Assert.DoesNotContain("BaseVertex", pair.VertexSource);
        }

        // Both draws start at vertex 0 and base vertex 0, and read entries 3 to 5 through the uniform base.
        static void DrawBoth(IGpuCommandList cl, IGpuPipeline pipeline, IGpuResourceSet set, IGpuBuffer indexBuffer,
            IGpuFramebuffer drawFb, IGpuFramebuffer indexedFb)
        {
            cl.SetFramebuffer(drawFb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, set);
            cl.Draw(3, 1, 0, 0);

            cl.SetFramebuffer(indexedFb);
            cl.ClearColorTarget(0, Color.Black);
            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, set);
            cl.SetIndexBuffer(indexBuffer, GpuIndexFormat.UInt16);
            cl.DrawIndexed(3, 1, 0, 0, 0);
        }

        static IGpuTexture Target(IGpuResourceFactory f) => f.CreateTexture(GpuTextureDescription.Texture2D(
            Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

        static byte[] Centre(IGpuDevice device, IGpuTexture target)
        {
            byte[] rgba = GpuReadback.ToRgba(device, target, Size, Size);
            int i = ((Size / 2) * Size + Size / 2) * 4;
            return new[] { rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3] };
        }

        static GpuPipelineDescription Pipeline(IGpuShaderSet shaders, IGpuFramebuffer fb, IGpuResourceLayout layout)
            => new()
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
            };
    }
}
