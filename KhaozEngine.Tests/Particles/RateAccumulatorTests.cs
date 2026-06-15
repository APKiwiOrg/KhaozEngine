using KhaozEngine.Particles;
using Xunit;

namespace KhaozEngine.Tests.Particles;

public class RateAccumulatorTests
{
    [Fact]
    public void TenPerSecond_OverOneSecond_EmitsTen()
    {
        var acc = new RateAccumulator();
        int total = 0;

        // 1.0s in 0.1s steps at 10/sec => exactly 10.
        for (int i = 0; i < 10; i++)
        {
            total += acc.Advance(0.1f, 10f);
        }

        Assert.Equal(10, total);
    }

    [Fact]
    public void FractionalCarry_AccumulatesAcrossFrames()
    {
        var acc = new RateAccumulator();

        // 10/sec at 0.05s => 0.5 per frame: 0, 1, 0, 1, ...
        Assert.Equal(0, acc.Advance(0.05f, 10f)); // carry 0.5
        Assert.Equal(1, acc.Advance(0.05f, 10f)); // carry 1.0 -> emit 1
        Assert.Equal(0, acc.Advance(0.05f, 10f)); // carry 0.5
        Assert.Equal(1, acc.Advance(0.05f, 10f)); // carry 1.0 -> emit 1
    }

    [Fact]
    public void LongRunAverage_MatchesRate()
    {
        var acc = new RateAccumulator();
        int total = 0;

        // 7/sec for 3 seconds at an awkward 1/60 step => 21 (carry-correct).
        const float dt = 1f / 60f;
        int steps = 180; // 3 seconds
        for (int i = 0; i < steps; i++)
        {
            total += acc.Advance(dt, 7f);
        }

        Assert.Equal(21, total);
    }

    [Fact]
    public void ZeroRateOrDt_EmitsNothing()
    {
        var acc = new RateAccumulator();
        Assert.Equal(0, acc.Advance(0.1f, 0f));
        Assert.Equal(0, acc.Advance(0f, 10f));
    }

    [Fact]
    public void Reset_ClearsCarry()
    {
        var acc = new RateAccumulator();
        acc.Advance(0.05f, 10f); // carry 0.5
        acc.Reset();
        Assert.Equal(0, acc.Advance(0.05f, 10f)); // back to 0.5, not 1.0
    }
}
