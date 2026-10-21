using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Counts in a temporal program's compiled SPIR-V: the <c>NoContraction</c> decorations <c>precise</c> leaves,
/// and the lock release's barriers, each an <c>OpBitwiseOr</c> of a value with <c>Sizes.x</c>'s bits shifted right by
/// 31.</summary>
internal static class TemporalLockReleaseSpirv
{
    /// <summary>The barriers one lock release compiles to, one per <c>temporalOrdered</c> call.</summary>
    internal const int BarriersPerRelease = 12;

    const int OpTypeInt = 21, OpConstant = 43, OpDecorate = 71, OpShiftRightLogical = 194, OpBitwiseOr = 197;
    const int NoContraction = 42;

    internal static byte[] Fragment(string glsl) => SpirvFrontEnd.ToSpirv(glsl, GpuShaderStages.Fragment, "lock release count");

    /// <summary>OpDecorate NoContraction in a module.</summary>
    internal static int NoContractions(byte[] spirv)
        => Instructions(spirv).Count(i => i.Op == OpDecorate && i.Words.Length >= 3 && i.Words[2] == NoContraction);

    /// <summary>Every OpBitwiseOr in a fragment program's module.</summary>
    internal static int BitwiseOrs(string glsl) => Instructions(Fragment(glsl)).Count(i => i.Op == OpBitwiseOr);

    /// <summary>The OpBitwiseOr whose operand is an unsigned 32-bit shift right by the constant 31.</summary>
    internal static int Barriers(byte[] spirv)
    {
        var instructions = Instructions(spirv).ToList();
        var unsigned = instructions.Where(i => i.Op == OpTypeInt && i.Words[2] == 32 && i.Words[3] == 0)
            .Select(i => i.Words[1]).ToHashSet();
        var thirtyOne = instructions.Where(i => i.Op == OpConstant && unsigned.Contains(i.Words[1]) && i.Words[3] == 31)
            .Select(i => i.Words[2]).ToHashSet();
        var shifts = instructions.Where(i => i.Op == OpShiftRightLogical && thirtyOne.Contains(i.Words[4]))
            .Select(i => i.Words[2]).ToHashSet();
        return instructions.Count(i => i.Op == OpBitwiseOr && (shifts.Contains(i.Words[3]) || shifts.Contains(i.Words[4])));
    }

    internal readonly record struct Instruction(int Op, uint[] Words);

    internal static IEnumerable<Instruction> Instructions(byte[] spirv)
    {
        for (int offset = 20; offset < spirv.Length;)
        {
            uint word = BitConverter.ToUInt32(spirv, offset);
            int count = (int)(word >> 16);
            if (count <= 0) throw new InvalidOperationException($"a zero-length SPIR-V instruction at byte {offset}");
            var words = new uint[count];
            for (int k = 0; k < count; k++) words[k] = BitConverter.ToUInt32(spirv, offset + k * 4);
            yield return new Instruction((int)(word & 0xffff), words);
            offset += count * 4;
        }
    }
}

/// <summary>
/// No <c>precise</c> survives in any shipped temporal program, on any backend, and every program with the lock release
/// carries its twelve barriers (Task 7g.3a). The GLSL holds no <c>precise</c>, the SPIR-V no <c>NoContraction</c>, the
/// emitted HLSL no <c>precise</c> and the emitted MSL none of the <c>spvF</c> wrappers SPIRV-Cross writes for it. The twenty
/// programs that include the release compile exactly twelve barrier ORs each, and TemporalPrepare includes none.
/// Device-free.
/// </summary>
public sealed class TemporalLockReleaseCompiledTests
{
    static readonly string[] Includers = new string[]
    {
        "TemporalResolve", "TemporalResolveHalf", "TemporalAccumulate", "TemporalAccumulateHalf",
        "TemporalAccumulateAtDisplaySize", "TemporalAccumulateAtDisplaySizeHalf", "TemporalCountProbe",
        "TemporalCountProbeHalf", "TemporalDebugView", "TemporalDebugViewHalf",
    }.SelectMany(name => new[] { name, name + "FullyLit" }).ToArray();

    static IEnumerable<ShippedGraphicsProgram> Temporal() => ShippedShaderPrograms.GraphicsPrograms()
        .Where(p => p.Name.Contains("Temporal", StringComparison.Ordinal));

