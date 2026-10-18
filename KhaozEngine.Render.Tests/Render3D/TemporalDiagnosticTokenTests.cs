using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("NativeDeviceLifecycle")]
public sealed class TemporalDiagnosticTokenTests
{
    [Theory]
    [InlineData("vis-release-0.1", 16777216)]
    [InlineData("vis-release-0.2", 33554432)]
    [InlineData("vis-release-0.3", 67108864)]
    [InlineData("vis-reset-0.2", 134217728)]
    [InlineData("show-vis-release", 268435456)]
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

    [Fact]
    public void VisibilityFlagsDoNotRoundAwayLowDiagnosticBits()
    {
        int saved = TemporalResolveDiagnostics.Mask;
        try
        {
            int mask = TemporalResolveDiagnostics.Parse("vis-reset-0.2,show-vis-release,show-released,shadow-bilinear-3x3");
            TemporalResolveDiagnostics.OverrideMaskForMeasurement(mask);
            var view = new TemporalViewInput(Matrix4x4.Identity, Matrix4x4.Identity);
            var uniforms = TemporalResolveMath.BuildUniforms(view, null, Vector2.Zero, 16, 16, 16, 16, false);
            Assert.Equal(24f, uniforms.Params.X);
            Assert.Equal(4194305f, uniforms.Params.W);
        }
        finally { TemporalResolveDiagnostics.OverrideMaskForMeasurement(saved); }
    }

}
