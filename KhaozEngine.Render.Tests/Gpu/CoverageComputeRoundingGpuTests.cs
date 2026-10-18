using System.Collections.Generic;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeRoundingGpuTests(ITestOutputHelper output)
{
    // All backends run this fact. Direct3D11 and Vulkan native execution is hosted-only.
    [GpuFact]
    public void Limb_carries_midpoints_and_corrected_results_match_independent_integers()
    {
        var exact = CoverageGpuExactWordsProbe.Data();
        uint[] words = Run(CoverageGpuExactWordsProbe.Source, exact.Inputs, exact.Expected.Length);
        Assert.Equal(exact.Expected, words);
        uint limbOverflowCount = 0;
        for (int i = 19; i < words.Length; i += CoverageGpuExactWordsProbe.OutputWords) limbOverflowCount += words[i];
        Assert.Equal(0u, limbOverflowCount);
        var rounding = CoverageGpuRoundingProbe.Data();
        words = Run(CoverageGpuRoundingProbe.Source, rounding.Inputs, (int)rounding.Inputs[0] * CoverageGpuRoundingProbe.OutputWords);
        var actualDeviceWords = new List<uint>();
        uint maxBracketBisections = 0;
        for (int i = 0; i < words.Length; i += 4)
        {
            actualDeviceWords.Add(words[i]); actualDeviceWords.Add(words[i + 1]); actualDeviceWords.Add(words[i + 3]);
            Assert.InRange(words[i + 2], 0u, 31u);
            maxBracketBisections = System.Math.Max(maxBracketBisections, words[i + 2]);
        }
        Assert.Equal(rounding.Expected, actualDeviceWords.ToArray());
        Assert.InRange(maxBracketBisections, 1u, 31u);
        output.WriteLine($"Exact {exact.Inputs[0]} integer rows and {rounding.Inputs[0]} rounding rows, including injected seeds. Max bisections {maxBracketBisections}");
    }

    uint[] Run(string sourceText, uint[] inputs, int count)
    {
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        Assert.True(device.Capabilities.SupportsCompute);
        output.WriteLine($"Native backend {gpu.Backend}, adapter {device.Capabilities.DeviceName}");
        IGpuResourceFactory factory = device.Factory;
        using IGpuBuffer source = factory.CreateBuffer(new GpuBufferDescription(
            (uint)inputs.Length * sizeof(uint), GpuBufferUsage.StructuredBufferReadOnly, sizeof(uint)));
        using IGpuBuffer result = factory.CreateBuffer(new GpuBufferDescription(
            (uint)count * sizeof(uint), GpuBufferUsage.StructuredBufferReadWrite, sizeof(uint)));
        device.UpdateBuffer(source, 0, inputs);
        var poison = new uint[count];
        System.Array.Fill(poison, 0xdeadbeefu);
        device.UpdateBuffer(result, 0, poison);
        using IGpuResourceLayout layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("InputBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new GpuResourceLayoutElement("ResultBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
        using IGpuResourceSet set = factory.CreateResourceSet(new GpuResourceSetDescription(layout, source, result));
        using IGpuComputeShader shader = factory.CreateComputeShaderFromSpirv(sourceText, new GpuShaderCompileOptions(PreciseMath: true));
        using IGpuComputePipeline pipeline = factory.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));
        using IGpuCommandList commands = factory.CreateCommandList();
        commands.Begin(); commands.SetComputePipeline(pipeline); commands.SetComputeResourceSet(0, set);
        commands.Dispatch((inputs[0] + 63) / 64, 1, 1); commands.End();
        device.Submit(commands); device.WaitForIdle();
        return GpuReadback.ReadBuffer<uint>(device, result, count);
    }
}
