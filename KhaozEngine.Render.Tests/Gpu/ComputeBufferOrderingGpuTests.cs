using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SEAM'S IN-LIST STORAGE BUFFER ORDERING, on a real device: compute rule 2 on
    /// <see cref="IGpuCommandList"/>. A dispatch or a draw that reads a storage buffer an earlier dispatch in the
    /// SAME list wrote sees the write, with no barrier call and no drain between them.
    /// <para>
    /// <see cref="ReductionAndDrawShareOneSubmission"/> is <see cref="ComputeBufferGpuTests"/>' 4096-value
    /// reduction with both passes and a vertex-stage reader of the total in one list and one submission.
    /// <see cref="ARestoredPipelinesReaderObservesTheDispatchWrite"/> puts a pipeline that cannot reach the
    /// written buffer between the writer and the reader, so the reader's draw is the first one that binds it.
    /// </para>
    /// </summary>
    public sealed class ComputeBufferOrderingGpuTests
    {
        const uint GroupSize = 256;
        const uint Count = 4096;
        const uint Groups = Count / GroupSize;
        const int Size = 16;

        [StructLayout(LayoutKind.Sequential)]
        struct ReduceParams
        {
            public uint Count;
            public uint Pad0, Pad1, Pad2;
        }

        // The total read in the VERTEX stage, so the draw is a storage reader of what the second pass wrote.
        const string TotalVert = @"#version 450
layout(std430, set = 0, binding = 0) readonly buffer TotalBuf { uint Total[]; };
layout(location = 0) out vec4 vColour;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
    vColour = Total[0] == 8390656u ? vec4(0.0, 1.0, 0.0, 1.0) : vec4(1.0, 0.0, 0.0, 1.0);
}";

        const string Frag = @"#version 450
layout(location = 0) in vec4 vColour;
layout(location = 0) out vec4 oColour;
void main() { oColour = vColour; }";

        // Set 0 alone: the tint, so this pipeline cannot reach the buffer at set 1.
        const string TintVert = @"#version 450
layout(set = 0, binding = 0) uniform Tint { vec4 TintColour; };
layout(location = 0) out vec4 vColour;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
    vColour = TintColour;
}";

        // Set 0 and set 1: the colour comes from the buffer the dispatch writes, the alpha from the tint.
        const string ValueVert = @"#version 450
layout(set = 0, binding = 0) uniform Tint { vec4 TintColour; };
layout(std430, set = 1, binding = 0) readonly buffer ValueBuf { vec4 Values[]; };
layout(location = 0) out vec4 vColour;
void main() {
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
    vColour = vec4(Values[0].rgb, TintColour.a);
}";

        const string WriteValue = @"#version 450
