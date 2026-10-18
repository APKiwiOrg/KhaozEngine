using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class ComputeIntegerAtomicGpuTests
    {
        const string ReadResults = @"#version 450
layout(local_size_x = 1) in;
layout(std430, set = 0, binding = 0) readonly buffer WordBuf { uint Words[]; };
layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
void main() {
    Results[0] = Words[0];
    Results[1] = Words[1];
    Results[2] = Words[2];
}";

        [GpuFact]
        public void UnsignedOrAndMaxContendAcrossFourWorkgroups()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            Assert.True(device.Capabilities.SupportsCompute, $"{device.Backend} reports no compute support");
            IGpuResourceFactory f = device.Factory;

            using IGpuBuffer words = f.CreateBuffer(new GpuBufferDescription(
                3 * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            using IGpuBuffer results = f.CreateBuffer(new GpuBufferDescription(
                3 * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            device.UpdateBuffer(words, 0, new uint[] { 0u, 0u, 0x12345678u });
            device.UpdateBuffer(results, 0, new uint[] { 0u, 0u, 0u });

            using IGpuComputeShader atomicShader = f.CreateComputeShaderFromSpirv(ComputeAtomicShaders.UnsignedOrMax);
            Assert.Equal(64u, atomicShader.ThreadGroupSizeX);
            using IGpuResourceLayout atomicLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("WordBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet atomicSet = f.CreateResourceSet(new GpuResourceSetDescription(atomicLayout, words));
            using IGpuComputePipeline atomicPipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(atomicShader, atomicLayout));

            using IGpuComputeShader readerShader = f.CreateComputeShaderFromSpirv(ReadResults);
            using IGpuResourceLayout readerLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("WordBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
                new GpuResourceLayoutElement("ResultBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet readerSet = f.CreateResourceSet(new GpuResourceSetDescription(readerLayout, words, results));
            using IGpuComputePipeline readerPipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(readerShader, readerLayout));

            // All four groups contend on two words. Only a later dispatch observes the completed result,
            // so no invocation depends on workgroup order or tries to synchronize workgroups in the shader.
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetComputePipeline(atomicPipeline);
                cl.SetComputeResourceSet(0, atomicSet);
                cl.Dispatch(4, 1, 1);
                cl.SetComputePipeline(readerPipeline);
                cl.SetComputeResourceSet(0, readerSet);
                cl.Dispatch(1, 1, 1);
                cl.End();
                device.Submit(cl);
                device.WaitForIdle();
            }

            uint[] result = GpuReadback.ReadBuffer<uint>(device, results, 3);
            Assert.Equal(new uint[] { 0xffffffffu, 0x800000ffu, 0x12345678u }, result);
        }

        [GpuFact]
        public void AtomicsReturnThePreviousUnsignedValue()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = gpu.GpuDevice;
            Assert.True(device.Capabilities.SupportsCompute, $"{device.Backend} reports no compute support");
            IGpuResourceFactory f = device.Factory;

            using IGpuBuffer words = f.CreateBuffer(new GpuBufferDescription(
                3 * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
            device.UpdateBuffer(words, 0, new uint[] { 0u, 0u, 0x20u });
            using IGpuComputeShader shader = f.CreateComputeShaderFromSpirv(ComputeAtomicShaders.ReturnOldValue);
            Assert.Equal(1u, shader.ThreadGroupSizeX);
            using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("WordBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, words));
            using IGpuComputePipeline pipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));

            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetComputePipeline(pipeline);
                cl.SetComputeResourceSet(0, set);
                cl.Dispatch(1, 1, 1);
                cl.End();
                device.Submit(cl);
                device.WaitForIdle();
            }

            uint[] returnedAndFinal = GpuReadback.ReadBuffer<uint>(device, words, 3);
            Assert.Equal(new uint[] { 0x20u, 0x24u, 0x80000000u }, returnedAndFinal);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void UnsignedAtomicsCrossCompileToNativeIntegerOperations(bool returnOldValue)
        {
            string source = returnOldValue ? ComputeAtomicShaders.ReturnOldValue : ComputeAtomicShaders.UnsignedOrMax;
            string label = returnOldValue ? nameof(ComputeAtomicShaders.ReturnOldValue) : nameof(ComputeAtomicShaders.UnsignedOrMax);
            ShaderValidation.ValidateCompute(source, label);

            byte[] spirv = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, label);
            string hlsl = SpirvCrossCompile.ComputeToHlsl(spirv, label).ComputeSource;
            Assert.Contains("InterlockedOr", hlsl);
            Assert.Contains("InterlockedMax", hlsl);

            string msl = SpirvCrossCompile.ComputeToMsl(spirv, label).ComputeSource;
            Assert.Contains("atomic_fetch_or_explicit", msl);
            Assert.Contains("atomic_fetch_max_explicit", msl);
        }
    }
}
