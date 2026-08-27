using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// THE TWO REMOTE TILE READS, and the difference between them, pinned rather than implied.
/// <see cref="TileWorldClient.TryGetRemoteTile"/> answers off the DELAYED render timeline the bodies ride, so it
/// agrees with <see cref="TileWorldClient.TryGetRemotePose"/> and trails the server by
/// <see cref="TileWorldClientConfig.InterpolationDelayTicks"/>. <see cref="TileWorldClient.TryGetLatestRemoteTile(long, out TileCoord)"/>
/// answers off the newest APPLIED snapshot, so it trails by the transport latency plus at most one snapshot
/// interval and nothing else.
/// <para>Every test here asserts BOTH, which is the point. Either read alone passes its own bound while the other
/// quietly answers the same thing, and a pair of reads that cannot be told apart is worse than one read: R2's
/// combat overlay picks between them at the call site and needs the choice to mean something. The bounds live in
/// R0 of <c>docs/design/TILE-COMBAT-ACTORS-DESIGN-2026-08-27.md</c>.</para>
/// <para>The delayed read's own agreement with the drawn body is asserted in
/// <see cref="TileGlideTests"/>, next to the glide it belongs to, and is not restated here.</para>
/// </summary>
public class TileRemoteReadTests
{
    const float Tick = 1f / 6f;
    const float Frame = 1f / 60f;