    [Fact]
    public void ExactlyTheCheckedAndFullyLitIncludersCarryTheLockRelease()
    {
        string[] including = ShippedShaderPrograms.GraphicsPrograms()
            .Where(p => p.FragmentGlsl.Contains(ShaderSources.TemporalLockReleaseGlsl, StringComparison.Ordinal))
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Includers.Order(StringComparer.Ordinal), including);
        Assert.DoesNotContain("temporalOrdered", ShaderSources.TemporalPrepareFrag, StringComparison.Ordinal);
    }

    [Fact]
    public void NoShippedTemporalGlslIsPrecise()
    {
        Assert.True(Temporal().Count() >= Includers.Length + 1);
        foreach (ShippedGraphicsProgram program in Temporal())
        {
            Assert.DoesNotMatch(@"\bprecise\b", program.VertexGlsl);
            Assert.True(!Regex.IsMatch(program.FragmentGlsl, @"\bprecise\b"), $"{program.Name} still writes precise");
        }
    }

    [Fact]
    public void NoShippedTemporalSpirvModuleHasANoContractionDecoration()
    {
        foreach (ShippedGraphicsProgram program in Temporal())
        {
            int vertex = TemporalLockReleaseSpirv.NoContractions(SpirvFrontEnd.ToSpirv(program.VertexGlsl,
                GpuShaderStages.Vertex, program.Name));
            int fragment = TemporalLockReleaseSpirv.NoContractions(TemporalLockReleaseSpirv.Fragment(program.FragmentGlsl));
            Assert.True(vertex == 0 && fragment == 0,
                $"{program.Name}: {vertex} vertex and {fragment} fragment NoContraction decorations");
        }
        // The count has teeth: the program D shipped, its precise helper included, carries them.
        Assert.True(TemporalLockReleaseSpirv.NoContractions(TemporalLockReleaseSpirv.Fragment(ShippedD)) > 0);
    }

    static string ShippedD => TemporalVisibilityFrozenD.ToShippedD(ShaderSources.TemporalAccumulateFrag);

    [Fact]
    public void NoEmittedTemporalHlslIsPrecise()
    {
        foreach (ShippedGraphicsProgram program in Temporal())
        {
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(program.VertexGlsl, program.FragmentGlsl, program.Name);
            int count = Regex.Matches(pair.VertexSource, @"\bprecise\b").Count
                + Regex.Matches(pair.FragmentSource, @"\bprecise\b").Count;
            Assert.True(count == 0, $"{program.Name}: {count} precise in the emitted HLSL");
        }
        Assert.Matches(@"\bprecise\b", SpirvCrossCompile.GlslPairToHlsl(ShaderSources.FullscreenVert, ShippedD,
            "shipped D").FragmentSource);
    }

    [Fact]
    public void NoEmittedTemporalMslHasAContractionWrapper()
    {
        foreach (ShippedGraphicsProgram program in Temporal())
        {
            CrossCompiledPair pair = SpirvCrossCompile.VertexFragmentToMsl(
                SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name),
                TemporalLockReleaseSpirv.Fragment(program.FragmentGlsl), program.Name);
            int count = Regex.Matches(pair.VertexSource, @"\bspvF[A-Z]\w*").Count
                + Regex.Matches(pair.FragmentSource, @"\bspvF[A-Z]\w*").Count;
            Assert.True(count == 0, $"{program.Name}: {count} spvF wrapper names in the emitted MSL");
        }
        Assert.Matches(@"\bspvF[A-Z]\w*", SpirvCrossCompile.VertexFragmentToMsl(
            SpirvFrontEnd.ToSpirv(ShaderSources.FullscreenVert, GpuShaderStages.Vertex, "shipped D"),
            TemporalLockReleaseSpirv.Fragment(ShippedD), "shipped D").FragmentSource);
    }

    [Fact]
    public void EveryIncluderCompilesTwelveBarriers()
    {
        foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms()
            .Where(p => Includers.Contains(p.Name)))
        {
            int barriers = TemporalLockReleaseSpirv.Barriers(TemporalLockReleaseSpirv.Fragment(program.FragmentGlsl));
            Assert.True(barriers == TemporalLockReleaseSpirv.BarriersPerRelease,
                $"{program.Name}: {barriers} barrier ORs compiled");
        }
        Assert.Equal(0, TemporalLockReleaseSpirv.Barriers(TemporalLockReleaseSpirv.Fragment(ShaderSources.TemporalPrepareFrag)));
    }

    /// <summary>The barrier count has teeth: the barrier-free rewrite compiles none.</summary>
    [Fact]
    public void TheBarrierFreeRewriteCompilesNoBarrier()
        => Assert.Equal(0, TemporalLockReleaseSpirv.Barriers(TemporalLockReleaseSpirv.Fragment(
            TemporalVisibilityCostPrograms.WithoutBarriers(ShaderSources.TemporalAccumulateFrag))));
}
