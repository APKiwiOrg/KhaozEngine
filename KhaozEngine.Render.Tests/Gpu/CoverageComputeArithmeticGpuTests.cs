using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeArithmeticGpuTests(ITestOutputHelper output)
{
    // A missing operand/result flush, changed zero tie or contracted scalar graph changes output words.
    [GpuFact]
    public void Inventory_words_match_on_device()
    {
        uint[] inputs = CoverageGpuArithmeticProbe.Inputs(), expected = CoverageGpuArithmeticProbe.Reference(inputs);
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        Assert.True(device.Capabilities.SupportsCompute);
        output.WriteLine($"Native backend {gpu.Backend}, adapter {device.Capabilities.DeviceName}");
        IGpuResourceFactory factory = device.Factory;
        using IGpuBuffer source = factory.CreateBuffer(new GpuBufferDescription(
            (uint)inputs.Length * sizeof(uint), GpuBufferUsage.StructuredBufferReadOnly, sizeof(uint)));
        using IGpuBuffer result = factory.CreateBuffer(new GpuBufferDescription(
            (uint)expected.Length * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
        device.UpdateBuffer(source, 0, inputs);
        var poison = new uint[expected.Length];
        System.Array.Fill(poison, 0xdeadbeefu);
        device.UpdateBuffer(result, 0, poison);
        using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("InputBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new GpuResourceLayoutElement("ResultBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
        using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(layout, source, result));
        using IGpuComputeShader shader = factory.CreateComputeShaderFromSpirv(
            CoverageGpuArithmeticProbe.Source, new GpuShaderCompileOptions(PreciseMath: true));
        using IGpuComputePipeline pipeline = factory.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));
        using IGpuCommandList commands = factory.CreateCommandList();
        commands.Begin();
        commands.SetComputePipeline(pipeline);
        commands.SetComputeResourceSet(0, set);
        commands.Dispatch((inputs[0] + 63) / 64, 1, 1);
        commands.End();
        device.Submit(commands);
        device.WaitForIdle();
        uint[] actual = GpuReadback.ReadBuffer<uint>(device, result, expected.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == actual[i],
                $"Row {i / CoverageGpuArithmeticProbe.OutputWords}, field {i % CoverageGpuArithmeticProbe.OutputWords}: expected 0x{expected[i]:x8}, actual 0x{actual[i]:x8}");
        CoverageGpuArithmeticTests.AssertPinnedWords(inputs, actual);
        output.WriteLine($"Exact inventory comparison: {actual.Length} words, {inputs[0]} dynamic rows");
    }
}
