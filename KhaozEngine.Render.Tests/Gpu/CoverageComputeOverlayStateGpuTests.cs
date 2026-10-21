using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeOverlayStateGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Divergent_overlay_crossings_keep_lane_owned_state_across_multiple_workgroups()
    {
        var data = CoverageGpuSupportProbe.Data();
        int[] overlayRows = Enumerable.Range(0, (int)data.Inputs[0])
            .Where(row => data.Inputs[1 + row * CoverageGpuSupportProbe.InputWords] == 2u).ToArray();
        // Rotate the reference cases through every lane, including an incomplete third workgroup.
        // Invalid hosts exit beside valid crossings and canonical endpoint heights.
        const int rows = 137;
        var inputs = new uint[1 + rows * CoverageGpuSupportProbe.InputWords];
        var expected = new uint[rows * CoverageGpuSupportProbe.OutputWords];
        inputs[0] = rows;
        for (int row = 0; row < rows; row++)
        {
            int source = overlayRows[(row * 7 + row / 64) % overlayRows.Length];
            Array.Copy(data.Inputs, 1 + source * CoverageGpuSupportProbe.InputWords,
                inputs, 1 + row * CoverageGpuSupportProbe.InputWords, CoverageGpuSupportProbe.InputWords);
            Array.Copy(data.Expected, source * CoverageGpuSupportProbe.OutputWords,
                expected, row * CoverageGpuSupportProbe.OutputWords, CoverageGpuSupportProbe.OutputWords);
        }
        uint[] actual = CoverageGpuQueryProbe.Run(output, "CoverageOverlayLaneState", CoverageGpuSupportProbe.Source(2),
            inputs, expected.Length, data.Static);
        for (int i = 0; i < expected.Length; i++)
        {
            if (i % 4 is 1 or 2 && float.IsNaN(BitConverter.UInt32BitsToSingle(expected[i])))
                Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(actual[i])), $"row {i / 4}, field {i % 4}");
            else Assert.True(expected[i] == actual[i], $"row {i / 4}, field {i % 4}: expected {expected[i]:x8}, actual {actual[i]:x8}");
        }
    }
}
