using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryLayoutTests
{
    [Fact]
    public void Every_pipeline_and_recorded_set_matches_its_reflected_bindings_element_by_element()
    {
        using var device = new CoverageCarryLayoutDevice();
        using var carry = new CoverageCarryDevice(device, new CoverageCarryFixture(), 1);
        string[] names = ["CoverageCarrySkin", "CoverageCarryTransport", "CoverageCarryBasis",
            "CoverageCarryPublish", "CoverageCarryOrdinarySkin"];
        Assert.Equal(names.Length, device.Pipelines.Count);
        var kernels = CoverageCarryShaders.Kernels().ToArray();
        Action[] checks = device.Pipelines.Select((pipeline, index) => (Action)(() =>
        {
            Assert.Equal(names[index], kernels[index].Name);
            var options = new GpuShaderCompileOptions(PreciseMath: index != 4);
            byte[] module = SpirvFrontEnd.ToSpirv(kernels[index].ComputeGlsl, GpuShaderStages.Compute, names[index], options);
            var hlsl = SpirvCrossCompile.ComputeToHlsl(module, names[index], options).Reflection;
            var msl = SpirvCrossCompile.ComputeToMsl(module, names[index], options).Reflection;
            var expected = Assert.Single(hlsl.ResourceLayouts).Elements;
            Assert.Equal(index == 1 ? 7 : 6, expected.Length);
            Assert.Equal(expected, Assert.Single(msl.ResourceLayouts).Elements);
            var declared = Assert.Single(pipeline.Description.ResourceLayouts);
            var actual = Assert.IsType<CoverageCarryLayoutDevice.Layout>(declared).Description.Elements;
            Assert.True(expected.Length == actual.Length,
                $"{names[index]} declares {actual.Length} elements but reflects {expected.Length}.");
            for (int binding = 0; binding < expected.Length; binding++)
            {
                Assert.Equal(binding < 2 || binding == 6 ? GpuResourceKind.StructuredBufferReadOnly
                    : GpuResourceKind.StructuredBufferReadWrite, expected[binding].Kind);
                Assert.Equal(expected[binding].Name, actual[binding].Name);
                Assert.Equal(expected[binding].Kind, actual[binding].Kind);
                Assert.Equal(expected[binding].Stages, actual[binding].Stages);
                Assert.Equal(expected[binding].Dynamic, actual[binding].Dynamic);
            }
        })).ToArray();
        Assert.Multiple(checks);

        using var commands = new CoverageCarryLayoutDevice.Commands();
        commands.Begin();
        carry.Record(commands);
        carry.Record(commands, ordinary: true);
        commands.End();
        Assert.Equal(device.Pipelines, commands.Dispatches.Select(d => d.Pipeline));
        foreach (var dispatch in commands.Dispatches)
        {
            var declared = Assert.Single(dispatch.Pipeline.Description.ResourceLayouts);
            var layout = Assert.IsType<CoverageCarryLayoutDevice.Layout>(declared);
            Assert.Same(declared, dispatch.Set.Description.Layout);
            Assert.Equal(layout.Description.Elements.Length, dispatch.Set.Description.Resources.Length);
            for (int binding = 0; binding < layout.Description.Elements.Length; binding++)
                Assert.Same(device.Buffers[binding], dispatch.Set.Description.Resources[binding]);
        }
    }
}
