using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Water composition retains main's source at 0af218d5b after undoing the accepted still-detail changes.</summary>
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
            "Non-water GLSL must stay byte-identical to main at 0af218d5b after the still-detail undo. "
            + "Share source expressions without changing lit composition.\n" + string.Join("\n", mismatches));
    }

    // The water baseline predates still-detail stabilization. Keep every frozen hash and undo only that
    // accepted feature through its checked delta, restoring compact visibility after the shared undo.
    // The Shimmer debug view lives outside the shared accumulator and has four additional exact edits.
    static string SourceBeforeStillDetail(string source)
    {
        source = source.ReplaceLineEndings("\n");
        bool fullyLit = source.Contains("bool visibilityReleased = false;", StringComparison.Ordinal);
        source = TemporalVisibilityFrozenDLaterChanges.Restore(TemporalVisibilityFullyLitGpuTests.ForceCheck(source));
        if (fullyLit) source = TemporalVisibilityPrograms.WithoutDecision(source);

        const string shimmerModes = "// Mode.x is the SceneDebugView value: 2 History, 3 Disocclusion, 4 Reactive, 5 Shimmer.\n";
        if (!source.Contains(shimmerModes, StringComparison.Ordinal)) return source;
        return source.Replace(shimmerModes,
                "// Mode.x is the SceneDebugView value: 2 History, 3 Disocclusion, 4 Reactive.\n", StringComparison.Ordinal)
            .Replace("    vec3 c = context;   // a mode no view sets shows the dimmed scene alone\n",
                "    vec3 c;\n", StringComparison.Ordinal)
            .Replace("    } else if (mode == 4) {\n", "    } else {\n", StringComparison.Ordinal)
            .Replace("    } else if (mode == 5) {\n"
                + "        c = mix(context, vec3(0.15, 0.55, 1.0), clamp(t.shimmer, 0.0, 1.0));\n",
                string.Empty, StringComparison.Ordinal);
    }

    static string Sha256(string source)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SourceBeforeStillDetail(source))));

    static string TablePath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "glsl-hashes", "non-water-glsl.sha256.txt");
}
