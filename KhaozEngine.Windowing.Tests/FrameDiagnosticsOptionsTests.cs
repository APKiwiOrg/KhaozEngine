using System;
using KhaozEngine.Windowing;
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
}
