using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryKernelTests
{
    public static IEnumerable<object[]> Stages() => Enumerable.Range(0, 4).Select(i => new object[] { i });
    [Fact]
    public void Runtime_inventory_contains_only_the_four_retained_stages()
        => Assert.Equal(new[] { "CoverageCarrySkin", "CoverageCarryTransport", "CoverageCarryBasis", "CoverageCarryPublish" },
            CoverageCarryShaders.Kernels.Select(k => k.Name));

    [Theory]
    [MemberData(nameof(Stages))]
    public void Physical_programs_keep_the_retained_scalar_worker_and_size_caps(int stage)
    {
        var kernel = CoverageCarryShaders.Kernels[stage];
        Assert.True(CoverageCarryShaders.Options.PreciseMath);
        byte[] module = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options);
        string hlsl = SpirvCrossCompile.ComputeToHlsl(module, kernel.Name, CoverageCarryShaders.Options).ComputeSource;
        string msl = SpirvCrossCompile.ComputeToMsl(module, kernel.Name, CoverageCarryShaders.Options).ComputeSource;
        Assert.InRange(Encoding.UTF8.GetByteCount(hlsl), 1, 100_000);
        Assert.InRange(Encoding.UTF8.GetByteCount(msl), 1, 100_000);
        ValidateCallTree(module);
        CoverageSpirvAccessAudit.Validate(module, kernel.Name, constantPrivateArrays: true);
        Assert.DoesNotContain("CovPreviousWords", kernel.Source);
        Assert.DoesNotContain("covPredicateProduct", kernel.Source);
        Assert.DoesNotContain("covExactProduct", kernel.Source);
    }

    [Fact]
    public void Publication_tail_lanes_reach_every_barrier()
    {
        var kernel = CoverageCarryShaders.Kernels[3];
        byte[] module = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options);
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        uint entry = 0, function = 0;
        var body = new List<uint>();
        for (int at = 5; at < words.Length; at += (int)(words[at] >> 16))
        {
            uint opcode = words[at] & 65535;
            if (opcode == 15) entry = words[at + 2];
            if (opcode == 54) function = words[at + 2];
            if (function == entry) body.Add(opcode);
            if (opcode == 56) function = 0;
        }
        Assert.Contains(224u, body);
        Assert.Single(body, opcode => opcode == 253);
        Assert.True(body.IndexOf(253) > body.LastIndexOf(224));
    }

    static void ValidateCallTree(byte[] module)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        var names = new Dictionary<uint, string>();
        var functions = new Dictionary<uint, (int Instructions, int Returns, List<uint> Calls)>();
        uint current = 0, entry = 0;
        int globals = 0;
        for (int at = 5; at < words.Length; at += (int)(words[at] >> 16))
        {
            int length = (int)(words[at] >> 16), op = (int)(words[at] & 65535);
            if (op == 5) names[words[at + 1]] = Encoding.UTF8.GetString(MemoryMarshal.AsBytes(words.AsSpan(at + 2, length - 2))).TrimEnd('\0');
            if (op == 15) entry = words[at + 2];
            if (op == 54) { current = words[at + 2]; functions.Add(current, (0, 0, [])); }
            if (current == 0) globals++;
            else
            {
                var function = functions[current];
                function.Instructions++;
                if (op is 253 or 254) function.Returns++;
                if (op == 57) function.Calls.Add(words[at + 3]);
                if (op == 246) Assert.NotEqual(0u, words[at + 3] & 2u);
                functions[current] = function;
            }
            if (op == 56) current = 0;
        }
        long Bound(uint id) => functions[id].Instructions + 8L * Math.Max(0, functions[id].Returns - 1) + functions[id].Calls.Sum(Bound);
        int Sites(uint id, string helper) => functions[id].Calls.Sum(c =>
            (names.GetValueOrDefault(c, "").StartsWith(helper + "(", StringComparison.Ordinal) ? 1 : 0) + Sites(c, helper));
        Assert.InRange(globals + Bound(entry), 1, 30_000);
        foreach (string helper in new[] { "covExactWorker", "covRoundOperation", "covCorrectPositive", "covRoundedAtOrBelow", "covResidualProduct", "covExactMul32" })
            Assert.Equal(1, Sites(entry, helper));
    }
}
