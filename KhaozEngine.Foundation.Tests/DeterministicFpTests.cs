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
        // SetCanonical must actually install the canonical (round-to-nearest) FP state, and Restore must put
        // the prior state back - not merely avoid throwing (issue #185: the original body called the two
        // methods with zero Assert calls, so an unhandled exception was the only possible failure mode). The
        // corrupted starting rounding mode is a deliberately hostile precondition, the same trick
        // DeterministicFpHarnessTests.cs uses, restored in a finally so a mid-test failure can't leak corrupted
        // rounding into a reused xUnit worker thread.
        try
        {
            FpPoke.SetRoundTowardZero();
            int priorRounding = FpPoke.GetRound();
            Assert.Equal(FpPoke.RoundTowardZero, priorRounding);

            var token = DeterministicFp.SetCanonical();
            Assert.Equal(FpPoke.RoundToNearest, FpPoke.GetRound());   // canonical state actually applied

            DeterministicFp.Restore(token);
            Assert.Equal(priorRounding, FpPoke.GetRound());          // prior state actually restored
        }
        finally
        {
            FpPoke.SetRoundToNearest();
        }
    }
}
