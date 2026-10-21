using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_pipeline_and_recorded_set_matches_its_reflected_bindings_element_by_element(bool nativeFloat)
    {
        using var device = new CoverageCarryLayoutDevice();
        using var carry = new CoverageCarryDevice(device, new CoverageCarryFixture(), 1, nativeFloat);
        string prefix = nativeFloat ? "CoverageCarryNative" : "CoverageCarry";
        string[] names = [prefix + "Skin", prefix + "Transport", prefix + "Basis", prefix + "Publish", "CoverageCarryOrdinarySkin"];
        Assert.Equal(names.Length, device.Pipelines.Count);
        var kernels = CoverageCarryShaders.Kernels().OrderBy(k => Array.IndexOf(names, k.Name)).Where(k => names.Contains(k.Name)).ToArray();
        Action[] checks = device.Pipelines.Select((pipeline, index) => (Action)(() =>
        {
            Assert.Equal(names[index], kernels[index].Name);
            var options = CoverageCarryShaders.Options(kernels[index].Name);
            int[] buffers = CoverageCarryShaders.BufferIndices(kernels[index].Name);
            byte[] module = SpirvFrontEnd.ToSpirv(kernels[index].ComputeGlsl, GpuShaderStages.Compute, names[index], options);
            var hlsl = SpirvCrossCompile.ComputeToHlsl(module, names[index], options).Reflection;
            var msl = SpirvCrossCompile.ComputeToMsl(module, names[index], options).Reflection;
            var expected = Assert.Single(hlsl.ResourceLayouts).Elements;
            Assert.Equal(nativeFloat && index < 3 ? (index == 1 ? 5 : 4) : index == 1 ? 7 : 6, expected.Length);
            Assert.Equal(expected, Assert.Single(msl.ResourceLayouts).Elements);
            var declared = Assert.Single(pipeline.Description.ResourceLayouts);
            var actual = Assert.IsType<CoverageCarryLayoutDevice.Layout>(declared).Description.Elements;
            Assert.True(expected.Length == actual.Length,
                $"{names[index]} declares {actual.Length} elements but reflects {expected.Length}.");
            for (int binding = 0; binding < expected.Length; binding++)
            {
                Assert.Equal(buffers[binding] < 2 || buffers[binding] == 6 ? GpuResourceKind.StructuredBufferReadOnly
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
        foreach (var (dispatch, index) in commands.Dispatches.Select((value, index) => (value, index)))
        {
            var declared = Assert.Single(dispatch.Pipeline.Description.ResourceLayouts);
            var layout = Assert.IsType<CoverageCarryLayoutDevice.Layout>(declared);
            Assert.Same(declared, dispatch.Set.Description.Layout);
            Assert.Equal(layout.Description.Elements.Length, dispatch.Set.Description.Resources.Length);
            for (int binding = 0; binding < layout.Description.Elements.Length; binding++)
                Assert.Same(device.Buffers[CoverageCarryShaders.BufferIndices(names[index])[binding]], dispatch.Set.Description.Resources[binding]);
        }
    }
}
