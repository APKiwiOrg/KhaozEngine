using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Main's non-water source baseline plus the native prepared-foliage stages.</summary>
public sealed class NonWaterShaderSourceEqualityTests
{
    [Fact]
    public void EveryNonWaterStageMatchesMainSourceText()
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

        Dictionary<string, string> expected = File.ReadAllLines(TablePath())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        var missing = actual.Where(row => !expected.ContainsKey(row.Key)).OrderBy(row => row.Key, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0, "Unpinned non-water stages:\n" +
            string.Join("\n", missing.Select(row => $"{row.Key} {row.Value}")));
        // Main's 248 stages plus two stages for each of the seven prepared foliage programs.
        Assert.Equal(248 + 2 * 7, expected.Count);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));

        string[] mismatches = actual.OrderBy(row => row.Key, StringComparer.Ordinal)
            .Where(row => expected[row.Key] != row.Value)
            .Select(row => $"{row.Key}: expected {expected[row.Key]}, composed {row.Value}")
            .ToArray();
        Assert.True(mismatches.Length == 0,
            "Non-water GLSL must match the main baseline and native prepared-foliage extensions. "
            + "Share source expressions without changing lit composition.\n" + string.Join("\n", mismatches));
    }

    // Normalize checkout line endings only. All shader whitespace, identifiers and ordering remain pinned.
    static string Sha256(string source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ReplaceLineEndings("\n"))));

    static string TablePath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "glsl-hashes", "non-water-glsl.sha256.txt");
}
