using System;
using KhaozEngine.Windowing;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class FrameDiagnosticsOptionsTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData("0", false, false)]
    [InlineData("0", true, false)]
    [InlineData("1", false, true)]
    [InlineData("1", true, true)]
    public void Override_preserves_the_public_mode_when_missing_and_accepts_only_exact_digits(
        string? value, bool current, bool expected)
    {
        Assert.Equal(expected, FrameDiagnosticsOptions.ResolveRendererInitializesSwapchain(value, current));
    }

    [Theory]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData(" 0")]
    [InlineData("1 ")]
    [InlineData("01")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("1\n")]
    public void Invalid_supplied_override_fails_clearly_instead_of_silently_preserving_a_mode(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            FrameDiagnosticsOptions.ResolveRendererInitializesSwapchain(value, false));
        Assert.Contains("KE_FRAME_DIAGNOSTICS_RENDERER_INITIALIZES_SWAPCHAIN", error.Message);
        Assert.Contains("'0' or '1'", error.Message);
    }
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void Native_pool_startup_reads_once_and_freezes_the_mode(string? value, bool expected)
    {
        int reads = 0;
        bool mode = FrameDiagnosticsOptions.ReadNativePoolBatching(name =>
        {
            Assert.Equal("KE_FRAME_DIAGNOSTICS_NATIVE_POOL_BATCHING", name);
            reads++;
            return value;
        }, GpuBackendKind.MetalNative, true);
        value = expected ? "0" : "1";
        Assert.Equal(expected, mode);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData(" 0")]
    [InlineData("1 ")]
    [InlineData("01")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("1\n")]
    public void Invalid_native_pool_value_fails_before_recording(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            FrameDiagnosticsOptions.ReadNativePoolBatching(_ => value, GpuBackendKind.MetalNative, true));
        Assert.Contains("KE_FRAME_DIAGNOSTICS_NATIVE_POOL_BATCHING", error.Message);
    }

    [Theory]
    [InlineData(GpuBackendKind.MetalNative, false)]
    [InlineData(GpuBackendKind.VulkanNative, true)]
    [InlineData(GpuBackendKind.Direct3D11Native, true)]
    public void Enabled_probe_on_unsupported_device_fails_instead_of_recording_a_false_mode(GpuBackendKind backend, bool mac)
    {
        Assert.Throws<InvalidOperationException>(() => FrameDiagnosticsOptions.ReadNativePoolBatching(_ => "1", backend, mac));
        Assert.False(FrameDiagnosticsOptions.ReadNativePoolBatching(_ => "0", backend, mac));
    }
}
