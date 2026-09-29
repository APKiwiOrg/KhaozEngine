using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

// One pursuit window, with every tick relative to W0, the first tick the target's committed tile changes.
// WindowEnd is the window length, so ResolvedTicks and WindowEnd share that origin.
internal readonly record struct PursuitWindow(int Landed, int IllegalReachEnds, int EndsAfterFirstLanded,
    long[] ResolvedTicks, long WindowEnd, bool EveryResolutionWithinBound, bool TargetNeverIdle,
    bool StayedInOneRegion)
{
    // Resolutions in [first, first + cadences * cadence), counted from this build's own first resolution.
    public int SteadyCount(int cadences, byte cadence)
    {
        if (ResolvedTicks.Length == 0) return 0;
        long first = ResolvedTicks[0], end = first + (long)cadences * cadence;
        return ResolvedTicks.Count(tick => tick < end);
    }
}

// Equal-speed tile pursuit inside one 64-tile region: an attacker follows a target walking or running east, with
// preparation enabled or on the legacy path. The gait, profile, cadence and lock phase are the only inputs.
internal static class PursuitScenario
{
    const long LockTick = 20;
    const int WindowTicks = 110;
    static readonly TileCoord AttackerSpawn = new(2, 20, 0);
    static readonly TileCoord TargetSpawn = new(3, 20, 0);
    static readonly TileCoord Goal = new(62, 20, 0);

    internal static PursuitWindow Run(bool prepared, TileMoveMode gait, byte cadence,
        byte lead, byte strike, int phase)
    {
        var profiles = new PreparationScenario.Profiles { Current = new(lead, strike, 7) };
        var rules = new PreparationScenario.FixedRules { Ticks = cadence };
        var hub = new InMemoryTransportHub();
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0));
        TileWorldServerConfig config = TileWorldServerTickTests.Config(AttackerSpawn) with
        {
            TickSeconds = 1f / 6f,
            CombatPreparationRules = prepared ? profiles : null
        };
        using var server = new TileWorldServer(hub.Server, config, TileMoveSimulatorTests.Bake(doc),
            new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        server.CombatRules = rules;
        long attacker = server.SpawnActor(AttackerSpawn, new TileActorSpawn(1000, cadence, TileDirection.E));
        long target = server.SpawnActor(TargetSpawn, new TileActorSpawn(1000, cadence, TileDirection.E));
        while (server.TickCount < LockTick) server.Tick(1f / 6f);

        var legacyTicks = new List<long>();
        server.OnCombatEvent += outcome =>
        {
            if (outcome.AttackerNetId == attacker) legacyTicks.Add(server.TickCount);
        };
        var schedules = new Dictionary<ulong, TileCombatPreparation>();
        var resolved = new List<long>();
        var illegalEnds = new List<long>();
        var allEnds = new List<long>();
        var commits = new List<long>();
        bool withinBound = true, oneRegion = true;
        long w0 = -1;
        TileCoord targetTile = TargetSpawn;
        long walkTick = LockTick + phase;
        byte period = config.StepTicks.For(gait);

        server.Actors.Command(attacker, TileCommand.Attack(target, gait));
        while (w0 < 0 ? server.TickCount <= walkTick + period : server.TickCount < w0 + WindowTicks)
        {
            long tick = server.TickCount;
            if (tick == walkTick) server.Actors.Command(target, TileCommand.WalkTo(Goal, gait));
            server.Tick(1f / 6f);

            oneRegion &= InRegion(server, attacker) && InRegion(server, target);
            Assert.True(server.TryGetActorState(target, out TileMoveState moved));
            if (!moved.Tile.Equals(targetTile))
            {
                if (w0 < 0) w0 = tick;
                commits.Add(tick - w0);
                targetTile = moved.Tile;
            }
            // Re-latch the same goal when the planned route runs out, so the path window never parks the target.
            if (tick >= walkTick && moved.Route.IsIdle) server.Actors.Command(target, TileCommand.WalkTo(Goal, gait));

            if (prepared)
            {
                foreach (PreparedCombatEvent result in server.PreparedCombatEventsThisTick)
                {
                    if (result.Outcome.AttackerNetId != attacker) continue;
                    resolved.Add(result.ImpactTick);
                    withinBound &= schedules.TryGetValue(result.AttackId, out TileCombatPreparation schedule)
                        && result.ImpactTick >= schedule.ImpactTick
                        && result.ImpactTick <= schedule.ImpactTick + schedule.StrikeTicks;
                }
                foreach (CombatPreparationEnded ended in server.EndedCombatPreparationsThisTick)
                {
                    if (ended.AttackerNetId != attacker) continue;
                    allEnds.Add(ended.ServerTick);
                    if (ended.Reason == TileCombatPreparationEndReason.IllegalReach) illegalEnds.Add(ended.ServerTick);
                }
                if (server.TryGetCombatPreparation(attacker, out TileCombatPreparation active))
                    schedules[active.AttackId] = active;
            }
        }
        Assert.True(w0 >= 0, "The target never committed a step.");

        long[] inWindow = (prepared ? resolved : legacyTicks).Where(t => t >= w0).Select(t => t - w0).ToArray();
        long firstLanded = inWindow.Length > 0 ? inWindow[0] : long.MaxValue;
        int illegal = illegalEnds.Count(t => t >= w0);
        int endsAfter = allEnds.Count(t => t >= w0 && t - w0 > firstLanded);
        bool neverIdle = commits.Count > 0 && commits[0] == 0 && WindowTicks - commits[^1] <= period;
        for (int i = 1; i < commits.Count; i++) neverIdle &= commits[i] - commits[i - 1] <= period;
        return new PursuitWindow(inWindow.Length, illegal, endsAfter, inWindow, WindowTicks, withinBound, neverIdle,
            oneRegion);
    }

    static bool InRegion(TileWorldServer server, long netId) =>
        server.Host.TryGetOwner(netId, out CellSim cell, out _) && cell.Coord.Equals(new CellCoord(0, 0));

    internal static string Table(IEnumerable<(int Phase, PursuitWindow Before, PursuitWindow After)> rows,
        byte cadence, int cadences)
    {
        var text = new StringBuilder();
        text.AppendLine("phase build    landed illegal-ends ends-after-first first steady resolved-from-W0");
        foreach ((int phase, PursuitWindow before, PursuitWindow after) in rows)
        {
            Line(text, phase, "legacy  ", before, cadence, cadences);
            Line(text, phase, "prepared", after, cadence, cadences);
        }
        return text.ToString();
    }

    static void Line(StringBuilder text, int phase, string build, PursuitWindow window, byte cadence, int cadences)
    {
        string first = window.ResolvedTicks.Length > 0
            ? window.ResolvedTicks[0].ToString(CultureInfo.InvariantCulture) : "none";
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{phase,5} {build} {window.Landed,6} {window.IllegalReachEnds,12} {window.EndsAfterFirstLanded,16} " +
            $"{first,5} {window.SteadyCount(cadences, cadence),6} [{string.Join(", ", window.ResolvedTicks)}]" +
            $" bound={window.EveryResolutionWithinBound} moving={window.TargetNeverIdle} region={window.StayedInOneRegion}");
    }
}
