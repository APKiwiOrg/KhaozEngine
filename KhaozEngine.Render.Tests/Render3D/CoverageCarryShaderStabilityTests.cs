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
    // A2 decouples native local direction recovery from exact position publication. Position vectors remain frozen.
    [Theory]
    [InlineData("CoverageCarrySkin", "0a03daf886e70e3acec36af944ce56088df5e810a966416babb37e13fb98b2c6")]
    [InlineData("CoverageCarryBasis", "b7aa298dd67fca2e09ded90ba0e5708ed2f702cf5b01be9ba57f9da69d8ca9d8")]
    public void Carry_modules_match_pinned_compilation_identities(string name, string expected)
    {
        var kernel = CoverageCarryShaders.Kernels.Single(k => k.Name == name);
        byte[] module = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options);
        string actual = Convert.ToHexStringLower(SHA256.HashData(module));
        output.WriteLine($"{name} SPIR-V SHA256={actual}");
        Assert.Equal(expected, actual);
    }
}
