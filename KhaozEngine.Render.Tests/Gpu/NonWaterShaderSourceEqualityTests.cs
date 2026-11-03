using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Frames without opted-in water retain the shader source text from main at 0af218d5b, apart from the private
/// probe's six shader gates. The table stays that capture, so with the gate text taken out this proves everything else
/// in every stage is the released source. <c>ShaderGateProbeTests</c> pins the gate text itself.</summary>
public sealed class NonWaterShaderSourceEqualityTests
{
    [Fact]
    public void EveryNonWaterStageMatchesMainSourceText()
    {
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
        {
            if (program.Name is "Water" or "WaterClipmap" or "WaterTriangle" or "WaterClipmapTriangle") continue;
            actual.Add(program.Name + ".vertex", Sha256(program.VertexGlsl));
            actual.Add(program.Name + ".fragment", Sha256(program.FragmentGlsl));
        }
        foreach (ShippedComputeKernel kernel in ShippedShaderPrograms.ComputeKernels())
            actual.Add(kernel.Name + ".compute", Sha256(kernel.ComputeGlsl));

        Dictionary<string, string> expected = File.ReadAllLines(TablePath())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        Assert.Equal(248, expected.Count);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));

        string[] mismatches = actual.OrderBy(row => row.Key, StringComparer.Ordinal)
            .Where(row => expected[row.Key] != row.Value)
            .Select(row => $"{row.Key}: expected {expected[row.Key]}, composed {row.Value}")
            .ToArray();
        Assert.True(mismatches.Length == 0,
            "Non-water GLSL must stay byte-identical to main at 0af218d5b. "
            + "Share source expressions without changing lit composition.\n" + string.Join("\n", mismatches));
    }

    // Normalize checkout line endings and undo the probe's exact gate text only. All other shader whitespace, identifiers
    // and ordering remain pinned.
    static string Sha256(string source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(WithoutProbeGates(CandidateGateSource.WithoutGates(source.ReplaceLineEndings("\n"))))));

    static string WithoutProbeGates(string source) => source
        .Replace("        // IndirectParams.y is the private probe's switch: a banded ndl of exactly zero multiplies this term away.\n", "", StringComparison.Ordinal)
        .Replace(" && (IndirectParams.y < 0.5 || ndl > 0.0))", ")", StringComparison.Ordinal)
        .Replace("            // IndirectParams.z is the private probe's switch. Later slices only multiply a positive zero by 0 to 1.\n"
            + "            if (IndirectParams.z > 0.5 && floatBitsToUint(visibility) == 0u) break;\n", "", StringComparison.Ordinal)
        .Replace("(MotionParams.y > 0.5 && slot < 0 ? world : prevModel * vec4(Position, 1.0))", "(prevModel * vec4(Position, 1.0))", StringComparison.Ordinal);

    static string TablePath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "glsl-hashes", "non-water-glsl.sha256.txt");
}
