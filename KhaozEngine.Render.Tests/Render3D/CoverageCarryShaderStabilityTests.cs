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
    // Contract v2 replaces emulated direction arithmetic with native operations. Position vectors remain frozen.
    [Theory]
    [InlineData("CoverageCarrySkin", "4144fd5b35b546f7ce18841b9f9a7f6408e7337020c1713c57559ff4325c42c0")]
    [InlineData("CoverageCarryBasis", "df127506e5a1d3ddd88a47d2a84860264daa6f2ec660d3bbe7e17f2b184d1ed0")]
    public void Carry_modules_match_pinned_compilation_identities(string name, string expected)
    {
        var kernel = CoverageCarryShaders.Kernels.Single(k => k.Name == name);
        byte[] module = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options);
        string actual = Convert.ToHexStringLower(SHA256.HashData(module));
        output.WriteLine($"{name} SPIR-V SHA256={actual}");
        Assert.Equal(expected, actual);
    }
}
