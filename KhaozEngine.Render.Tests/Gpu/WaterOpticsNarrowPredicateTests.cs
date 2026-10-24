using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class WaterOpticsNarrowPredicateTests
{
    const string Evidence = "/tmp/khaozengine-water-triangles/task-2/predicate-fastpath";
    sealed record Probe(string Name, uint[] Bits, bool Narrow);

    // Read the production helpers, not a numerical reimplementation of the shader.
    static string Helper(string name) => (string)typeof(ShaderSources)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    static string Source => @"#version 450
layout(local_size_x=1) in;
layout(std430,set=0,binding=0) buffer Results { ivec4 Values[]; };
layout(std430,set=0,binding=1) readonly buffer Inputs { uvec4 Bits[]; };
" + Helper("WaterOpticsBinary32Glsl") + Helper("WaterOpticsPredicatesGlsl") + @"
void main() {
    uint i = gl_GlobalInvocationID.x;
    uvec2 a = Bits[2u*i].xy, b = Bits[2u*i].zw, p = Bits[2u*i+1u].xy;
    int baseShift = opticsOrientBaseShift(a,b,p);
    Values[i] = ivec4(opticsOrient(a,b,p),baseShift,
        baseShift >= 0 ? opticsOrientNarrow(a,b,p,baseShift) : 0,opticsOrientWide(a,b,p));
}";

    [Fact]
    public void ExactPredicateCompilesToAllBackendForms()
    {
        Directory.CreateDirectory(Evidence);
        byte[] spirv = SpirvFrontEnd.ToSpirv(Source, GpuShaderStages.Compute);
        File.WriteAllText(Path.Combine(Evidence, "predicate.glsl"), Source);
        File.WriteAllBytes(Path.Combine(Evidence, "predicate.spv"), spirv);
        File.WriteAllText(Path.Combine(Evidence, "predicate.hlsl"), SpirvCrossCompile.GlslComputeToHlsl(Source).ComputeSource);
        File.WriteAllText(Path.Combine(Evidence, "predicate.msl"), SpirvCrossCompile.ComputeToMsl(spirv).ComputeSource);
    }

    [GpuFact]
    public void CertifiedSpanCarryCancellationAndFallbackMatchIndependentDyadicOracle()
    {
        Probe[] probes = Probes().ToArray();
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        IGpuDevice device = gpu.GpuDevice;
        IGpuResourceFactory f = device.Factory;
        var bits = new uint[probes.Length * 8];
        for (int i = 0; i < probes.Length; i++) probes[i].Bits.CopyTo(bits, i * 8);
        using IGpuBuffer input = f.CreateBuffer(new GpuBufferDescription((uint)bits.Length * 4,
            GpuBufferUsage.StructuredBufferReadOnly, 16));
        using IGpuBuffer result = f.CreateBuffer(new GpuBufferDescription((uint)probes.Length * 16,
            GpuBufferUsage.StructuredBufferReadWrite, 16));
        device.UpdateBuffer(input, 0, bits);
        using IGpuResourceLayout layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Results", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new GpuResourceLayoutElement("Inputs", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute)));
        using IGpuResourceSet set = f.CreateResourceSet(new GpuResourceSetDescription(layout, result, input));
        using IGpuComputeShader shader = f.CreateComputeShaderFromSpirv(Source);
        using IGpuComputePipeline pipeline = f.CreateComputePipeline(new GpuComputePipelineDescription(shader, layout));
        using (IGpuCommandList commands = f.CreateCommandList())
        {
            commands.Begin();
            commands.SetComputePipeline(pipeline);
            commands.SetComputeResourceSet(0, set);
            commands.Dispatch((uint)probes.Length, 1, 1);
            commands.End();
            device.Submit(commands);
            device.WaitForIdle();
        }
        int[] values = GpuReadback.ReadBuffer<int>(device, result, probes.Length * 4);
        Directory.CreateDirectory(Evidence);
        File.WriteAllText(Path.Combine(Evidence, "predicate-results.json"), JsonSerializer.Serialize(new
        {
            device.Backend,
            device.Capabilities.DeviceName,
            Probes = probes.Select((probe, i) => new
            {
                probe.Name,
                probe.Bits,
                probe.Narrow,
                ExpectedSign = ExactSign(probe.Bits),
                Sign = values[4 * i],
                BaseShift = values[4 * i + 1],
                NarrowSign = values[4 * i + 2],
                WideSign = values[4 * i + 3]
            })
        }, new JsonSerializerOptions { WriteIndented = true }));
        for (int i = 0; i < probes.Length; i++)
        {
            Assert.True(ExactSign(probes[i].Bits) == values[4 * i], $"{probes[i].Name}: wrong exact orientation");
            Assert.True(probes[i].Narrow == (values[4 * i + 1] >= 0), $"{probes[i].Name}: wrong certified domain");
            Assert.Equal(ExactSign(probes[i].Bits), values[4 * i + 3]);
            if (probes[i].Narrow) Assert.Equal(ExactSign(probes[i].Bits), values[4 * i + 2]);
        }
    }

    static IEnumerable<Probe> Probes()
    {
        const uint one = 0x3fc00000, next = one + 1, previous = one - 1, negative = 0x80000000;
        yield return new("signed-zeros", [0, negative, negative, 0, 0, negative], true);
        yield return new("zero-products-do-not-widen-domain", [0x7f7fffff, 0, 0, 0x7f7fffff, 0, 0], true);
        const uint top = (166u << 23) | 0x7fffff;
        yield return new("one-product-unit-negative", [top, top, top - 1, top - 2, top - 2, top - 3], true);
        yield return new("one-product-unit-positive", [top - 1, top - 2, top, top, top - 2, top - 3], true);
        yield return new("full-product-cancellation", [top, top, top - 1, top - 2, top - 2, top - 4], true);
        foreach (int span in new[] { 0, 1, 15, 16, 31, 32, 33, 47, 63, 64, 65, 76, 77, 78 })
        {
            uint low = (uint)(127 - span) << 23;
            foreach (uint x in new[] { previous, one, next })
                yield return new($"shared-edge-span-{span}-{x:X8}", [one, low, one, one, x, one], span <= 77);
            // Four maximal products add on one side, with two equal low products cancelling.
            uint high = (166u << 23) | 0x7fffff, small = ((uint)(166 - span) << 23) | 0x7fffff;
            yield return new($"carry-span-{span}", [high | negative, small, high, high | negative, high, high], span <= 77);
            yield return new($"carry-reversed-span-{span}", [high, high | negative, high | negative, small, high, high], span <= 77);
        }
        foreach (int span in new[] { 77, 78 })
            foreach (uint tiny in new uint[] { 1, 0x7fffff })
            {
                uint normal = ((uint)span + 1) << 23;
                yield return new($"subnormal-query-{span}-{tiny}", [normal, normal, normal + 1, normal, normal, tiny], span <= 77);
                yield return new($"subnormal-edge-{span}-{tiny}", [normal, tiny, normal, normal, normal + 1, normal], span <= 77);
            }
        yield return new("all-subnormal-cancellation", [1, 2, 2, 4, 3, 6], true);
        yield return new("all-subnormal-positive", [1, 2, 2, 4, 3, 7], true);
        yield return new("extreme-fallback", [1, 0x7f7fffff, 0x7f7fffff, 1, 0x7f7fffff, 0x7f7fffff], false);
        yield return new("extreme-fallback-negative", [0x80000001, 0x7f7fffff, 0x7f7fffff, 1, 0xff7fffff, 0x7f7fffff], false);
    }

    // Independent arbitrary-precision determinant of exact source bits in units of 2^-149.
    // No packed atlas, software float helpers, product expansion or fixed-width carry algorithm.
    static int ExactSign(uint[] bits)
    {
        BigInteger[] v = bits.Select(Coordinate).ToArray();
        return ((v[2] - v[0]) * (v[5] - v[1]) - (v[3] - v[1]) * (v[4] - v[0])).Sign;
    }

    static BigInteger Coordinate(uint bits)
    {
        int exponent = (int)((bits >> 23) & 255);
        BigInteger value = bits & 0x7fffff;
        if (exponent > 0) value = (value + 0x800000) << (exponent - 1);
        return (bits & 0x80000000) == 0 ? value : -value;
    }
}
