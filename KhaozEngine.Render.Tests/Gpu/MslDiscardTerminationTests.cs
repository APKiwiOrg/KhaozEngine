using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Emitted MSL ends the fragment at a discard, KhaozEngine #1411. Without it, stale memory reached every
/// colour attachment of discarded pixels on Apple GPUs. InteriorCutGpuTests is the device-backed regression.</summary>
public sealed class MslDiscardTerminationTests
{
    const string Helper = "void cut() { discard_fragment(); }\n";

    [Theory]
    [InlineData("fragment main0_out main0(main0_in in [[stage_in]])\n{\n    main0_out out = {};\n    if (a) { discard_fragment(); }\n    return out;\n}\n",
        "{ discard_fragment(); return out; }")]
    [InlineData("fragment void main0(main0_in in [[stage_in]])\n{\n    if (a) { discard_fragment(); }\n}\n",
        "{ discard_fragment(); return; }")]
    public void EntryPointDiscardReturnsAndHelpersAreLeftAlone(string entry, string terminated)
    {
        string applied = MslDiscardTermination.Apply(Helper + entry);
        Assert.StartsWith(Helper, applied, StringComparison.Ordinal);
        Assert.Contains(terminated, applied, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceWithoutAFragmentEntryIsUnchanged()
    {
        const string vertex = "vertex main0_out main0(main0_in in [[stage_in]])\n{\n    discard_fragment();\n}\n";
        Assert.Equal(vertex, MslDiscardTermination.Apply(vertex));
    }

    [Fact]
    public void EveryShippedDiscardEndsTheFragmentEntryPoint()
    {
        int discarding = 0;
        var offenders = new List<string>();
        foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
        {
            string msl = SpirvCrossCompile.VertexFragmentToMsl(
                SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name),
                SpirvFrontEnd.ToSpirv(TemporalVisibilityGatherPolicy.ForBackend(program.FragmentGlsl,
                    GpuBackendKind.MetalNative), GpuShaderStages.Fragment, program.Name),
                program.Name).FragmentSource;
            int entry = msl.IndexOf(" main0(", StringComparison.Ordinal);
            int total = Count(msl, "discard_fragment();");
            if (total == 0) continue;
            discarding++;
            if (entry < 0 || Count(msl[..entry], "discard_fragment();") != 0
                || total != Count(msl, "{ discard_fragment(); return"))
                offenders.Add(program.Name);
        }
        Assert.True(discarding > 0, "No shipped program discards, so this guard cannot fail.");
        Assert.Empty(offenders);
    }

    static int Count(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