layout(local_size_x = 1) in;
layout(std430, set = 0, binding = 0) buffer ValueBuf { vec4 Values[]; };
void main() { Values[0] = vec4(0.0, 0.0, 1.0, 1.0); }";

        [GpuFact]
        public void ReductionAndDrawShareOneSubmission()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            Assert.True(device.Capabilities.SupportsCompute, $"{device.Backend} reports no compute support");
            IGpuResourceFactory f = device.Factory;

            using IGpuComputeShader shader = f.CreateComputeShaderFromSpirv(ComputeShaders.Reduce);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("SrcBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("DstBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuComputePipeline reduce = f.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));

            using IGpuBuffer src = f.CreateBuffer(new GpuBufferDescription(
                Count * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer partials = f.CreateBuffer(new GpuBufferDescription(
                Groups * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer total = f.CreateBuffer(new GpuBufferDescription(
                4 * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));

            var values = new uint[Count];
            for (uint i = 0; i < Count; i++) values[i] = i + 1;
            device.UpdateBuffer(src, 0, values);

            using IGpuBuffer paramsPass1 = Uniform(device, new ReduceParams { Count = Count });
            using IGpuBuffer paramsPass2 = Uniform(device, new ReduceParams { Count = Groups });
            using IGpuResourceSet setPass1 = f.CreateResourceSet(new GpuResourceSetDescription(layout, paramsPass1, src, partials));
            using IGpuResourceSet setPass2 = f.CreateResourceSet(new GpuResourceSetDescription(layout, paramsPass2, partials, total));

            using IGpuTexture target = Target(f);
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            using IGpuResourceLayout readerLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("TotalBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));
            using IGpuResourceSet readerSet = f.CreateResourceSet(new GpuResourceSetDescription(readerLayout, total));
            using IGpuShaderSet readerShaders = f.CreateShadersFromSpirv(TotalVert, Frag);
            using IGpuPipeline reader = f.CreateGraphicsPipeline(Pipeline(readerShaders, fb, readerLayout));

            // ONE list, ONE submission: pass 1, pass 2 reading pass 1's partials, and a draw reading pass 2's total.
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetComputePipeline(reduce);
                cl.SetComputeResourceSet(0, setPass1);
                cl.Dispatch(Groups, 1, 1);
                cl.SetComputeResourceSet(0, setPass2);
                cl.Dispatch(1, 1, 1);

                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, Color.Black);
                cl.SetPipeline(reader);
                cl.SetGraphicsResourceSet(0, readerSet);
                cl.Draw(3);
                cl.End();
                device.Submit(cl);
                device.WaitForIdle();
            }

            Assert.Equal(8390656u, GpuReadback.ReadBuffer<uint>(device, total, 1)[0]);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, Centre(device, target));
        }

        /// <summary>
        /// A pipeline that declares only set 0 draws between the writer and the reader, then the two-set pipeline
        /// is restored and its draw reads the written value. The sets are rebound after each switch because that
        /// is the portable contract (Direct3D 11 forgets its records on a pipeline switch). The native Vulkan
        /// record keeps set 1 across the switch and only asks about it again once the restored layout declares
        /// it, which <c>VulkanComputeHazardWalkTests</c> pins device-free without the rebind.
        /// </summary>
        [GpuFact]
        public void ARestoredPipelinesReaderObservesTheDispatchWrite()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            Assert.True(device.Capabilities.SupportsCompute, $"{device.Backend} reports no compute support");
            IGpuResourceFactory f = device.Factory;

            using IGpuBuffer valueBuffer = f.CreateBuffer(new GpuBufferDescription(
                16, GpuBufferUsage.StructuredBufferReadWrite, 16));
            device.UpdateBuffer(valueBuffer, 0, new Vector4(0f, 1f, 0f, 1f));
            using IGpuBuffer tint = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            device.UpdateBuffer(tint, 0, new Vector4(1f, 0f, 0f, 1f));

            using IGpuComputeShader writerShader = f.CreateComputeShaderFromSpirv(WriteValue);
            using IGpuResourceLayout writerLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("ValueBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet writerSet = f.CreateResourceSet(new GpuResourceSetDescription(writerLayout, valueBuffer));
            using IGpuComputePipeline writer = f.CreateComputePipeline(new GpuComputePipelineDescription(writerShader, writerLayout));

            using IGpuTexture target = Target(f);
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            using IGpuResourceLayout tintLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tint", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            using IGpuResourceLayout valueLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("ValueBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));
            using IGpuResourceSet tintSet = f.CreateResourceSet(new GpuResourceSetDescription(tintLayout, tint));
            using IGpuResourceSet valueSet = f.CreateResourceSet(new GpuResourceSetDescription(valueLayout, valueBuffer));
            using IGpuShaderSet tintShaders = f.CreateShadersFromSpirv(TintVert, Frag);
            using IGpuShaderSet valueShaders = f.CreateShadersFromSpirv(ValueVert, Frag);
            using IGpuPipeline dropping = f.CreateGraphicsPipeline(Pipeline(tintShaders, fb, tintLayout));
            using IGpuPipeline restoring = f.CreateGraphicsPipeline(Pipeline(valueShaders, fb, tintLayout, valueLayout));

            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, Color.Black);
                cl.SetPipeline(restoring);
                cl.SetGraphicsResourceSet(0, tintSet);
                cl.SetGraphicsResourceSet(1, valueSet);

                cl.SetComputePipeline(writer);
                cl.SetComputeResourceSet(0, writerSet);
                cl.Dispatch(1, 1, 1);

                cl.SetPipeline(dropping);
                cl.SetGraphicsResourceSet(0, tintSet);
                cl.Draw(3);

                cl.SetPipeline(restoring);
                cl.SetGraphicsResourceSet(0, tintSet);
                cl.SetGraphicsResourceSet(1, valueSet);
                cl.Draw(3);
                cl.End();
                device.Submit(cl);
                device.WaitForIdle();
            }

            Assert.Equal(new byte[] { 0, 0, 255, 255 }, Centre(device, target));
        }

        static IGpuBuffer Uniform(IGpuDevice device, ReduceParams value)
        {
            IGpuBuffer b = device.Factory.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            device.UpdateBuffer(b, 0, value);
            return b;
        }

        static IGpuTexture Target(IGpuResourceFactory f) => f.CreateTexture(GpuTextureDescription.Texture2D(
            Size, Size, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

        static byte[] Centre(IGpuDevice device, IGpuTexture target)
        {
            byte[] rgba = GpuReadback.ToRgba(device, target, Size, Size);
            int i = ((Size / 2) * Size + Size / 2) * 4;
            return new[] { rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3] };
        }

        static GpuPipelineDescription Pipeline(IGpuShaderSet shaders, IGpuFramebuffer fb,
            params IGpuResourceLayout[] layouts) => new()
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = layouts,
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = fb.Outputs,
            };
    }
}
