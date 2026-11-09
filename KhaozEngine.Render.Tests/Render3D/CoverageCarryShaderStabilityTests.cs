using System;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryShaderStabilityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("CoverageCarrySkin", "a6f645e0c0c6b8dd8b4ae5d2c703ed777a1decf643821216c2484b1763488da1")]
    [InlineData("CoverageCarryBasis", "064aa97da725f0965101fc5a827eb5665ae586967678b6a11474f09bd21b4c88")]
    public void Carry_modules_match_pinned_compilation_identities(string name, string expected)
    {
        var kernel = CoverageCarryShaders.Kernels.Single(k => k.Name == name);
        byte[] module = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options);
        string actual = Convert.ToHexStringLower(SHA256.HashData(module));
        output.WriteLine($"{name} SPIR-V SHA256={actual}");
        Assert.Equal(expected, actual);
    }
}
