using System;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryShaderTests
{
    [Fact]
    public void Refinement_adapter_accepts_Windows_checkout_line_endings()
    {
        string source = CoverageGpuAttributesGlsl.Source;
        Assert.Equal(CoverageCarryRefinementShader.Adapt(source.ReplaceLineEndings("\n")),
            CoverageCarryRefinementShader.Adapt(source.ReplaceLineEndings("\r\n")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Native_float_path_has_all_four_carry_stages()
    {
        Assert.Equal(new[] { "CoverageCarryNativeSkin", "CoverageCarryNativeTransport", "CoverageCarryNativeBasis", "CoverageCarryNativePublish" },
            CoverageCarryShaders.Kernels().Where(k => k.Name.StartsWith("CoverageCarryNative", StringComparison.Ordinal)).Select(k => k.Name));
    }

    [Fact]
    public void Carry_kernels_compile_with_their_actual_options_including_Fxc_on_Windows()
    {
        foreach (var kernel in CoverageCarryShaders.Kernels())
        {
            var options = CoverageCarryShaders.Options(kernel.Name);
            ShaderValidation.ValidateCompute(kernel.ComputeGlsl, options, kernel.Name);
            if (KhaozEngineD3D11.IsPlatformSupported)
                D3D11ShaderValidation.ValidateCompute(kernel.ComputeGlsl, options, kernel.Name);
            if (!kernel.Name.StartsWith("CoverageCarryNative", StringComparison.Ordinal)) continue;
            Assert.False(options.PreciseMath);
            Assert.DoesNotContain("covExact", kernel.ComputeGlsl);
            Assert.DoesNotContain("CovExact", kernel.ComputeGlsl);
            Assert.DoesNotContain("covCorrectPositive", kernel.ComputeGlsl);
            byte[] module = SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, kernel.Name, options);
            uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
            for (int at = 5; at < words.Length; at += (int)(words[at] >> 16))
                Assert.False((words[at] & 65535) == 71 && words[at + 2] == 42, "Native float must not carry NoContraction.");
        }
    }
}
