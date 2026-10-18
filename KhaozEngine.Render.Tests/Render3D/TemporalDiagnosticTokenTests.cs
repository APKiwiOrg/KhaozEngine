using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalDiagnosticTokenTests
{
    [Theory]
    [InlineData("shadow-bilinear-3x3", 4194304)]
    [InlineData("shadow-bilinear-1", 8388608)]
    [InlineData("lock-history-ridge", 1024)]
    [InlineData("lock-shade-release", 2048)]
    [InlineData("lock-bound-current", 4096)]
    [InlineData("show-gate", 8192)]
    [InlineData("show-lock-age", 16384)]
    [InlineData("show-gate-extent", 32768)]
    [InlineData("lock-trust-2", 65536)]
    [InlineData("lock-trust-3", 131072)]
    [InlineData("lock-trust-4", 262144)]
    [InlineData("lock-luma-kill-0.1", 524288)]
    [InlineData("lock-luma-kill-0.2", 1048576)]
    [InlineData("lock-luma-kill-wide-0.1", 524288 | 2097152)]
    [InlineData("lock-luma-kill-wide-0.2", 1048576 | 2097152)]
    public void CandidateTokensHaveDistinctBits(string token, int bit)
        => Assert.Equal(bit, TemporalResolveDiagnostics.Parse(token));

    [Fact]
    public void LockedOverlayTokenComposesWithoutChangingOtherBits()
    {
        Assert.Equal(512, TemporalResolveDiagnostics.Parse("show-locked"));
        Assert.Equal(768, TemporalResolveDiagnostics.Parse("noop, SHOW-LOCKED"));
        Assert.Equal(0, TemporalResolveDiagnostics.Parse(null));
    }

    [Fact]
    public void TheWholeMaskStaysExactInTheFloatUniform()
    {
        int all = TemporalResolveDiagnostics.Parse("show-gate,show-lock-age,show-gate-extent,lock-trust-4,lock-luma-kill-wide-0.2");
        Assert.Equal(all, (int)((float)all + 0.5f));
        Assert.True(all < 1 << 24);
    }
    [Theory]
    [InlineData(0, "")]
    [InlineData(512, "")]
    [InlineData(4194304, "#define ShadowBilinear3x3\n")]
    [InlineData(8388608 | 512 | 1048576, "#define ShadowBilinear1\n")]
    public void LightingOnlyCompilesTheSelectedShadowFilter(int mask, string define)
    {
        const string program = "#version 450\nvoid main() {}";
        string result = TemporalResolveDiagnostics.ApplyShadowFilter(program, mask);
        Assert.Equal("#version 450\n" + define + "void main() {}", result);
        if (define.Length == 0) Assert.Same(program, result);
    }

    [Fact]
    public void IncompatibleShadowFiltersAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TemporalResolveDiagnostics.Parse("shadow-bilinear-3x3,shadow-bilinear-1"));
    }

}
