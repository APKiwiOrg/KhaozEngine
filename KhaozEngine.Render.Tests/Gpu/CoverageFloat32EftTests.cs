using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageFloat32EftTests
{
    [Theory]
    [InlineData(false, false, false, false, 0)]
    [InlineData(true, false, true, true, 0)]
    [InlineData(false, true, true, true, 0)]
    [InlineData(true, true, false, false, 0)]
    [InlineData(true, true, true, false, 1)]
    [InlineData(true, true, false, true, 2)]
    [InlineData(true, true, true, true, 1)]
    public void Only_verified_vulkan_controls_enable_a_filter(bool exceptional, bool rte, bool preserve, bool flush, int path)
    {
        var capabilities = new GpuCapabilities
        {
            SupportsShaderFloatControls = exceptional,
            SupportsFloat32Rte = rte,
            SupportsFloat32DenormPreserve = preserve,
            SupportsFloat32DenormFlush = flush
        };
        Assert.Equal(path, (int)CoverageGpuFilterPolicy.Resolve(GpuBackendKind.VulkanNative, capabilities));
        Assert.Equal(CoverageGpuFilterPath.Exact, CoverageGpuFilterPolicy.Resolve(GpuBackendKind.MetalNative, capabilities));
        Assert.Equal(CoverageGpuFilterPath.Exact, CoverageGpuFilterPolicy.Resolve(GpuBackendKind.Direct3D11Native, capabilities));
        foreach (bool ftz in new[] { false, true })
        {
            var options = new GpuShaderCompileOptions(PreciseMath: true, RequireFloat32Eft: true, FlushFloat32Denorms: ftz);
            if (exceptional && rte && (ftz ? flush : preserve)) VulkanFloat32Eft.Require(capabilities, options);
            else Assert.Throws<ShaderValidationException>(() => VulkanFloat32Eft.Require(capabilities, options));
        }
    }

    [Fact]
    public void Eft_modules_request_rte_and_preserve_denormals_with_a_distinct_cache_identity()
    {
        var precise = new GpuShaderCompileOptions(PreciseMath: true);
        var eft = new GpuShaderCompileOptions(PreciseMath: true, RequireFloat32Eft: true);
        byte[] ordinary = SpirvFrontEnd.ToSpirv(PreciseMathShaders.Compute, GpuShaderStages.Compute, options: precise);
        byte[] filtered = SpirvFrontEnd.ToSpirv(PreciseMathShaders.Compute, GpuShaderStages.Compute, options: eft);
        Assert.DoesNotContain(4462u, Modes(ordinary));
        Assert.Contains(4462u, Modes(filtered));
        Assert.Contains(4459u, Modes(filtered));
        Assert.Contains(4461u, Modes(filtered));
        Assert.NotEqual(SpirvFrontEnd.OptionsIdentity("eft", precise), SpirvFrontEnd.OptionsIdentity("eft", eft));
        Assert.NotEqual(ordinary, filtered);
    }

    [Fact]
    public void Eft_without_precise_math_is_refused_before_compilation()
    {
        Assert.Throws<ShaderValidationException>(() => SpirvFrontEnd.ToSpirv(PreciseMathShaders.Compute,
            GpuShaderStages.Compute, options: new(RequireFloat32Eft: true)));
        Assert.Throws<ShaderValidationException>(() => SpirvFrontEnd.ToSpirv(PreciseMathShaders.Vertex,
            GpuShaderStages.Vertex, options: new(PreciseMath: true, RequireFloat32Eft: true)));
        Assert.Throws<ShaderValidationException>(() => SpirvFrontEnd.ToSpirv(PreciseMathShaders.Compute,
            GpuShaderStages.Compute, options: new(PreciseMath: true, FlushFloat32Denorms: true)));
    }

    [Fact]
    public void Vulkan_shader_and_pipeline_creation_both_refuse_unavailable_eft_controls()
    {
        var fixture = new VulkanResourceFixture();
        var options = new GpuShaderCompileOptions(PreciseMath: true, RequireFloat32Eft: true);
        Assert.Throws<ShaderValidationException>(() => fixture.Factory.CreateComputeShaderFromSpirv(PreciseMathShaders.Compute, options));
        using var shader = new VulkanComputeShader(fixture.Modules, PreciseMathShaders.Compute, options: options);
        Assert.Throws<ShaderValidationException>(() => fixture.Factory.CreateComputePipeline(new(shader)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Every_filter_add_subtract_and_multiply_is_noncontracting(int stage)
    {
        var selected = (CoverageGpuFilteredStage)stage;
        byte[] module = SpirvFrontEnd.ToSpirv(CoverageGpuFilteredSources.Source(selected), GpuShaderStages.Compute,
            options: CoverageGpuFilteredSources.Options(selected, CoverageGpuFilterPath.FilteredPreserve));
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(module);
        var operations = new HashSet<uint>(); var decorations = new HashSet<uint>();
        for (int i = 5; i < words.Length; i += (int)(words[i] >> 16))
        {
            uint opcode = words[i] & 65535u;
            if (opcode is 129u or 131u or 133u) operations.Add(words[i + 2]);
            if (opcode == 71u && words[i + 2] == 42u) decorations.Add(words[i + 1]);
            Assert.False(opcode == 71u && words[i + 2] == 0u, "RelaxedPrecision must never qualify a filter operation");
        }
        Assert.NotEmpty(operations);
        Assert.Subset(decorations, operations);
    }

    static HashSet<uint> Modes(byte[] module)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(module);
        var modes = new HashSet<uint>();
        for (int i = 5; i < words.Length; i += (int)(words[i] >> 16))
            if ((words[i] & 65535u) == 16u && (words[i] >> 16) == 4u && words[i + 3] == 32u)
                modes.Add(words[i + 2]);
        return modes;
    }
}
