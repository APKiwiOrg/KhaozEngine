using System;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The elapsed-time replication cadence: one allowance at most per host call, skipped boundaries counted in
/// <see cref="ReplicationCadence.Tick"/> for deadlines, fractional time carried, and zero-time drains free. Step sizes
/// are binary fractions so every expectation is exact.
/// </summary>
public class ReplicationCadenceTests
{
    [Fact]
    public void ShortFramesAccumulateWithoutExtraAllowances()
    {
        var cadence = new ReplicationCadence(0.25f);
        int allowances = 0;
        for (int call = 1; call <= 16; call++)
        {
            bool allowed = cadence.Advance(0.0625f);
            Assert.Equal(call % 4 == 0, allowed);
            if (allowed) allowances++;
        }

        Assert.Equal(4, allowances);
        Assert.Equal(4, cadence.Tick);
    }

    [Fact]
    public void FractionalTimeCarriesAcrossBoundaries()
    {
        var cadence = new ReplicationCadence(0.25f);

        Assert.False(cadence.Advance(0.1875f));
        Assert.True(cadence.Advance(0.1875f));
        Assert.True(cadence.Advance(0.1875f));
        Assert.True(cadence.Advance(0.1875f));
        Assert.False(cadence.Advance(0.1875f));
        Assert.Equal(3, cadence.Tick);
    }

    [Fact]
    public void SixtyHertzFramesGiveThirtyHertzAllowances()
    {
        var cadence = new ReplicationCadence(1f / 30f);
        int allowances = 0;
        for (int frame = 0; frame < 60; frame++)
            if (cadence.Advance(1f / 60f)) allowances++;

        Assert.Equal(30, allowances);
        Assert.Equal(30, cadence.Tick);
    }

    [Fact]
    public void LongFrameSkipsBacklogButAdvancesDeadlines()
    {
        var cadence = new ReplicationCadence(0.25f);
        const long deadlineTick = 5;

        Assert.True(cadence.Advance(1.0f));
        Assert.Equal(4, cadence.Tick);
        Assert.True(cadence.Tick < deadlineTick);

        Assert.False(cadence.Advance(0.125f));
        Assert.Equal(4, cadence.Tick);

        Assert.True(cadence.Advance(0.625f));
        Assert.Equal(7, cadence.Tick);
        Assert.True(cadence.Tick >= deadlineTick);

        Assert.True(cadence.Advance(0.25f));
        Assert.Equal(8, cadence.Tick);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.125f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ZeroTimeDrainConsumesNoBudget(float drain)
    {
        var cadence = new ReplicationCadence(0.25f);

        Assert.False(cadence.Advance(0.125f));
        for (int i = 0; i < 5; i++) Assert.False(cadence.Advance(drain));
        Assert.Equal(0, cadence.Tick);
        Assert.True(cadence.Advance(0.125f));
        Assert.Equal(1, cadence.Tick);
        for (int i = 0; i < 5; i++) Assert.False(cadence.Advance(drain));
        Assert.Equal(1, cadence.Tick);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void CadenceNeedsAPositiveFiniteTick(float tickSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplicationCadence(tickSeconds));
    }
}
