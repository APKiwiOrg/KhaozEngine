using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageGpuExactWordsTests(ITestOutputHelper output)
{
    [Fact]
    public void Limb_carries_midpoints_and_corrected_results_match_independent_integers()
    {
        Assert.Equal(0x3f800000u, CoverageBinary32IntegerOracle.Round((BigInteger.One << 24) + 1, -24, 1));
        Assert.Equal(0x3f800002u, CoverageBinary32IntegerOracle.Round((BigInteger.One << 24) + 3, -24, 1));
        Assert.Equal(0x3eaaaaabu, CoverageBinary32IntegerOracle.Divide(0x3f800000u, 0x40400000u));
        Assert.Equal(0x3fb504f3u, CoverageBinary32IntegerOracle.Sqrt(0x40000000u));
        Assert.Equal(0x00800000u, CoverageBinary32IntegerOracle.Divide(0x01000000u, 0x40000000u));
        Assert.Equal(0u, CoverageBinary32IntegerOracle.Flush(0x00800000u));
        Assert.Equal(0x80000000u, CoverageBinary32IntegerOracle.Sqrt(0x80000000u));
        Assert.Equal(0x7f800000u, CoverageBinary32IntegerOracle.Divide(0x7f7fffffu, 0x3f000000u));
        var reference = new CoverageReferenceArithmetic();
        foreach (var pair in CoverageGpuRoundingProbe.Divisions)
            AssertReferenceWord(CoverageBinary32IntegerOracle.Flush(CoverageBinary32IntegerOracle.Divide(pair.A, pair.B)),
                BitConverter.SingleToUInt32Bits(reference.Div(BitConverter.UInt32BitsToSingle(pair.A), BitConverter.UInt32BitsToSingle(pair.B))));
        foreach (uint a in CoverageGpuRoundingProbe.Roots)
            AssertReferenceWord(CoverageBinary32IntegerOracle.Flush(CoverageBinary32IntegerOracle.Sqrt(a)),
                BitConverter.SingleToUInt32Bits(reference.Sqrt(BitConverter.UInt32BitsToSingle(a))));
        Validate(CoverageGpuExactWordsProbe.Name, CoverageGpuExactWordsProbe.Source);
        Validate(CoverageGpuRoundingProbe.Name, CoverageGpuRoundingProbe.Source);
    }

    static void AssertReferenceWord(uint expected, uint actual)
    {
        // Contract section 3 admits no NaN payload. Its exponent must trigger the same
        // invalid-intermediate guard. The native probe still compares all oracle bits.
        if ((expected & 0x7fffffffu) > CoverageBinary32IntegerOracle.Infinity)
            Assert.True((actual & 0x7fffffffu) > CoverageBinary32IntegerOracle.Infinity);
        else Assert.Equal(expected, actual);
    }

    void Validate(string name, string source)
    {
        var precise = new GpuShaderCompileOptions(PreciseMath: true);
        byte[] spirv = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, name, precise);
        CoverageSpirvAccessAudit.Validate(spirv, name);
        AssertInstructions(spirv, name == CoverageGpuRoundingProbe.Name);
        var hlslResult = SpirvCrossCompile.ComputeToHlsl(spirv, name, precise);
        var mslResult = SpirvCrossCompile.ComputeToMsl(spirv, name, precise);
        string hlsl = hlslResult.ComputeSource.ReplaceLineEndings("\n");
        string msl = mslResult.ComputeSource.ReplaceLineEndings("\n");
        Assert.DoesNotContain("uint64_t", hlsl); Assert.DoesNotContain("uint64_t", msl);
        Assert.DoesNotContain("double", hlsl); Assert.DoesNotContain("double", msl);
        if (name == CoverageGpuRoundingProbe.Name)
        {
            Assert.Contains(" / ", hlsl); Assert.Contains("sqrt(", hlsl);
            Assert.Contains(" / ", msl); Assert.Contains("sqrt(", msl);
        }
        ShaderValidation.ValidateCompute(source, precise, name);
        string? directory = Environment.GetEnvironmentVariable("KE_SHADER_CORPUS_DUMP");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, name + ".compute.spirv"), spirv);
            File.WriteAllText(Path.Combine(directory, name + ".compute.hlsl"), hlsl);
            File.WriteAllText(Path.Combine(directory, name + ".compute.msl"), msl);
            string layout = ShaderCorpus.Canonical(hlslResult.Reflection);
            Assert.Equal(layout, ShaderCorpus.Canonical(mslResult.Reflection));
            var rows = new List<string>();
            foreach (string target in new[] { "hlsl", "msl", "spirv" })
            {
                string path = Path.Combine(directory, name + ".compute." + target);
                byte[] bytes = File.ReadAllBytes(path);
                rows.Add($"{name}.compute.{target} {Convert.ToHexStringLower(SHA256.HashData(bytes))} {bytes.Length}");
            }
            string layoutHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(layout)));
            rows.Add($"{name}.layout.hlsl {layoutHash} 2 {layout}");
            rows.Add($"{name}.layout.msl {layoutHash} 2 {layout}");
            File.WriteAllLines(Path.Combine(directory, name + ".corpus-rows.txt"), rows);
        }
        output.WriteLine($"{name}: precise SPIR-V, C5 audit and HLSL/MSL emission. Native FXC compilation is hosted-only");
    }

    static void AssertInstructions(byte[] spirv, bool rounding)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);
        var decorated = new HashSet<uint>();
        int divisions = 0, roots = 0;
        for (int offset = 5; offset < words.Length;)
        {
            int length = (int)(words[offset] >> 16);
            uint op = words[offset] & 65535u;
            Assert.True(length > 0 && offset + length <= words.Length);
            if (op is 21 or 22) Assert.Equal(32u, words[offset + 2]); // OpTypeInt/OpTypeFloat
            if (op == 71 && words[offset + 2] == 42) decorated.Add(words[offset + 1]);
            if (op == 136) { Assert.Contains(words[offset + 2], decorated); divisions++; } // OpFDiv
            if (op == 12 && words[offset + 4] == 31) roots++; // GLSL.std.450 Sqrt
            offset += length;
        }
        Assert.Equal(rounding ? 1 : 0, divisions);
        Assert.Equal(rounding ? 1 : 0, roots);
    }
}
