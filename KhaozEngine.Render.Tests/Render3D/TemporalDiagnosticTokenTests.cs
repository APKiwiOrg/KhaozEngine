using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalDiagnosticTokenTests
{
    [Theory]
    [InlineData("lock-history-ridge", 1024)]
    [InlineData("lock-shade-release", 2048)]
    [InlineData("lock-bound-current", 4096)]
    public void CandidateTokensHaveDistinctBits(string token, int bit)
        => Assert.Equal(bit, TemporalResolveDiagnostics.Parse(token));

    [Fact]
    public void LockedOverlayTokenComposesWithoutChangingOtherBits()
    {
        Assert.Equal(512, TemporalResolveDiagnostics.Parse("show-locked"));
        Assert.Equal(768, TemporalResolveDiagnostics.Parse("noop, SHOW-LOCKED"));
        Assert.Equal(0, TemporalResolveDiagnostics.Parse(null));
    }
}
