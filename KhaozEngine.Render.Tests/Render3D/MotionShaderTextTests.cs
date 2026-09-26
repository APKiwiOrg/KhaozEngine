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
        ["ModelDissolveMotionFrag"] = (ShaderSources.ModelDissolveMotionFrag, ShaderSources.ModelDissolveFrag,
            "layout(location=10) in float vDissolveComplement;"),
        ["SplatMotionFrag"] = (ShaderSources.SplatMotionFrag, ShaderSources.SplatFrag, ""),
        ["TileGroundMotionFrag"] = (ShaderSources.TileGroundMotionFrag, ShaderSources.TileGroundFrag, ""),
        ["TexturedBillboardMotionFrag"] = (ShaderSources.TexturedBillboardMotionFrag, ShaderSources.TexturedBillboardFrag, ""),
        ["BeamMotionFrag"] = (ShaderSources.BeamMotionFrag, ShaderSources.BeamFrag, ""),
        ["TrailMotionFrag"] = (ShaderSources.TrailMotionFrag, ShaderSources.TrailFrag, ""),
        ["OverlayUnlitMotionFrag"] = (ShaderSources.OverlayUnlitMotionFrag, ShaderSources.OverlayUnlitFrag, ""),
        ["SilhouetteMotionFrag"] = (ShaderSources.SilhouetteMotionFrag, ShaderSources.SilhouetteFrag, ""),
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

    static string[] Lines(string source) => source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    static bool IsClip(string line) =>
        line.Contains("vCurClip", StringComparison.Ordinal) || line.Contains("vPrevClip", StringComparison.Ordinal);

    static bool Declares(string line, string direction) =>
        line.StartsWith("layout(location=", StringComparison.Ordinal) && line.Contains(direction, StringComparison.Ordinal);

    // The vertex inputs, whole lines.
    static string[] Inputs(string[] lines) => lines.Where(line => Declares(line, ") in ")).ToArray();

    // The outputs as type and name, without the location and comment a variant may change, and without the clip pair.
    static string[] Outputs(string[] lines) => lines
        .Where(line => Declares(line, ") out ") && !IsClip(line))
        .Select(line =>
        {
            string declaration = line[(line.IndexOf(") out ", StringComparison.Ordinal) + ") out ".Length)..];
            return declaration[..(declaration.IndexOf(';', StringComparison.Ordinal) + 1)];
        })
        .ToArray();

    // From gl_Position to the end of the program, without the clip pair.
    static string[] Tail(string[] lines) => lines
        .SkipWhile(line => !line.TrimStart().StartsWith("gl_Position", StringComparison.Ordinal))
        .Where(line => !IsClip(line))
        .ToArray();

    /// <summary>FoliageVert's statements up to gl_Position, with the renames foliageWorld makes (the focus, clock,
    /// pixel scale and fade matrix become parameters, and the interactor reads go through the current-or-previous
    /// locals), equal foliageWorld's body line for line. The inputs, the outputs and the rest of main match too.</summary>
    [Fact]
    public void TheFoliageVariantRestatesFoliageVertLineForLine()
    {
        string[] baseline = Lines(ShaderSources.FoliageVert);
        string[] variant = Lines(ShaderSources.FoliageMotionVert);
        string[] deformation = baseline
            .SkipWhile(line => line != "void main() {").Skip(1)
            .TakeWhile(line => !line.TrimStart().StartsWith("gl_Position", StringComparison.Ordinal))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(line => line
                .Replace("FocusRadius.xz", "focus.xz", StringComparison.Ordinal)
                .Replace("bool rejected =", "rejected =", StringComparison.Ordinal)
                .Replace("mat4 Model =", "Model =", StringComparison.Ordinal)
                .Replace("WindTime.z * FadeWind.z", "windTime * FadeWind.z", StringComparison.Ordinal)
                .Replace("(ViewProj * vec4(root, 1.0)).w, 0.0) * WindFade.y",
                    "(fadeViewProj * vec4(root, 1.0)).w, 0.0) * metresPerPixel", StringComparison.Ordinal)
                .Replace("Interactors[i]", "interactor", StringComparison.Ordinal)
                .Replace("Strengths[i]", "strength", StringComparison.Ordinal))
            .ToArray();
        string[] function = variant
            .SkipWhile(line => !line.StartsWith("vec3 foliageWorld(", StringComparison.Ordinal)).Skip(2)
            .TakeWhile(line => line != "    return world.xyz;")
            .Where(line => !line.Contains("previous ? Prev", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(deformation, function);
        // The lines that give the variant its last-frame meaning, which the comparison above leaves out.
        Assert.Contains("        vec4 interactor = previous ? PrevInteractors[i] : Interactors[i];", variant);
        Assert.Contains("        float strength = previous ? PrevStrengths[i] : Strengths[i];", variant);
        Assert.Contains("    vec4 world = vec4(foliageWorld(FocusRadius.xyz, WindTime.z, WindFade.y, ViewProj, false, Model, "
            + "rejected), 1.0);", variant);
        Assert.Contains("    vec3 lastWorld = foliageWorld(PrevFocus.xyz, PrevFocus.w, WindFade.z, PrevViewProj, true, lastModel, "
            + "lastRejected);", variant);
        Assert.Equal(Inputs(baseline), Inputs(variant));
        Assert.Equal(Outputs(baseline), Outputs(variant));
        Assert.Equal(Tail(baseline), Tail(variant));
    }

    // main and everything after it, without the clip pair. Not named Main, which C# reads as a bad entry point (CS0028).
    static string[] FromMain(string[] lines) => lines.SkipWhile(line => line != "void main() {").Where(line => !IsClip(line)).ToArray();

    // The name an interpolant declaration declares.
    static string Name(string line) => line[..line.IndexOf(';', StringComparison.Ordinal)].Split(' ')[^1];

    // The lines outside the block that opens on the line containing open, through its closing brace line.
    static IEnumerable<string> WithoutBlock(IEnumerable<string> lines, string open)
    {
        bool inside = false;
        foreach (string line in lines)
        {
            if (!inside && line.Contains(open, StringComparison.Ordinal)) inside = true;
            if (!inside) yield return line;
            else if (line == "};") inside = false;
        }
    }

    /// <summary>SplatMotionVert moves SplatVert's three fragment-unused outputs to make room for the clip pair, so it is
    /// written out whole. It keeps SplatVert's inputs line for line, its outputs apart from their locations, and its main
    /// apart from the clip pair. Every other line of SplatVert is held too: the frame block by its members in order,
    /// because the variant spells it compactly, and the rest whole, which pins the five outputs SplatFrag reads to their
    /// locations. Only the comment on the old layout and the three moved outputs are free to differ.</summary>
    [Fact]
    public void TheSplatVariantRestatesSplatVertLineForLine()
    {
        string[] baseline = Lines(ShaderSources.SplatVert);
        string[] variant = Lines(ShaderSources.SplatMotionVert);
        Assert.Equal(Inputs(baseline), Inputs(variant));
        Assert.Equal(Outputs(baseline), Outputs(variant));
        Assert.Equal(FromMain(baseline), FromMain(variant));

        string[] read = Inputs(Lines(ShaderSources.SplatFrag)).Select(Name).ToArray();
        string[] Rest(string[] lines) => WithoutBlock(WithoutBlock(lines, "uniform U {"), "uniform MotionFrame {")
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !IsClip(line)
                && !(Declares(line, ") out ") && !read.Contains(Name(line))))
            .ToArray();
        Assert.Equal(5, read.Length);
        Assert.Equal(MotionUboLayoutTests.Members(ShaderSources.SplatVert, "uniform U {"),
            MotionUboLayoutTests.Members(ShaderSources.SplatMotionVert, "uniform U {"));
        Assert.Equal(Rest(baseline), Rest(variant));
    }
}
