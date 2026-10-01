using System.Globalization;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileNetcode;

// Characterization of the existing pursuit geometry, with neither prediction nor remote interpolation involved.
// A legal committed-tile hit does not promise one metre between the bodies on the full-step glide.
public class TileCombatContactGeometryTests(ITestOutputHelper output)
{
    const float TickSeconds = 1f / 6f;
    const long TargetId = 42;
    static readonly TileStepTicks StepTicks = new(walk: 4, run: 2);
    static readonly TilePresenter Presenter = new(tileSize: 1f, planeHeight: 3f);

    [Theory]
    [InlineData(TileMoveMode.Walk, 4, 1.25f)]
    [InlineData(TileMoveMode.Run, 2, 1.5f)]
    public void Equal_time_pursuit_glides_exceed_one_metre_during_legal_committed_adjacency(
        TileMoveMode mode, int cadence, float expectedGap)
    {
        var (sim, attacker, target) = Trace(mode, new TileCoord(20, 40, 0), lastTick: cadence);

        // Both traces reach these same tiles. The pursuer has just committed its next step, one tick after the
        // target. Hand calculation: attacker Z = 21, target Z = 22 + 1/cadence, hence 1.25 m walking or 1.50 m running.
        Assert.Equal(new TileCoord(20, 21, 0), attacker.StepFrom);
        Assert.Equal(new TileCoord(20, 22, 0), attacker.Tile);
        Assert.Equal(0, attacker.StepTicks);
        Assert.Equal(new TileCoord(20, 22, 0), target.StepFrom);
        Assert.Equal(new TileCoord(20, 23, 0), target.Tile);
        Assert.Equal(1, target.StepTicks);
        Assert.Equal(cadence, attacker.StepTotal);
        Assert.Equal(cadence, target.StepTotal);
        Assert.True(InReach(sim, attacker, target));
        Assert.Equal(1f + (1f / cadence), expectedGap);

        // The shared fractional tick advances both continuous glides equally. The gap is not an artefact of
        // sampling only a tick boundary, and removing a remote interpolation delay cannot remove this term.
        foreach (float extraTicks in new[] { 0f, 0.5f })
        {
            float gap = Report("pursuit", mode, cadence, attacker, target, extraTicks);
            Assert.Equal(expectedGap, gap, precision: 5);
            Assert.True(gap > 1.08f, "the existing equal-time gap exceeds the consumer's 80 mm contact envelope");
        }
    }

    [Theory]
    [InlineData(TileMoveMode.Walk, 4)]
    [InlineData(TileMoveMode.Run, 2)]
    public void A_target_that_stops_and_a_pursuer_that_lands_show_one_metre(TileMoveMode mode, int cadence)
    {
        var (sim, attacker, target) = Trace(mode, new TileCoord(20, 23, 0), lastTick: 3 * cadence);

        Assert.Equal(new TileCoord(20, 22, 0), attacker.Tile);
        Assert.Equal(new TileCoord(20, 23, 0), target.Tile);
        Assert.False(attacker.IsStepping);
        Assert.False(target.IsStepping);
        Assert.True(InReach(sim, attacker, target));
        Assert.Equal(1f, Report("stopped", mode, 3 * cadence, attacker, target, extraTicks: 0f), precision: 5);
    }

    (TileMoveSimulator Sim, TileMoveState Attacker, TileMoveState Target) Trace(
        TileMoveMode mode, TileCoord goal, int lastTick)
    {
        var targets = new OneTarget();
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()),
            StepTicks, null, null, targets);
        TileMoveState attacker = TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.N);
        TileMoveState target = TileMoveState.At(new TileCoord(20, 21, 0), TileDirection.N);
        for (int tick = 0; tick <= lastTick; tick++)
        {
            // This is the server's tick-start target snapshot, as in TileCombatTargetTests' reachable dance trace.
            // Neither body's decision reads a tile the other body committed in this same tick.
            targets.Tile = target.Tile;
            attacker = sim.Step(attacker, tick == 0 ? TileCommand.Attack(TargetId, mode)
                : TileCommand.Continue(mode), TickSeconds);
            target = sim.Step(target, tick == 0 ? TileCommand.WalkTo(goal, mode)
                : TileCommand.Continue(mode), TickSeconds);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"tick={tick} mode={mode} legal={InReach(sim, attacker, target)} "
                + $"attacker={attacker.StepFrom}->{attacker.Tile} progress={attacker.StepTicks}/{attacker.StepTotal} "
                + $"target={target.StepFrom}->{target.Tile} progress={target.StepTicks}/{target.StepTotal}"));
        }
        return (sim, attacker, target);
    }

    float Report(string scenario, TileMoveMode mode, int tick, in TileMoveState attacker,
        in TileMoveState target, float extraTicks)
    {
        TilePose a = Presenter.Pose(attacker, extraTicks), b = Presenter.Pose(target, extraTicks);
        float gap = Vector3.Distance(a.Position, b.Position);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"scenario={scenario} mode={mode} tick={tick} extraTicks={extraTicks:F2} "
            + $"shownAttacker=({a.Position.X:F4},{a.Position.Y:F4},{a.Position.Z:F4}) "
            + $"shownTarget=({b.Position.X:F4},{b.Position.Y:F4},{b.Position.Z:F4}) gapMetres={gap:F6}"));
        return gap;
    }

    static bool InReach(TileMoveSimulator sim, in TileMoveState attacker, in TileMoveState target) =>
        TileReach.Contains(sim.Map, new TileRect(target.Tile.X, target.Tile.Z, 1, 1), target.Tile.Plane, attacker.Tile);

    sealed class OneTarget : ITileTargets
    {
        internal TileCoord Tile;

        public bool TryGetFootprint(long id, out TileRect footprint, out int plane)
        {
            footprint = new TileRect(Tile.X, Tile.Z, 1, 1);
            plane = Tile.Plane;
            return id == TargetId;
        }
    }
}
