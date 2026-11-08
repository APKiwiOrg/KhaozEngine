using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryLayoutTests
{
    public static IEnumerable<object[]> Stages() => Enumerable.Range(0, 4).Select(i => new object[] { i });
    [Theory]
    [MemberData(nameof(Stages))]
    public void Packed_bindings_include_the_immutable_table(int stage)
    {
        using var f = new CoverageCarryGpuFixture();
        f.Stage();
        var kernel = CoverageCarryShaders.Kernels[stage];
        byte[] module = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options);
        var words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        var bindings = new List<uint>();
        for (int at = 5; at < words.Length; at += (int)(words[at] >> 16))
        {
            if ((words[at] & 65535) != 71) continue;
            if (words[at + 2] == 33) bindings.Add(words[at + 3]);
            if (words[at + 2] == 34) Assert.Equal(0u, words[at + 3]);
        }
        var hlsl = SpirvCrossCompile.ComputeToHlsl(module, kernel.Name, CoverageCarryShaders.Options);
        var msl = SpirvCrossCompile.ComputeToMsl(module, kernel.Name, CoverageCarryShaders.Options);
        var expected = Assert.Single(hlsl.Reflection.ResourceLayouts).Elements;
        Assert.Equal(stage is 1 or 3 ? 5 : 4, expected.Length);
        Assert.Equal(Enumerable.Range(0, expected.Length).Select(i => (uint)i), bindings.Order());
        Assert.Equal(expected, Assert.Single(msl.Reflection.ResourceLayouts).Elements);
        var pipeline = f.Device.Pipelines[stage];
        var declared = Assert.IsType<CoverageCarryTestDevice.Layout>(Assert.Single(pipeline.Description.ResourceLayouts));
        Assert.Equal(expected, declared.Description.Elements);
        Assert.Equal(stage == 1 ? "CarryTableBuf" : stage == 3 ? "CovCurrentBuf" : "CovResultsBuf", expected[^1].Name);
        Assert.Equal(stage == 1 ? GpuResourceKind.StructuredBufferReadOnly : GpuResourceKind.StructuredBufferReadWrite, expected[^1].Kind);
    }
}
