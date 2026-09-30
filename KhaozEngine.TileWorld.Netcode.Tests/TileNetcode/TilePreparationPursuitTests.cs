using System.Collections.Generic;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationPursuitTests
{
    readonly ITestOutputHelper output;

    public TilePreparationPursuitTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData(3, 1, 14)]
    [InlineData(3, 1, 16)]
    [InlineData(4, 2, 14)]
    [InlineData(4, 2, 16)]
    public void Walking_pursuit_keeps_the_legacy_hit_rate_in_every_phase(byte lead, byte strike, byte cadence) =>
        AssertPursuit(TileMoveMode.Walk, lead, strike, cadence);

    [Theory]
    [InlineData(3, 1, 14)]
    [InlineData(3, 1, 16)]
    [InlineData(4, 2, 14)]
    [InlineData(4, 2, 16)]
    public void Running_pursuit_keeps_the_legacy_hit_rate_in_every_phase(byte lead, byte strike, byte cadence) =>
        AssertPursuit(TileMoveMode.Run, lead, strike, cadence);

    void AssertPursuit(TileMoveMode gait, byte lead, byte strike, byte cadence)
    {
        const int Cadences = 5;
        var rows = new List<(int Phase, PursuitWindow Before, PursuitWindow After)>();
        for (int phase = 0; phase < 4; phase++)
            rows.Add((phase, PursuitScenario.Run(false, gait, cadence, lead, strike, phase),
                PursuitScenario.Run(true, gait, cadence, lead, strike, phase)));
        // One line per phase and build: landed, IllegalReach ends, ends after first landed, first resolved tick,
        // steady count, and the resolved ticks relative to W0.
        string table = $"{gait} lead {lead} strike {strike} cadence {cadence}\n"
            + PursuitScenario.Table(rows, cadence, Cadences);
        output.WriteLine(table);
        foreach (var (phase, before, after) in rows)
        {
            Assert.True(before.TargetNeverIdle && after.TargetNeverIdle && before.StayedInOneRegion
                && after.StayedInOneRegion, table);
            Assert.True(after.EveryResolutionWithinBound, table);
            Assert.True(after.IllegalReachEnds <= 1, table);        // only a run start from a stand may end once
            Assert.True(after.EndsAfterFirstLanded == 0, table);
            for (int i = 1; i < after.ResolvedTicks.Length; i++)
            {
                long gap = after.ResolvedTicks[i] - after.ResolvedTicks[i - 1];
                Assert.True(gap >= cadence && gap <= cadence + strike, table);
            }
            Assert.True(after.ResolvedTicks.Length > 0
                && after.ResolvedTicks[0] + Cadences * cadence <= after.WindowEnd, table);
            Assert.True(after.SteadyCount(Cadences, cadence) >= before.SteadyCount(Cadences, cadence), table);
        }
    }
}
