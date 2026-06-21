using KhaozEngine.Determinism;
using Xunit;

namespace KhaozEngine.Tests;

public class DeterministicFpTests
{
    [Fact]
    public void IsSupportedOnThisPlatform()
    {
        // The dev machine (arm64 macOS) and CI (x64 linux) must both have FP control wired up;
        // otherwise the scope silently no-ops and determinism is not actually enforced.
        Assert.True(DeterministicFp.IsSupported);
    }

    [Fact]
    public void EnterAndDisposeRoundTrips()
    {
        // Entering applies canonical state; disposing restores. The round-trip must not throw.
        using (DeterministicFpScope.Enter())
        {
            float x = 1.0f / 3.0f;
            Assert.True(x > 0f);
        }
    }

    [Fact]
    public void SetCanonicalAndRestoreRoundTrips()
    {
        var prior = DeterministicFp.SetCanonical();
        DeterministicFp.Restore(prior);
    }
}
