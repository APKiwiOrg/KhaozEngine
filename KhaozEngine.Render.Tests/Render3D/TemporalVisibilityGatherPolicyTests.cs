using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityGatherPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetalSelectsOnlyCurrentLoadsAtProgramBuildOnEitherCheckout(bool crlf)
    {
        static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
        string source = Lf(ShaderSources.TemporalAccumulateFrag);
        if (crlf) source = source.Replace("\n", "\r\n", StringComparison.Ordinal);
        string selected = TemporalVisibilityGatherPolicy.ForBackend(source, GpuBackendKind.MetalNative);
        string expected = Lf(source).Replace(TemporalVisibilityFastPath.CurrentSources(true),
            TemporalVisibilityFastPath.CurrentSources(false), StringComparison.Ordinal);
        Assert.Equal(expected, Lf(selected));
        Assert.NotEqual(source, selected);
        Assert.Same(selected, TemporalVisibilityGatherPolicy.ForBackend(selected, GpuBackendKind.MetalNative));
    }

    [Theory]
    [InlineData(GpuBackendKind.Direct3D11Native)]
    [InlineData(GpuBackendKind.VulkanNative)]
    public void OtherBackendsRetainTheGatheredProgram(GpuBackendKind backend)
        => Assert.Same(ShaderSources.TemporalAccumulateFrag,
            TemporalVisibilityGatherPolicy.ForBackend(ShaderSources.TemporalAccumulateFrag, backend));

    [Fact]
    public void FrozenDAndTheFirstPassHaveNoSubstitution()
    {
        string d = TemporalVisibilityFrozenD.ToShippedD(ShaderSources.TemporalResolveFrag);
        Assert.Same(d, TemporalVisibilityGatherPolicy.ForBackend(d, GpuBackendKind.MetalNative));
        Assert.Same(ShaderSources.TemporalPrepareFrag,
            TemporalVisibilityGatherPolicy.ForBackend(ShaderSources.TemporalPrepareFrag, GpuBackendKind.MetalNative));
    }
}
