using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Vulkan's withdrawn gather variant restores the ten complete pre-gather modules byte for byte, including
/// fused, split, display-size split, debug and count programs. These independent baseline hashes came from the
/// committed Vulkan table at cb2a97e9e. They are never rebaked by a writer.</summary>
public sealed class TemporalVisibilityBaselineSpirvTests
{
    public static IEnumerable<object[]> BaselineModules()
    {
        foreach (string line in File.ReadLines(BaselinePath()))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            yield return [fields[0].Replace(".fragment", "", StringComparison.Ordinal), fields[1]];
        }
    }

    [Theory]
    [MemberData(nameof(BaselineModules))]
    public void SelectedVulkanModuleIsThePreGatherModule(string program, string sha256)
    {
        ShippedGraphicsProgram source = ShippedShaderPrograms.GraphicsPrograms().Single(p => p.Name == program);
        string selected = TemporalVisibilityGatherPolicy.ForBackend(source.FragmentGlsl, GpuBackendKind.VulkanNative);
        byte[] spirv = SpirvFrontEnd.ToSpirv(selected, GpuShaderStages.Fragment, program);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(spirv)));
    }

    static string BaselinePath([CallerFilePath] string file = "") => Path.Combine(Path.GetDirectoryName(file)!,
        "spirv-hashes", "taa-visibility-loads-baseline.sha256.txt");
}
