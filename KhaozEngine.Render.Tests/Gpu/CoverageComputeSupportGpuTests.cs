using System;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeSupportGpuTests(ITestOutputHelper output)
{
    // All backends run this fact. Direct3D11 and Vulkan native execution is hosted-only.
    [GpuFact]
    public void Packed_support_queries_preserve_grid_source_order_boundary_ties_and_overlay_words()
    {
        var data = CoverageGpuSupportProbe.Data();
        uint[] actual = CoverageGpuQueryProbe.RunOperations(output, CoverageGpuSupportProbe.Kernels(),
            data.Inputs, CoverageGpuSupportProbe.InputWords, CoverageGpuSupportProbe.OutputWords, data.Static);
        Assert.Equal(1u, actual[0]); Assert.Equal(0u, actual[1]); Assert.Equal(0u, actual[3]);
        Assert.Equal(1u, actual[4]); Assert.Equal(0u, actual[5]); Assert.Equal(0x3f800000u, actual[6]);
        for (int i = 0; i < actual.Length; i++)
        {
            if (i % 4 is 1 or 2 && float.IsNaN(BitConverter.UInt32BitsToSingle(data.Expected[i])))
                Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(actual[i])), $"row {i / 4}, field {i % 4}");
            else Assert.True(data.Expected[i] == actual[i], $"row {i / 4}, field {i % 4}: expected {data.Expected[i]:x8}, actual {actual[i]:x8}");
        }
        // Nonzero support base, 16-word header, full source-triangle records and reserved words.
        Assert.Equal(17u, data.Inputs[2]);
        Assert.Equal(new uint[] { 0x3f800000, 0xbf800000, 0xbf800000, 0x3f800000, 0x3f800000, 2, 4,
            16, 28, 30, 46, 50, 307, 0, 0, 0 }, data.Static[17..33]);
        output.WriteLine($"Matched all support-query words in {data.Inputs[0]} rows, including B16 neighbors and B21/B27 regressions");
    }
}
