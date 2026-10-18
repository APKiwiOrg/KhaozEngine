using System;
using System.IO;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class CoverageGpuArithmeticTests(ITestOutputHelper output)
{
    internal static readonly GpuShaderCompileOptions Precise = new(PreciseMath: true);

    [Fact]
    public void Inventory_words_and_precise_lowering_are_pinned()
    {
        uint[] inputs = CoverageGpuArithmeticProbe.Inputs();
        AssertPinnedWords(inputs, CoverageGpuArithmeticProbe.Reference(inputs));
        byte[] spirv = SpirvFrontEnd.ToSpirv(CoverageGpuArithmeticProbe.Source, GpuShaderStages.Compute,
            CoverageGpuArithmeticProbe.Name, Precise);
        int missingFlushOrNoContractionSites = CoverageArithmeticSpirvAudit.MissingSites(spirv, output);
        Assert.Equal(0, missingFlushOrNoContractionSites);
        byte[] unqualified = SpirvFrontEnd.ToSpirv(CoverageGpuArithmeticProbe.Source.Replace("precise ", "", StringComparison.Ordinal),
            GpuShaderStages.Compute, "coverage unqualified control", Precise);
        Assert.True(CoverageArithmeticSpirvAudit.MissingSites(unqualified, output) > 0);
        string hlsl = SpirvCrossCompile.ComputeToHlsl(spirv, options: Precise).ComputeSource;
        string msl = SpirvCrossCompile.ComputeToMsl(spirv, options: Precise).ComputeSource;
        Assert.Contains("precise float _161 = a + b;", hlsl);
        Assert.Contains("precise float _176 = a - b;", hlsl);
        Assert.Contains("precise float _191 = a * b;", hlsl);
        Assert.Contains("spvFAdd", msl);
        Assert.Contains("spvFSub", msl);
        Assert.Contains("return fma(l, r, T(-0.0));", msl);
        Assert.False(MslCompilePin.FastMathEnabledFor(MetalShaderStage.Compute, Precise));
        Assert.Equal(0x00002000u, D3D11ShaderCompileFlags.For(D3D11ShaderStage.Compute, 0, Precise));
        Assert.Equal(Silk.NET.Shaderc.OptimizationLevel.Zero, SpirvFrontEndPin.OptimizationFor(Precise));
        ShaderValidation.ValidateCompute(CoverageGpuArithmeticProbe.Source, Precise, CoverageGpuArithmeticProbe.Name);
        string? directory = Environment.GetEnvironmentVariable("KE_SHADER_CORPUS_DUMP");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "CoverageArithmetic.compute.spirv"), spirv);
            File.WriteAllText(Path.Combine(directory, "CoverageArithmetic.compute.hlsl"), hlsl);
            File.WriteAllText(Path.Combine(directory, "CoverageArithmetic.compute.msl"), msl);
        }
        output.WriteLine("Shaderc Zero, HLSL precise, FXC IEEE_STRICTNESS=0x2000, MSL fast math off");
    }

    internal static void AssertPinnedWords(uint[] inputs, uint[] words)
    {
        uint At(uint a, uint b, int field)
        {
            for (int row = 0; row < inputs[0]; row++)
                if (inputs[1 + row * CoverageGpuArithmeticProbe.InputWords] == a && inputs[1 + row * CoverageGpuArithmeticProbe.InputWords + 3] == b)
                    return words[row * CoverageGpuArithmeticProbe.OutputWords + field];
            throw new InvalidOperationException("Missing pinned input row");
        }
        uint flushNegativeSmallestNormal = At(0x80800000u, 0u, 0);
        uint flushNextNormal = At(0x00800001u, 0u, 0);
        uint minPositiveNegativeZero = At(0u, 0x80000000u, 8);
        uint maxPositiveNegativeZero = At(0u, 0x80000000u, 9);
        uint clampNegativeZeroToPositiveZero = At(0x80000000u, 0u, 10);
        uint separateMultiplyAdd = At(0x3f800001u, 0x3f7ffffeu, 18);
        bool nanFinite = At(0x7fc00000u, 0x3f800000u, 11) != 0;
        bool nanNotEqual = At(0x7fc00000u, 0x3f800000u, 17) != 0;
        Assert.Equal(0x80000000u, flushNegativeSmallestNormal);
        Assert.Equal(0x00800001u, flushNextNormal);
        Assert.Equal(0x80000000u, minPositiveNegativeZero);
        Assert.Equal(0u, maxPositiveNegativeZero);
        Assert.Equal(0x80000000u, clampNegativeZeroToPositiveZero);
        Assert.Equal(0u, separateMultiplyAdd); // (0x3f800001 * 0x3f7ffffe) + -1
        Assert.False(nanFinite);
        Assert.True(nanNotEqual);
        // WARP must not let the infinite Z lane poison the selected finite X lane.
        Assert.Equal(0xbfffffffu, At(0u, 0x7f7fffffu, 21));
        Assert.Equal(0xff800000u, At(0u, 0x7f7fffffu, 23));
        int firstVectorRow = (int)inputs[0] - CoverageGpuArithmeticProbe.VectorCases.Length;
        for (int row = 0; row < CoverageGpuArithmeticProbe.VectorCases.Length; row++)
        {
            var literal = CoverageGpuArithmeticProbe.VectorCases[row];
            Assert.Equal(literal.Equal, words[(firstVectorRow + row) * CoverageGpuArithmeticProbe.OutputWords + 33]);
            Assert.Equal(literal.Finite, words[(firstVectorRow + row) * CoverageGpuArithmeticProbe.OutputWords + 34]);
        }
        Assert.Equal(0u, At(0x00800000u, 0u, 0));
        Assert.Equal(0u, At(0x00800001u, 0x00800002u, 4) & 0x7fffffffu);
        Assert.Equal(0u, At(0x00800001u, 0x3f7ffffeu, 5));
        Assert.Equal(0x00800001u, At(0x00800001u, 0x3f800000u, 5));
    }
}