    // How old an answer is, in server ticks, measured exactly rather than inferred from a tile distance: keep the
    // server's committed tile per tick, then find the NEWEST tick whose tile is the answer. A step holds one tile
    // for two ticks running and four walking, so counting tiles and multiplying is a different, coarser number.
    static int TicksBehind(List<(long Tick, TileCoord Tile)> history, TileCoord answer, long now)
    {
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Tile.Equals(answer)) return (int)(now - history[i].Tick);
        return int.MaxValue;   // a tile the server was never committed to, which every assertion below fails on
    }

    /// <summary>
    /// The headline, at both cadences. While a remote walks, the LATEST read is never more than one snapshot
    /// interval behind the tile the server has it committed to, the DELAYED read runs the interpolation delay
    /// behind that, and the two disagree on real frames rather than being one read under two names.
    /// <para>The delayed read is never AHEAD of the latest one, which is the direction the pair has to hold in:
    /// one is an older view of the same timeline, so it can lag it and can equal it and can never lead it. That is
    /// the assertion a future change to either read would break first.</para>
    /// </summary>
    [Theory]
    [InlineData(TileMoveMode.Run)]
    [InlineData(TileMoveMode.Walk)]
    public void The_latest_read_tracks_the_server_while_the_delayed_read_trails_the_interpolation_delay(
        TileMoveMode mode)
    {
        using var loop = new Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.N));
        loop.Frames(20);
        loop.Server.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(12, 60, 0), mode));

        var history = new List<(long Tick, TileCoord Tile)>();
        long lastTick = -1;
        int worstLatest = 0, worstDelayed = 0, disagreements = 0, samples = 0;
        float worstAge = 0f;
        for (int i = 0; i < 180; i++)
        {
            loop.Step();
            Assert.True(loop.Server.TryGetPlayerState(1, out TileMoveState server));
            if (loop.Server.TickCount != lastTick)
            {
                lastTick = loop.Server.TickCount;
                history.Add((lastTick, server.Tile));
            }
            // The first frames are the interpolation buffer warming up, which is a documented state of its own and
            // not what this test is about.
            if (i <= 40) continue;

            Assert.True(loop.Client.TryGetLatestRemoteTile(remote, out TileCoord latest, out float ticksOld));
            Assert.True(loop.Client.TryGetRemoteTile(remote, out TileCoord delayed));
            samples++;
            worstLatest = Math.Max(worstLatest, TicksBehind(history, latest, loop.Server.TickCount));
            worstDelayed = Math.Max(worstDelayed, TicksBehind(history, delayed, loop.Server.TickCount));
            worstAge = Math.Max(worstAge, ticksOld);
            if (!latest.Equals(delayed)) disagreements++;
            // The walk runs north, which is increasing Z, so an older view of it sits at a lower Z or the same one.
            Assert.True(delayed.Z <= latest.Z,
                $"the delayed read was AHEAD of the latest one, {delayed} against {latest}");
        }

        Assert.True(samples > 100, $"the walk was too short to say anything: {samples} frames");
        // The latest read's bound: the transport is instant here, so what is left is the one snapshot interval.
        Assert.True(worstLatest <= 1,
            $"the latest read was {worstLatest} ticks behind the server, over the one snapshot interval bound");
        Assert.True(worstAge <= 1.001f, $"the reported age reached {worstAge} ticks, over one snapshot interval");
        Assert.True(worstAge > 0.1f, "the reported age never moved, so it is not measuring anything");
        // The delayed read's bound: the interpolation delay, plus the same snapshot interval on top of it.
        Assert.True(worstDelayed >= 2,
            $"the delayed read was only {worstDelayed} ticks behind, so it is not on the delayed timeline at all");
        Assert.True(worstDelayed <= 3,
            $"the delayed read was {worstDelayed} ticks behind, over the two tick delay plus one interval");
        // And they are two reads rather than one: a quarter of the frames of a walk is far past float noise.
        Assert.True(disagreements > samples / 4,
            $"the two reads disagreed on only {disagreements} of {samples} frames, so they are the same read");
    }

    /// <summary>
    /// Both reads refuse the same two ids, and refusing has to be the same word in both: an id nobody is tracking,
    /// and the LOCAL player, whose committed tile is <c>Prediction.PredictedState.Tile</c> and who is drawn through
    /// <see cref="TileWorldClient.LocalPose"/> rather than through either of these.
    /// </summary>
    [Fact]
    public void Both_reads_refuse_an_unknown_id_and_the_local_player()
    {
        using var loop = new Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.N));
        loop.Frames(20);

        // The control: a real remote, so a blanket false would not pass this.
        Assert.True(loop.Client.TryGetRemoteTile(remote, out _));
        Assert.True(loop.Client.TryGetLatestRemoteTile(remote, out _));

        Assert.False(loop.Client.TryGetRemoteTile(9999, out _));
        Assert.False(loop.Client.TryGetLatestRemoteTile(9999, out _));
        Assert.False(loop.Client.TryGetLatestRemoteTile(9999, out TileCoord unknown, out float unknownAge));
        Assert.Equal(default, unknown);
        Assert.Equal(0f, unknownAge);

        Assert.False(loop.Client.TryGetRemoteTile(loop.Client.LocalNetId, out _));
        Assert.False(loop.Client.TryGetLatestRemoteTile(loop.Client.LocalNetId, out _));
        Assert.False(loop.Client.TryGetLatestRemoteTile(loop.Client.LocalNetId, out TileCoord local, out float localAge));
        Assert.Equal(default, local);
        Assert.Equal(0f, localAge);
    }

    /// <summary>
    /// A remote teleported by the server CUTS on the latest read, and cuts EARLIER than the delayed one. A teleport
    /// advances the replicated epoch and puts a pair of tiles on the wire that are not one step apart, so
    /// <c>TileProtocol.ReadMove</c> refuses the pair as a step origin and the state arrives standing on its
    /// destination. Both reads therefore answer a real tile at every instant, never one of the thirty in between,
    /// and the only difference is WHEN.
    /// <para>Read the assertion order rather than the count: the latest read reaching the destination strictly
    /// before the delayed one is the whole property, and a change that quietly wired the latest read to the delayed
    /// buffer would still pass every "never in between" assertion here.</para>
    /// </summary>
    [Fact]
    public void A_teleporting_remotes_latest_tile_cuts_ahead_of_the_delayed_one()
    {
        using var loop = new Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        var origin = new TileCoord(12, 10, 0);
        // Ten tiles, which is far enough that no step could cover it and near enough that the observer keeps the
        // remote in its area of interest. A teleport OUT of interest is a despawn, and both reads answer false for
        // it, which is a different property than the one this test is about.
        var destination = new TileCoord(12, 20, 0);
        loop.Server.SetPlayerState(1, TileMoveState.At(origin, TileDirection.N));
        loop.Frames(30);
        Assert.True(loop.Client.TryGetLatestRemoteTile(remote, out TileCoord before));
        Assert.Equal(origin, before);

        loop.Server.SetPlayerState(1, TileMoveState.At(destination, TileDirection.N), teleport: true);

        int latestCutAt = -1, delayedCutAt = -1;
        for (int i = 0; i < 40; i++)
        {
            loop.Step();
            Assert.True(loop.Client.TryGetLatestRemoteTile(remote, out TileCoord latest));
            Assert.True(loop.Client.TryGetRemoteTile(remote, out TileCoord delayed));
            // Never a tile in the gap, on either read: a lattice body is on a square or it is on the other square.
            Assert.True(latest.Equals(origin) || latest.Equals(destination),
                $"the latest read answered {latest}, which is mid teleport");
            Assert.True(delayed.Equals(origin) || delayed.Equals(destination),
                $"the delayed read answered {delayed}, which is mid teleport");
            if (latestCutAt < 0 && latest.Equals(destination)) latestCutAt = i;
            if (delayedCutAt < 0 && delayed.Equals(destination)) delayedCutAt = i;
        }

        Assert.True(latestCutAt >= 0, "the latest read never reached the destination");
        Assert.True(delayedCutAt >= 0, "the delayed read never reached the destination");
        Assert.True(latestCutAt < delayedCutAt,
            $"the latest read cut on frame {latestCutAt} and the delayed one on {delayedCutAt}, so the latest read "
            + "is reading the delayed timeline");
    }

    /// <summary>
    /// The reported age is what an R2 overlay fades on, so it has to keep climbing when the snapshots stop. The
    /// client's frame loop keeps running with a dead server, which is the shape of a stall or a lost link, and the
    /// age passes the interpolation delay rather than sitting under it forever.
    /// <para>The TILE is unchanged through all of it, deliberately: the read never extrapolates and never starts
    /// guessing. It keeps answering the last thing the server said and reports how long ago that was, which is the
    /// difference between a stale marker and a lying one.</para>
    /// </summary>
    [Fact]
    public void The_reported_age_climbs_while_snapshots_stop_arriving()
    {
        using var loop = new Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        var parked = new TileCoord(12, 10, 0);
        loop.Server.SetPlayerState(1, TileMoveState.At(parked, TileDirection.N));
        loop.Frames(30);
        Assert.True(loop.Client.TryGetLatestRemoteTile(remote, out TileCoord tile, out float fresh));
        Assert.Equal(parked, tile);
        Assert.True(fresh <= 1.001f, $"a healthy session reported an age of {fresh} ticks");

        // The server is gone. The head keeps drawing, which is exactly what a head does through a stall.
        for (int i = 0; i < 120; i++) { loop.Client.Poll(); loop.Client.AdvancePresentation(Frame); }

        Assert.True(loop.Client.TryGetLatestRemoteTile(remote, out TileCoord stale, out float staleAge));
        Assert.Equal(parked, stale);
        // Two seconds of frames at a 1/6 s tick is twelve ticks, well past the two tick delay an overlay would
        // otherwise treat as normal.
        Assert.True(staleAge > 10f, $"the age only reached {staleAge} ticks over two seconds of starvation");
    }

    // The allocation tests live in TileRemoteReadAllocationTests below, in the AllocSensitive collection, so
    // the five behavioural tests here keep the assembly's parallelism.

    // ---------------------------------------------------------------------------------------------------------
    // Harness.
    // ---------------------------------------------------------------------------------------------------------

    // A real server and a real client over an in-memory transport, with the client's command tick PHASE OFFSET
    // from the server's, which is this package's loopback rule: two hosts stepping in lockstep hide every ordering
    // bug a real client's independent clock runs into. Its own copy rather than a shared one, matching what
    // TileGlideTests and TileWorldClientLoopbackTests already do: each file's harness carries the cadence and the
    // world its own subject needs, and one shared harness would grow a parameter per caller.
    internal sealed class Loop : IDisposable
    {
        public readonly TileWorldServer Server;
        public readonly TileWorldClient Client;
        readonly InMemoryTransportHub hub;
        float serverAccum;

        public Loop()
        {
            hub = new InMemoryTransportHub();
            TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
            Server = new TileWorldServer(hub.Server,
                TileWorldServerTickTests.Config(new TileCoord(10, 10, 0)) with { TickSeconds = Tick },
                TileMoveSimulatorTests.Bake(doc),
                new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
            Client = new TileWorldClient(hub.CreateClient(), new TileWorldClientConfig
            {
                TickSeconds = Tick,
                StepTicks = new TileStepTicks(walk: 4, run: 2),
            }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));
            Client.Tick(0.037f);
            Client.Poll();
        }

        // Joined AND seeded, which are two moments over this transport: the handshake is immediate but the first
        // snapshot only arrives on the server's first tick.
        public void Join()
        {
            Frames(24);
            Assert.True(Client.IsJoined);
            Assert.True(Client.LocalNetId >= 0, "the client was never seeded");
        }

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++) Step();
        }

        public void Step()
        {
            Client.Tick(Frame);
            Server.Poll();
            serverAccum += Frame;
            while (serverAccum >= Tick) { serverAccum -= Tick; Server.Tick(Tick); }
            Client.Poll();
            Client.AdvancePresentation(Frame);
        }

        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }
}

