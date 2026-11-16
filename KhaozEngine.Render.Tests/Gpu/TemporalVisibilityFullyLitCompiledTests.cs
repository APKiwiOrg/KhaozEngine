using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class TemporalVisibilityFullyLitCompiledTests
{
    public static IEnumerable<object[]> Programs() => ShippedShaderPrograms.GraphicsPrograms()
        .Where(p => p.Name.EndsWith("FullyLit", StringComparison.Ordinal)).Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(Programs))]
    public void CertifiedSourcesOmitTheResourcesTheirCompactLayoutsDoNotBind(string name)
    {
        string source = ShippedShaderPrograms.GraphicsPrograms().Single(p => p.Name == name).FragmentGlsl;
        Assert.DoesNotContain("KeyShadowVisibility", source, StringComparison.Ordinal);
        Assert.DoesNotContain("uniform VisibilityHistory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("temporalVisibilityReleases(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("temporalVisibilityChange(", source, StringComparison.Ordinal);
        Assert.Contains("bool visibilityReleased = false;", source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Programs))]
    public void FrozenDRewritesRestoreTheOrdinaryProgramBeforeReplacingItsDecision(string name)
    {
        ShippedGraphicsProgram certified = ShippedShaderPrograms.GraphicsPrograms().Single(p => p.Name == name);
        ShippedGraphicsProgram ordinary = ShippedShaderPrograms.GraphicsPrograms()
            .Single(p => p.Name == name[..^"FullyLit".Length]);
        Assert.Equal(TemporalVisibilityFrozenD.ToD(ordinary.FragmentGlsl),
            TemporalVisibilityFrozenD.ToD(certified.FragmentGlsl));
        Assert.Equal(TemporalVisibilityFrozenD.ToShippedD(ordinary.FragmentGlsl),
            TemporalVisibilityFrozenD.ToShippedD(certified.FragmentGlsl));
    }

    [Theory]
    [MemberData(nameof(Programs))]
    public void CertifiedModulesHaveNoVisibilityReadsDecisionHelperOrGathers(string name)
    {
        ShippedGraphicsProgram program = ShippedShaderPrograms.GraphicsPrograms().Single(p => p.Name == name);
        byte[] vertex = SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, name);
        byte[] fragment = SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, name);
        foreach (string source in new[]
        {
            SpirvCrossCompile.VertexFragmentToMsl(vertex, fragment, name).FragmentSource,
            SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, name).FragmentSource,
        })
        {
            Assert.DoesNotContain("KeyShadowVisibility", source, StringComparison.Ordinal);
            Assert.DoesNotContain("temporalVisibilityReleases", source, StringComparison.Ordinal);
            Assert.DoesNotContain("temporalVisibilityChange(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("stored00", source, StringComparison.Ordinal);
        }
        for (int offset = 20; offset < fragment.Length;)
        {
            uint word = BitConverter.ToUInt32(fragment, offset);
            Assert.NotEqual(96u, word & 0xffffu); // OpImageGather belongs only to the visibility decision.
            int words = (int)(word >> 16);
            Assert.True(words > 0);
            offset += 4 * words;
        }
    }
}
