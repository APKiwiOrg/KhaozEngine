using System;
using System.Linq;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// THE FXC RULE ON EVERY TEMPORAL VARIANT (docs/CROSS-PLATFORM.md, "Keep a fragment shader's input interpolants
/// CONTIGUOUS from location 0"). A motion variant adds two interpolants to a model-pass program, and each is placed so
/// the fragment still reads a gap-free block its vertex emits at the same indices. Read on the emitted HLSL, so it
/// holds on every leg, not only the Windows FXC one. The catalog-wide vertex input sweep in
/// <see cref="D3D11FxcValidationTests"/> covers the other side of each variant.
/// </summary>
public sealed class MotionShaderSignatureTests
{
    public static TheoryData<string> MotionPrograms()
    {
        var data = new TheoryData<string>();
        foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
            if (program.Name.EndsWith("Motion", StringComparison.Ordinal)) data.Add(program.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(MotionPrograms))]
    public void TheFragmentReadsAGapFreeBlockItsVertexEmitsAtTheSameIndices(string name)
    {
        ShippedGraphicsProgram program = D3D11FxcValidationTests.Program(name);
        CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(program.VertexGlsl, program.FragmentGlsl, name);
        uint[] outputs = D3D11FxcValidationTests.Semantics(pair.VertexSource, "SPIRV_Cross_Output");
        uint[] inputs = D3D11FxcValidationTests.Semantics(pair.FragmentSource, "SPIRV_Cross_Input");

        Assert.Equal(Enumerable.Range(0, inputs.Length).Select(i => (uint)i).ToArray(), inputs);
        Assert.All(inputs, index => Assert.Contains(index, outputs));
    }

    [Theory]
    [MemberData(nameof(MotionPrograms))]
    public void EveryVariantWritesTheFourthOutput(string name)
    {
        string fragment = D3D11FxcValidationTests.Program(name).FragmentGlsl;
        Assert.Contains("layout(location=3) out vec4 oMotion;", fragment, StringComparison.Ordinal);
        if (fragment.Contains("vCurClip", StringComparison.Ordinal))
            Assert.Contains("oMotion = vec4(vPrevClip.w <= 1e-6 ? vec2(2.0, 2.0) : "
                + "(vCurClip.xy / vCurClip.w - vPrevClip.xy / vPrevClip.w) * vec2(0.5, -0.5), 0.0, 1.0);",
                fragment, StringComparison.Ordinal);
        else
            Assert.Contains("oMotion = vec4(0.0);", fragment, StringComparison.Ordinal);
    }

    /// <summary>The tile-ground pair reads every interpolant its vertex emits, so its variant must too: the two
    /// signatures are the same set, as <c>D3D11FxcValidationTests.TheTileGroundInterpolants_AreGapFreeAndEntirelyRead</c>
    /// holds for the base pair.</summary>
    [Fact]
    public void TheTileGroundVariantReadsEveryInterpolantItsVertexEmits()
    {
        CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
            ShaderSources.TileGroundMotionVert, ShaderSources.TileGroundMotionFrag, "TileGroundMotion");

        Assert.Equal(D3D11FxcValidationTests.Semantics(pair.VertexSource, "SPIRV_Cross_Output"),
            D3D11FxcValidationTests.Semantics(pair.FragmentSource, "SPIRV_Cross_Input"));
    }

    /// <summary>The splat variant is the one that moves interpolants. SplatFrag reads 0 to 4 and SplatVert parks three
    /// outputs it never reads at 5 to 7, so the variant moves those three to 7 to 9 and the pair joins the read block at
    /// 5 and 6. Its fragment reads exactly 0 to 6 and its vertex emits exactly 0 to 9, so every parked interpolant sits
    /// above everything the fragment reads, as
    /// <c>D3D11FxcValidationTests.TheTerrainInterpolantOrdering_KeepsTheFragmentInputsAGapFreePrefix</c> holds for the
    /// base pair.</summary>
    [Fact]
    public void TheSplatVariantParksItsThreeUnreadInterpolantsAboveThePair()
    {
        CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(
            ShaderSources.SplatMotionVert, ShaderSources.SplatMotionFrag, "SplatMotion");

        Assert.Equal(Enumerable.Range(0, 10).Select(i => (uint)i).ToArray(),
            D3D11FxcValidationTests.Semantics(pair.VertexSource, "SPIRV_Cross_Output"));
        Assert.Equal(Enumerable.Range(0, 7).Select(i => (uint)i).ToArray(),
            D3D11FxcValidationTests.Semantics(pair.FragmentSource, "SPIRV_Cross_Input"));
    }
}
