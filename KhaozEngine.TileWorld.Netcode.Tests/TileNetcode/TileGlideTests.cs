using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The FULL-STEP LINEAR GLIDE, through a real <see cref="TileWorldServer"/> and a real
/// <see cref="TileWorldClient"/> over an in-memory transport. The glide's own arithmetic is pinned on
/// <see cref="TilePresenter"/> in <see cref="TilePresenterTests"/>. What is pinned HERE is what the client does
/// with it, which the arithmetic says nothing about: whether a discontinuity cuts, whether a correction pops, and
/// what a frame clock that has stopped being a clock does to the session.
/// <para>WHY THIS SHAPE lives ONCE, in section 5.2 of docs/design/TILE-WORLD-NETCODE-DESIGN-2026-08-22.md: the
/// four-round feel iteration behind the glide, the measured stutter that killed the window, the chase that
/// measured well and still felt wrong, and the ruling that VISIBILITY beats tightness (the game draws a
/// true-tile marker and a route highlight on the reads this package leaves clean, see
/// <see cref="TilePresenter.PoseAt(TileCoord, TileDirection)"/>, <see cref="TileWorldClient.TryGetRemoteTile"/>
/// and the route on <see cref="ClientPrediction{TState,TCommand}.PredictedState"/>). Do not restate the
/// measurement here, point at 5.2.</para>
/// <para>The stutter-refusal test round three carried is retired with the chase it defended. A full-step glide
/// cannot rest mid route BY CONSTRUCTION: it is still moving on the last tick of every step, so there is no
/// schedule to finish early and no gap to wait out. That is asserted once, below, and needs no tuning to hold.
/// </para>
/// </summary>
public class TileGlideTests
{
    // A 1/6 s command tick, which is the cadence the feel was ruled on, with the engine's own walk 4 / run 2 step
    // costs: a walking step is 0.667 s and a running one is 0.333 s.
    const float Tick = 1f / 6f;
    const float Frame = 1f / 60f;

