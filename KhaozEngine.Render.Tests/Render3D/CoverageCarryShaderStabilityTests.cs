using System;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryShaderStabilityTests
{
    [Theory]
    [InlineData("CoverageCarrySkin", "9ac32b35015c2df2d9d9e34c3fbd6a15efcc3322c2026ef7781c0a0a71f4c2c0")]
    [InlineData("CoverageCarryBasis", "064aa97da725f0965101fc5a827eb5665ae586967678b6a11474f09bd21b4c88")]
    public void Shared_helpers_preserve_untouched_compiled_modules(string name, string expected)
    {
        var kernel = CoverageCarryShaders.Kernels.Single(k => k.Name == name);
        byte[] module = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options);
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(module)));
    }
}
