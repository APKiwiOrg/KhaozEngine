using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The composed non-water sources, deliberately rebaked for approved shader changes.</summary>
public sealed class NonWaterShaderSourceEqualityTests
{
    [Fact]
    public void EveryNonWaterStageMatchesPinnedSourceText()
    {
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ShippedGraphicsProgram program in ShippedShaderPrograms.GraphicsPrograms())
        {
            if (program.Name is "Water" or "WaterClipmap" or "BoundedWater" or "WaterTriangle" or "WaterClipmapTriangle") continue;
            actual.Add(program.Name + ".vertex", Sha256(program.VertexGlsl));
            actual.Add(program.Name + ".fragment", Sha256(program.FragmentGlsl));
        }
        foreach (ShippedComputeKernel kernel in ShippedShaderPrograms.ComputeKernels())
            actual.Add(kernel.Name + ".compute", Sha256(kernel.ComputeGlsl));

        if (Environment.GetEnvironmentVariable("KE_UPDATE_GLSL_HASHES") == "1")
        {
            File.WriteAllLines(TablePath(), new[] { "# Composed non-water GLSL. Rebuild only for deliberate shader edits." }
                .Concat(actual.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => row.Key + " " + row.Value)));
            return;
        }

        Dictionary<string, string> expected = File.ReadAllLines(TablePath())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        Assert.Equal(278, expected.Count);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));

        string[] mismatches = actual.OrderBy(row => row.Key, StringComparer.Ordinal)
            .Where(row => expected[row.Key] != row.Value)
            .Select(row => $"{row.Key}: expected {expected[row.Key]}, composed {row.Value}")
            .ToArray();
        Assert.True(mismatches.Length == 0,
            "Non-water GLSL differs from its pinned source. Re-bake intentional edits with KE_UPDATE_GLSL_HASHES=1.\n"
            + string.Join("\n", mismatches));
    }

    // Normalize checkout line endings only. All shader whitespace, identifiers and ordering remain pinned.
    static string Sha256(string source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ReplaceLineEndings("\n"))));

    static string TablePath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "glsl-hashes", "non-water-glsl.sha256.txt");
}