    // ---------------------------------------------------------------------------------------------------------
    // The ruling: continuous motion mid route.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The assertion that replaces round three's stutter refusal, and the only one this file needs about rest
    /// gaps. A twelve tile run at 60 fps, and no frame draws the body where the previous frame drew it while the
    /// route is still running.
    /// <para>It held for the chase because the chase had no schedule to finish. It holds here for a stronger
    /// reason: the glide's schedule IS the step, so the body is still crossing on the last tick before the next
    /// tile commits and the two steps meet with no gap between them. There is no knob that can break this, which
    /// is exactly what "by construction" means and why one assertion is enough where the window needed a
    /// measurement.</para>
    /// <para>Bit-identical positions rather than an epsilon, deliberately: the difference the eye reads as a beat
    /// is the difference between moving slowly and being stopped, and a linear glide at this cadence moves about
    /// 0.05 tiles a frame running, which is nowhere near a float tie.</para>
    /// </summary>
    [Fact]
    public void A_multi_step_run_never_draws_the_body_where_the_previous_frame_drew_it()
    {
        using var loop = new Loop();
        loop.Join();
        loop.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 22, 0), TileMoveMode.Run));

        var drawn = new List<Vector3>();
        TileCoord committed = loop.Client.Prediction.PredictedState.Tile;
        bool running = false;
        for (int i = 0; i < 300; i++)
        {
            loop.Step();
            // Counted from the first COMMIT, which is when the route starts running: a click is queued for the
            // next command tick, and the frames spent waiting for that tick are frames the player is standing
            // still on purpose.
            TileCoord now = loop.Client.Prediction.PredictedState.Tile;
            if (!running && !now.Equals(committed)) running = true;
            if (running && loop.Client.Prediction.PredictedState.Route.IsIdle
                && !loop.Client.Prediction.PredictedState.IsStepping) break;
            if (running) drawn.Add(loop.LocalDrawn);
            committed = now;
        }

        Assert.True(drawn.Count > 150, $"the route was too short to say anything: {drawn.Count} frames");
        int still = 0;
        for (int i = 1; i < drawn.Count; i++) if (drawn[i] == drawn[i - 1]) still++;
        Assert.True(still == 0, $"{still} of {drawn.Count} frames drew the body exactly where the previous one did");
    }

    /// <summary>
    /// The other half of "no schedule to finish": the body arrives exactly as the next step commits, so the two
    /// steps MEET rather than overlapping or leaving a gap. Pinned on the position rather than on the picture,
    /// because it is the property the glide's shape gives and the window's did not: the tile a step lands on is
    /// the tile the next step departs from, at the same fraction, so a route is one unbroken line.
    /// </summary>
    [Fact]
    public void Each_step_departs_from_the_tile_the_previous_one_landed_on()
    {
        var s = TileMoveState.At(new TileCoord(4, 4, 0), TileDirection.N);
        s.StepFrom = new TileCoord(4, 3, 0);
        s.StepTotal = 4;
        s.StepTicks = 4;
        // The last tick of a step lands it: the drawn body is on the committed tile, at fraction 1.
        Vector2 landed = s.Position;
        Assert.Equal(new Vector2(4f, 4f), landed);
        // The next step departs from exactly there, at fraction 0, with nothing in between.
        var next = TileMoveState.At(new TileCoord(4, 5, 0), TileDirection.N);
        next.StepFrom = new TileCoord(4, 4, 0);
        next.StepTotal = 4;
        next.StepTicks = 0;
        Assert.Equal(landed, next.Position);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Discontinuities.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A server teleport draws the local body AT its new tile on the very frame the snapshot lands, with no frame
    /// drawn on the ground between the two places. Gliding a teleport would slide the avatar across every tile in
    /// the gap, which is the one thing a lattice body must never be seen doing, and it would do it while the
    /// head's camera had already been warped by the teleport event.
    /// <para>Nothing in this package does the cut. The prediction layer's own reconcile zeroes both correction
    /// offsets on an epoch advance, so <see cref="ClientPrediction{TState,TCommand}.RenderedState"/> is already on
    /// the new position, and <see cref="TileWorldClient.LocalPose"/> is nothing but that. This test is what proves
    /// the client did not need a second rule of its own to get it right.</para>
    /// </summary>
    [Fact]
    public void A_teleport_draws_the_local_body_on_its_new_tile_the_same_frame()
    {
        using var loop = new Loop();
        loop.Join();
        int teleports = 0;
        loop.Client.Teleported += () => teleports++;

        var far = new TileCoord(10, 40, 0);
        loop.Server.SetPlayerState(0, TileMoveState.At(far, TileDirection.N), teleport: true);

        var zs = new List<float>();
        for (int i = 0; i < 40; i++) { loop.Step(); zs.Add(DrawnTileZ(loop.LocalDrawn.Z)); }

        Assert.Equal(1, teleports);
        Assert.Equal(far, loop.Client.Prediction.PredictedState.Tile);
        // Every frame drew it on one tile or the other, never on the thirty between them.
        foreach (float z in zs)
            Assert.True(z is < 10.5f or > 39.5f, $"a frame drew the body mid teleport at tile z {z}");
    }

    /// <summary>
    /// A HARD SNAP draws the body on its corrected tile the same frame, rather than gliding back across ground it
    /// never covered. That a hidden tree cuts a walk exactly once, and that a cut step is not a teleport, is
    /// pinned in the loopback suite. What is pinned here is the POSE, which is the half no assertion covered.
    /// <para>A hard snap is the client having walked somewhere the server never let it go, so the body is drawn a
    /// good fraction of a tile from where the rules now say it is. Gliding that gap would drag the avatar
    /// backwards at the moment the head most needs the picture to agree with the rules, and the prediction layer
    /// is what refuses to: past <see cref="PredictionSettings.HardSnapDistance"/> it places the state outright and
    /// zeroes the offset instead of decaying one.</para>
    /// </summary>
    [Fact]
    public void A_hard_snap_draws_the_body_on_its_corrected_tile_the_same_frame()
    {
        TileWorldDocument serverDoc = TileMoveSimulatorTests.FlatWorld();
        serverDoc.AddObject("tree", 10, 11, 0, 0);                  // only the SERVER knows about this
        using var loop = new Loop(serverDoc);
        loop.Join();
        int teleports = 0;
        loop.Client.Teleported += () => teleports++;

        loop.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 16, 0), TileMoveMode.Run));
        int snapFrame = -1;
        var beforeSnap = Vector3.Zero;
        for (int i = 0; i < 120 && snapFrame < 0; i++)
        {
            Vector3 previous = loop.LocalDrawn;
            int snaps = loop.Client.SnapCount;
            loop.Step();
            if (loop.Client.SnapCount > snaps) { snapFrame = i; beforeSnap = previous; }
        }

        Assert.True(snapFrame >= 0, "the hidden blocker never produced a hard snap");
        Assert.Equal(0, teleports);                                 // a cut step is not a teleport
        // Not vacuous: the frame before the snap had the body a real distance from where the rules then put it,
        // so what follows is a statement about the snap rather than about a body that was never anywhere else.
        Vector3 corrected = loop.Client.Presenter.PoseAt(loop.Client.Prediction.PredictedState.Tile).Position;
        Assert.True(Vector3.Distance(beforeSnap, corrected) > 0.2f,
            $"the body was already on the corrected tile before the snap, distance {Vector3.Distance(beforeSnap, corrected)}");
        // And the frame the snap landed on drew the body ON the corrected position, with no decaying offset left
        // to unwind: the prediction layer places rather than glides past the snap distance.
        Assert.Equal(loop.Client.Presenter.LocalPose(loop.Client.Prediction).Position, loop.LocalDrawn);
        Assert.Equal(loop.Client.Prediction.PredictedState.Position,
            loop.Client.Prediction.RenderedState.RenderPosition);
    }

    /// <summary>
    /// The complementary case, and the one the glide has to get right on its own: a correction the layer does NOT
    /// cut. A sub-tile disagreement (the authority agrees about which tile the player owns and disagrees only
    /// about how far through the step the body is) must be absorbed CONTINUOUSLY, with no frame jumping the whole
    /// correction. A BACKWARD correction legitimately reverses the drawn motion once (the authority moved the
    /// body back while the replayed step marches forward), so the honest bound is not "never reverses" but
    /// "travels no more than the correction explains": one back and forth of the disagreement, and nothing that
    /// oscillates.
    /// <para>This is where the glide and the chase genuinely differ, and it is worth being explicit rather than
    /// carrying round three's assertion across. Under the chase the right answer was that the drawn body does not
    /// move AT ALL, because the chase's target was the bare tile and the tile had not moved. Under the glide the
    /// body IS the corrected position, so the right answer is that the correction is absorbed by the prediction
    /// layer's decaying offset: the drawn position stays continuous across the rebase and the offset unwinds off
    /// it. Pinned as a bound on the per-frame delta rather than as bit-identity.</para>
    /// </summary>
    [Fact]
    public void A_sub_tile_correction_is_absorbed_without_a_pop_and_travels_only_what_it_explains()
    {
        using var loop = new Loop();
        loop.Join();
        loop.Frames(30);

        // The authority says the player is most of the way into the tile it is standing on, rather than parked on
        // it. A LONG step total keeps the disagreement sub-tile whatever the replay depth: the client's pending
        // Continues advance the step by a hundredth of a tile each, so the position error stays comfortably inside
        // the half-tile snap distance and comfortably outside the float-noise floor CorrectionCount is gated on.
        TileMoveState ahead = loop.Client.Prediction.PredictedState;
        ahead.StepFrom = new TileCoord(10, 9, 0);
        ahead.StepTotal = 100;
        ahead.StepTicks = 90;
        loop.Server.SetPlayerState(0, ahead);

        int snaps = loop.Client.SnapCount, corrections = loop.Client.CorrectionCount;
        (float travelled, float worst, float net) = loop.Trace(90);

        Assert.True(loop.Client.CorrectionCount > corrections, "no reconciliation happened at all");
        Assert.Equal(snaps, loop.Client.SnapCount);                 // it GLIDED rather than cutting
        Assert.True(travelled > 0.02f, $"the drawn body barely moved ({travelled} tiles), so this proves nothing");
        // Stated as a SHARE of the whole correction rather than as a magnitude, so it says the thing it means at
        // any disagreement size: the player is standing still here, so every tile drawn is the offset unwinding,
        // and a pop is one frame taking most of it. A decay at the configured rate takes at most a few per cent
        // of the remaining offset per frame, so a quarter is a wide margin around the right answer and nowhere
        // near the wrong one.
        Assert.True(worst < travelled * 0.25f,
            $"one frame moved the body {worst} tiles of a {travelled} tile correction, which is a pop");
        // The disagreement is 0.1 tiles by construction (the authority at 90 of 100 against a client at the
        // step's end), and the honest motion is one ease back and one walk forward, so the whole trace can
        // explain at most about two crossings of it plus the net move. A decay that OSCILLATES crosses the
        // ground again every swing and breaks this, which is what the unsigned pair above cannot see.
        Assert.True(travelled < 0.25f + net,
            $"the body travelled {travelled} tiles for a net move of {net}, more than one back and forth of a "
          + "0.1 tile correction explains");
    }

    /// <summary>
    /// A correction that MOVES the committed tile, which is the case a lattice makes possible and a continuous
    /// world does not: a one-tick skew between the two heads is a quarter of a tile of position error at walk
    /// cost, well inside <see cref="PredictionSettings.HardSnapDistance"/>, while the two heads'
    /// <see cref="TileMoveState.Tile"/> differ by one. The committed tile jumps a whole tile with no hard snap
    /// behind it, and the DRAWN body must not.
    /// </summary>
    [Fact]
    public void A_correction_that_moves_the_committed_tile_still_draws_a_continuous_body()
    {
        using var loop = new Loop();
        loop.Join();
        loop.Frames(30);

        // The authority owns tile (10, 11) a little way in while the client is standing on (10, 10), so the TILE
        // moves a whole tile while the position error stays a fraction of one.
        TileMoveState ahead = loop.Client.Prediction.PredictedState;
        ahead.Tile = new TileCoord(10, 11, 0);
        ahead.StepFrom = new TileCoord(10, 10, 0);
        ahead.StepTotal = 100;
        ahead.StepTicks = 25;
        loop.Server.SetPlayerState(0, ahead);

        int snaps = loop.Client.SnapCount;
        (float travelled, float worst, float net) = loop.Trace(90);

        Assert.Equal(new TileCoord(10, 11, 0), loop.Client.Prediction.PredictedState.Tile);
        Assert.Equal(snaps, loop.Client.SnapCount);                 // no cut behind it
        Assert.True(travelled > 0.02f, $"the drawn body barely moved ({travelled} tiles), so this proves nothing");
        // The COMMITTED TILE jumped a whole tile and no frame of the drawn body did: the offset takes up the
        // POSITION delta, which on this shape is the only quantity drawn, so the tile's jump never reaches the
        // picture at all. Same share bound as the sub-tile case, and for the same reason.
        Assert.True(worst < travelled * 0.25f,
            $"one frame moved the body {worst} tiles of a {travelled} tile correction, which is a pop");
        Assert.True(worst < 0.2f, $"a single frame moved the body {worst} tiles, most of the tile the rules moved");
        // Same reversal bound as the sub-tile case: one standing body, one unwinding line, no ground crossed twice.
        Assert.True(travelled < net * 1.1f + 0.001f,
            $"the body travelled {travelled} tiles for a net move of {net}, which is a reversal");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The frame clock.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A frame time that is not a finite positive duration advances nothing, and poisons nothing either. The
    /// client's presentation clock ACCUMULATES, so a single NaN dt would take the remote render timeline out for
    /// the rest of the session rather than for a frame, and an infinite dt would carry the render time past every
    /// buffered sample at once and park every remote on its newest one. Both bodies are checked, because the local
    /// one rides the prediction layer's easing and the remote one rides the clock.
    /// </summary>
    [Fact]
    public void A_frame_time_that_is_not_a_finite_duration_advances_nothing_and_poisons_nothing()
    {
        using var loop = new Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.N));
        loop.Frames(20);
        loop.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 30, 0), TileMoveMode.Run));
        loop.Server.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(12, 30, 0), TileMoveMode.Run));
        loop.Frames(20);

        Assert.True(loop.Client.TryGetRemotePose(remote, out TilePose beforeRemote));
        Vector3 beforeLocal = loop.LocalDrawn;
        loop.Client.AdvancePresentation(float.NaN);
        loop.Client.AdvancePresentation(float.PositiveInfinity);
        loop.Client.AdvancePresentation(float.NegativeInfinity);
        loop.Client.AdvancePresentation(-0.016f);
        loop.Client.AdvancePresentation(0f);

        Assert.Equal(beforeLocal, loop.LocalDrawn);
        Assert.True(loop.Client.TryGetRemotePose(remote, out TilePose afterRemote));
        Assert.Equal(beforeRemote.Position, afterRemote.Position);
        // And the clock is still a clock rather than a NaN: both bodies keep moving on the frames that follow.
        loop.Frames(30);
        Assert.True(loop.Client.TryGetRemotePose(remote, out TilePose movedRemote));
        Assert.True(movedRemote.Position.Z != beforeRemote.Position.Z, "the remote render timeline stopped");
        Assert.NotEqual(beforeLocal, loop.LocalDrawn);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The overlay reads.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The consumer-side mitigation's whole data path, exercised through the real client: while a route is
    /// running, the local player's COMMITTED TILE and the tiles still to walk are both readable, per frame, with
    /// no allocation and nothing one snapshot stale. A game that cannot read these cannot draw the true-tile
    /// marker and the route highlight, and the ruling's answer to the glide's lag is exactly those two overlays.
    /// <para>The route is read by INDEXING from <see cref="TileRoute.Index"/> rather than by foreach, which is the
    /// note the docs carry: <see cref="TileRoute.Tiles"/> is an <c>IReadOnlyList</c>, so a foreach over it boxes
    /// an enumerator every frame while an indexed loop allocates nothing.</para>
    /// </summary>
    [Fact]
    public void The_committed_tile_and_the_remaining_route_are_both_readable_while_a_route_runs()
    {
        using var loop = new Loop();
        loop.Join();
        var goal = new TileCoord(10, 18, 0);
        loop.Client.Queue(TileCommand.WalkTo(goal, TileMoveMode.Run));
        loop.Frames(12);

        TileMoveState now = loop.Client.Prediction.PredictedState;
        Assert.False(now.Route.IsIdle, "the route was already finished, so this proves nothing");
        // The committed tile is where the RULES have the player, and it is ahead of the drawn body: that gap is
        // what the marker exists to show.
        Vector3 marker = loop.Client.Presenter.PoseAt(now.Tile).Position;
        // A tenth of a tile, not merely non-zero: the documented average lead is half a tile, so a lead the
        // player could not see would pass a float-noise bound and fail this one.
        Assert.True(Vector3.Distance(marker, loop.LocalDrawn) > 0.1f,
            "the marker and the body coincided, so the lead was not visible at all");
        // The remaining route, indexed, in walk order, ending on the goal the click named.
        var remaining = new List<TileCoord>();
        for (int i = now.Route.Index; i < now.Route.Tiles.Count; i++) remaining.Add(now.Route.Tiles[i]);
        Assert.Equal(now.Route.Remaining, remaining.Count);
        Assert.Equal(goal, remaining[^1]);
        Assert.Equal(now.Route.Next, remaining[0]);
        // Every remaining tile is one Chebyshev step from the one before it, starting from the committed tile, so
        // a highlight drawn straight off this is a connected path rather than a scatter.
        TileCoord previous = now.Tile;
        foreach (TileCoord t in remaining)
        {
            Assert.Equal(previous.Plane, t.Plane);
            Assert.True(Math.Max(Math.Abs(t.X - previous.X), Math.Abs(t.Z - previous.Z)) == 1,
                $"route tile {t} is not one step from {previous}");
            previous = t;
        }
        // And the route shortens as the walk runs, so a highlight rebuilt each frame tracks the walk.
        loop.Frames(24);
        Assert.True(loop.Client.Prediction.PredictedState.Route.Remaining < now.Route.Remaining);
    }

    /// <summary>
    /// The same read for a REMOTE, which is the other half a true-tile overlay wants: the tile another player is
    /// committed to, on that player's own delayed timeline, straight off the replicated state. It is ahead of the
    /// remote's drawn body by the same step the local pair are apart.
    /// <para>A remote's ROUTE is deliberately unavailable: it is owner-only on the wire, so no client can
    /// highlight another player's path. That is a privacy property of the protocol rather than a gap.</para>
    /// </summary>
    [Fact]
    public void A_remotes_committed_tile_is_readable_and_leads_its_drawn_body()
    {
        using var loop = new Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.N));
        loop.Frames(20);
        loop.Server.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(12, 30, 0), TileMoveMode.Run));

        bool sawLead = false;
        for (int i = 0; i < 60; i++)
        {
            loop.Step();
            if (!loop.Client.TryGetRemoteTile(remote, out TileCoord tile)) continue;
            Assert.True(loop.Client.TryGetRemotePose(remote, out TilePose pose));
            Vector3 marker = loop.Client.Presenter.PoseAt(tile).Position;
            // Never BEHIND the body: the committed tile leads a walk north (world -z) or coincides with it.
            Assert.True(marker.Z <= pose.Position.Z + 1e-4f,
                $"the remote's committed tile {tile} was drawn behind its own body");
            if (marker.Z < pose.Position.Z - 1e-3f) sawLead = true;
        }
        Assert.True(sawLead, "the remote's committed tile never led its body, so the read proves nothing");
        Assert.False(loop.Client.TryGetRemoteTile(loop.Client.LocalNetId, out _));
        Assert.False(loop.Client.TryGetRemoteTile(9999, out _));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Harness.
    // ---------------------------------------------------------------------------------------------------------

    // A pose names the tile CENTRE, so the half tile comes back off on the way to a tile coordinate.
    static float DrawnTileZ(float worldZ) => TileWorldSpace.TileZ(worldZ, 1f) - 0.5f;

    // A real server and a real client over an in-memory transport, at the tick and frame rate this file is about.
    // The client's command tick is PHASE OFFSET from the server's, which is the loopback lesson: two hosts
    // stepping in lockstep hide every ordering bug a real client's independent clock exposes.
    sealed class Loop : IDisposable
    {
        public readonly TileWorldServer Server;
        public readonly TileWorldClient Client;
        readonly InMemoryTransportHub hub;
        float serverAccum;

        public Loop(TileWorldDocument? serverDoc = null)
        {
            hub = new InMemoryTransportHub();
            TileWorldDocument doc = serverDoc ?? TileMoveSimulatorTests.FlatWorld();
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

        // Joined AND SEEDED, which are two different moments here: the handshake is immediate over this transport
        // but the first snapshot only arrives on the server's first tick, ten frames later, and prediction is not
        // placed until it does. A test that started before the seed would watch the body cut from the default
        // state's tile (0, 0) onto the spawn, which is correct behaviour and nothing any assertion here is about.
        public void Join()
        {
            Frames(24);
            Assert.True(Client.IsJoined);
            Assert.True(Client.LocalNetId >= 0, "the client was never seeded");
            Assert.Equal(new TileCoord(10, 10, 0), Client.Prediction.PredictedState.Tile);
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

        public Vector3 LocalDrawn => Client.LocalPose.Position;

        // Runs count frames and reports how far the drawn body travelled in TOTAL, the largest single frame of
        // it, and the NET displacement start to end. The correction tests bound worst as a ratio of travelled
        // ("no frame took most of this in one go" at whatever size the disagreement happens to be), and bound
        // travelled against net, because a rubber band walks the same ground twice: total path length near the
        // net move is what "no reversal" actually asserts, and an unsigned sum alone cannot see it.
        public (float Travelled, float Worst, float Net) Trace(int count)
        {
            float travelled = 0f, worst = 0f;
            Vector3 start = LocalDrawn;
            Vector3 previous = start;
            for (int i = 0; i < count; i++)
            {
                Step();
                float d = Vector3.Distance(previous, LocalDrawn);
                travelled += d;
                if (d > worst) worst = d;
                previous = LocalDrawn;
            }
            return (travelled, worst, Vector3.Distance(start, previous));
        }

        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }
}
