using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Proof test (a) for the compute seam: a real storage-buffer job on a real device, readback-verified against an
    /// exact expected value. A two-pass parallel reduction of 4096 unsigned integers, which exercises the whole
    /// compute surface at once: a compute shader from GLSL, a compute pipeline, read-write storage buffers, workgroup
    /// shared memory plus <c>barrier()</c>, two dependent dispatches, and <see cref="GpuReadback.ReadBuffer{T}"/>.
    ///
    /// Unsigned integers, not floats, so the assertion is exact: 1 + 2 + ... + 4096 == 8390656 regardless of the
    /// order the GPU happened to sum in.
    ///
    /// The two passes are separated by End + Submit + WaitForIdle, per the ordering contract on
    /// <see cref="IGpuCommandList"/>: pass 2 reads what pass 1 wrote, and chaining dependent dispatches inside one
    /// command list is not safe on every backend.
    /// </summary>
    public sealed class ComputeBufferGpuTests
    {
        const uint GroupSize = 256;
        const uint Count = 4096;
        const uint Groups = Count / GroupSize;                 // 16 per-group partials
        const uint ExpectedSum = Count * (Count + 1) / 2;      // 8390656

        [StructLayout(LayoutKind.Sequential)]
        struct ReduceParams
        {
            public uint Count;
            public uint Pad0, Pad1, Pad2;   // std140 pads the block to 16 bytes; Veldrid needs the uniform buffer to match
        }

        [GpuFact]
        public void ParallelReductionOverAStorageBufferSumsExactly()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");

            IGpuResourceFactory f = dev.Factory;
            using IGpuComputeShader shader = f.CreateComputeShaderFromSpirv(ComputeShaders.Reduce);
            Assert.Equal(GroupSize, shader.ThreadGroupSizeX);   // read off the module, not restated by the caller

            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("SrcBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("DstBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuComputePipeline pipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));

            // src -> partials -> total. Every storage buffer is StructuredBufferReadWrite because the shader declares
            // both bindings as a writable storage block.
            using IGpuBuffer src = f.CreateBuffer(new GpuBufferDescription(
                Count * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer partials = f.CreateBuffer(new GpuBufferDescription(
                Groups * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer total = f.CreateBuffer(new GpuBufferDescription(
                4 * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));

            var values = new uint[Count];
            for (uint i = 0; i < Count; i++) values[i] = i + 1;
            dev.UpdateBuffer(src, 0, values);

            using IGpuBuffer paramsPass1 = UniformBuffer(dev, Count);
            using IGpuBuffer paramsPass2 = UniformBuffer(dev, Groups);
            using IGpuResourceSet setPass1 = f.CreateResourceSet(new GpuResourceSetDescription(layout, paramsPass1, src, partials));
            using IGpuResourceSet setPass2 = f.CreateResourceSet(new GpuResourceSetDescription(layout, paramsPass2, partials, total));

            Dispatch(dev, pipeline, setPass1, Groups);
            Dispatch(dev, pipeline, setPass2, 1);

            uint[] partialValues = GpuReadback.ReadBuffer<uint>(dev, partials, (int)Groups);
            uint[] totalValue = GpuReadback.ReadBuffer<uint>(dev, total, 1);

            // Every group summed its own contiguous 256-element block, so each partial is exactly computable.
            for (uint g = 0; g < Groups; g++)
            {
                uint first = g * GroupSize + 1;
                uint last = (g + 1) * GroupSize;
                uint expected = (first + last) * GroupSize / 2;
                Assert.Equal(expected, partialValues[g]);
            }
            Assert.Equal(ExpectedSum, totalValue[0]);
        }

        /// <summary>The dynamic-offset compute binding: one uniform buffer holding several per-dispatch parameter
        /// blocks at the 256-byte alignment every backend accepts, one resource set, and a byte offset chosen per
        /// dispatch. This is how a run of stages reads its own parameters without a set (and a buffer) each, which
        /// is what a multi-stage compute chain wants. Same reduction shader: two dispatches over the same 16
        /// partials with different element counts must give two different, exactly known sums.</summary>
        [GpuFact]
        public void ADynamicOffsetRebasesTheParameterBlockPerDispatch()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            Assert.True(dev.Capabilities.SupportsCompute, $"{dev.Backend} reports no compute support");
            IGpuResourceFactory f = dev.Factory;

            using IGpuComputeShader shader = f.CreateComputeShaderFromSpirv(ComputeShaders.Reduce);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute, dynamic: true),
                new GpuResourceLayoutElement("SrcBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("DstBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuComputePipeline pipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));

            // 16 values: 1, 2, ... 16. Summing the first 4 and all 16 are two distinct exact answers.
            const uint values = 16;
            using IGpuBuffer src = f.CreateBuffer(new GpuBufferDescription(
                values * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer dst = f.CreateBuffer(new GpuBufferDescription(
                4 * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            var data = new uint[values];
            for (uint i = 0; i < values; i++) data[i] = i + 1;
            dev.UpdateBuffer(src, 0, data);

            const uint alignment = 256;   // safe uniform-buffer offset alignment across Metal / Direct3D11 / Vulkan
            using IGpuBuffer paramBlocks = f.CreateBuffer(new GpuBufferDescription(alignment * 2, GpuBufferUsage.UniformBuffer));
            dev.UpdateBuffer(paramBlocks, 0, new ReduceParams { Count = values });
            dev.UpdateBuffer(paramBlocks, alignment, new ReduceParams { Count = 4 });

            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(
                layout, new GpuBufferRange(paramBlocks, 0, 16), src, dst));

            DispatchWithOffset(dev, pipeline, set, 0);
            Assert.Equal(values * (values + 1) / 2, GpuReadback.ReadBuffer<uint>(dev, dst, 1)[0]);   // 136

            DispatchWithOffset(dev, pipeline, set, alignment);
            Assert.Equal(4u * 5u / 2u, GpuReadback.ReadBuffer<uint>(dev, dst, 1)[0]);                // 10
        }

        static void DispatchWithOffset(IGpuDevice dev, IGpuComputePipeline pipeline, IGpuResourceSet set, uint offset)
        {
            using IGpuCommandList cl = dev.Factory.CreateCommandList();
            cl.Begin();
            cl.SetComputePipeline(pipeline);
            cl.SetComputeResourceSet(0, set, offset);
            cl.Dispatch(1, 1, 1);
            cl.End();
            dev.Submit(cl);
            dev.WaitForIdle();
        }

        static IGpuBuffer UniformBuffer(IGpuDevice dev, uint count)
        {
            IGpuBuffer b = dev.Factory.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
            dev.UpdateBuffer(b, 0, new ReduceParams { Count = count });
            return b;
        }

        // One dispatch per submission. Pass 2 reads pass 1's output, and a submit boundary plus a device drain is
        // the only cross-dispatch ordering the seam guarantees (see the IGpuCommandList ordering contract).
        static void Dispatch(IGpuDevice dev, IGpuComputePipeline pipeline, IGpuResourceSet set, uint groups)
        {
            using IGpuCommandList cl = dev.Factory.CreateCommandList();
            cl.Begin();
            cl.SetComputePipeline(pipeline);
            cl.SetComputeResourceSet(0, set);
            cl.Dispatch(groups, 1, 1);
            cl.End();
            dev.Submit(cl);
            dev.WaitForIdle();
        }
    }
}
