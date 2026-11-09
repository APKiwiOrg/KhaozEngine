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
    // Contract v2 fix restores exact decision intermediates. Only final output units are native. Position vectors remain frozen.
    [Theory]
    [InlineData("CoverageCarrySkin", "c3120e43151087a39cb9e0d8e063a921041190c1782b04c313d9556577a3a83e")]
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
