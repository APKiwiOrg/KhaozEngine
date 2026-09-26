using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Every temporal fragment is its base program plus the motion lines and nothing else. The check strips the
/// lines that name the motion output or the clip interpolants, and the one sink declaration a variant may add, and
/// requires the base text back byte for byte. A later edit that changes a variant beyond its motion lines fails here.
/// Each task that adds a variant adds its row.</summary>
public sealed class MotionShaderTextTests
{
    static readonly Dictionary<string, (string Variant, string Base, string Sink)> Variants = new()
    {
        ["ModelMotionFrag"] = (ShaderSources.ModelMotionFrag, ShaderSources.ModelFrag, ""),
        ["SkinnedModelMotionFrag"] = (ShaderSources.SkinnedModelMotionFrag, ShaderSources.SkinnedModelFrag,
            "layout(location=9) in vec2 vDissolve;"),
        ["SkinnedModelDissolveMotionFrag"] = (ShaderSources.SkinnedModelDissolveMotionFrag,
            ShaderSources.SkinnedModelDissolveFrag, ""),
    };

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (string name in Variants.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void AVariantIsItsBaseFragmentPlusTheMotionLines(string name)
    {
        (string variant, string baseline, string sink) = Variants[name];
        // The scheduled Windows leg checks out with CRLF line ends, and ShaderText inserts LF lines.
        variant = variant.Replace("\r\n", "\n", StringComparison.Ordinal);
        baseline = baseline.Replace("\r\n", "\n", StringComparison.Ordinal);
        string stripped = string.Join('\n', variant.Split('\n').Where(line =>
            !line.Contains("oMotion", StringComparison.Ordinal)
            && !line.Contains("vCurClip", StringComparison.Ordinal)
            && !line.Contains("vPrevClip", StringComparison.Ordinal)
            && (sink.Length == 0 || line != sink)));
        Assert.True(baseline == stripped, $"{name} differs from its base program beyond the motion lines.");
    }
}
