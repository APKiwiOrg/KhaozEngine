using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageShaderSizeTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Kernels() => ShaderCorpus.CoverageKernels().Select(k => new object[] { k.Name });

    [Fact]
    public void Every_physical_coverage_pipeline_has_an_exhaustive_measurement()
    {
        Assert.Equal(ShaderCorpus.CoverageKernels().Select(k => k.Name).Order(), CoverageShaderSizeManifest.Read().Keys.Order());
    }

    [Fact]
    public void Changed_module_cannot_reuse_an_approved_exhaustive_count()
    {
        string name = CoverageGpuResidualProbe.Name;
        string changed = CoverageGpuResidualProbe.Source.Replace("CovResidual a = CovResidual(Inputs[s],",
            "CovResidual a = CovResidual(Inputs[s] ^ 1u,", StringComparison.Ordinal);
        byte[] spirv = SpirvFrontEnd.ToSpirv(changed, GpuShaderStages.Compute, name, new GpuShaderCompileOptions(PreciseMath: true));
        string[] approved = CoverageShaderSizeManifest.Read()[name];
        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => CoverageShaderSizeManifest.InlineInstructions(spirv, name,
            int.Parse(approved[3], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(approved[4], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Contains("fresh exhaustive-inline measurement", error.Message);
    }

    [Theory]
    [MemberData(nameof(Kernels))]
    public void Every_coverage_pipeline_has_bounded_inlining_and_rolled_worker_sites(string name)
    {
        var precise = new GpuShaderCompileOptions(PreciseMath: true);
        ShippedComputeKernel kernel = ShaderCorpus.CoverageKernels().Single(k => k.Name == name);
        byte[] spirv = SpirvFrontEnd.ToSpirv(kernel.ComputeGlsl, GpuShaderStages.Compute, name, precise);
        string hlsl = SpirvCrossCompile.ComputeToHlsl(spirv, name, precise).ComputeSource;
        string msl = SpirvCrossCompile.ComputeToMsl(spirv, name, precise).ComputeSource;
        string? directory = Environment.GetEnvironmentVariable("KE_COVERAGE_SIZE_DUMP");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path + ".spv", spirv);
            File.WriteAllText(path + ".glsl", kernel.ComputeGlsl);
            File.WriteAllText(path + ".hlsl", hlsl.ReplaceLineEndings("\n"));
            File.WriteAllText(path + ".msl", msl.ReplaceLineEndings("\n"));
        }
        var sizes = CoverageShaderSizeAudit.Validate(spirv, hlsl, msl, name);
        output.WriteLine($"{name}: SPIR-V {sizes.Instructions}, exhaustive inline {sizes.InlineInstructions}, inline bound {sizes.InlineBound}, HLSL {sizes.HlslBytes}, MSL {sizes.MslBytes}");
    }
}