/// <summary>
/// The allocation half of the remote reads, serialized in the AllocSensitive collection so byte counting is not
/// disturbed by parallel test threads, and split from <see cref="TileRemoteReadTests"/> so the behavioural tests
/// there keep the assembly's parallelism.
/// </summary>
[Collection("AllocSensitive")]
public class TileRemoteReadAllocationTests
{
    /// <summary>
    /// The steady-state read costs nothing. R2 calls it once per drawn actor per frame, and a read that allocated
    /// would put the combat overlay's cost on the GC rather than on the frame.
    /// </summary>
    [Fact]
    public void The_latest_read_allocates_nothing_on_the_steady_path()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.N));
        loop.Frames(30);
        loop.Server.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(12, 60, 0), TileMoveMode.Run));
        loop.Frames(20);

        // Warm up the JIT on both shapes before anything is measured.
        for (int i = 0; i < 200; i++)
        {
            loop.Client.TryGetLatestRemoteTile(remote, out _);
            loop.Client.TryGetLatestRemoteTile(remote, out _, out _);
        }

        const int iterations = 20000;
        int hits = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            if (loop.Client.TryGetLatestRemoteTile(remote, out _, out _)) hits++;
            if (loop.Client.TryGetLatestRemoteTile(9999, out _, out _)) hits++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(iterations, hits);   // sanity: the loop actually read a live remote every time
        Assert.True(allocated < 4096,
            $"the read allocated {allocated} bytes over {iterations * 2} calls, which is not a free read");
    }

    // The CAPTURE half has no allocation test, deliberately. A bracket around whole loopback frames measured
    // about 1.1 KB per frame of legitimate transport and snapshot-apply allocation, which swamps anything a
    // reshaped capture could add at this harness's scale, so such a test pins the transport's habits rather
    // than the capture's. The capture's allocation freedom is a review-verified property of its code shape
    // (one pre-sized dictionary, struct values, no LINQ), not a measured one.
}
