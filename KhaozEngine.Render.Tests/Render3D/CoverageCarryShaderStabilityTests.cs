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
    [InlineData("CoverageCarrySkin", "48fe9456b6a483c809161014cff853298b92590928a7b36ec3da0aca83366dc8")]
    [InlineData("CoverageCarryBasis", "fa77e838e9b883588f1036e50f7ee649d270ad0c867fde6501d0ff4d18580919")]
    public void Shared_helpers_preserve_untouched_compiled_modules(string name, string expected)
    {
        var kernel = CoverageCarryShaders.Kernels.Single(k => k.Name == name);
        byte[] module = SpirvFrontEnd.ToSpirv(kernel.Source, GpuShaderStages.Compute, kernel.Name, CoverageCarryShaders.Options);
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(module)));
    }
}
