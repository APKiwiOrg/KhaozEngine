using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The damped chase: the drawn body pursuing the tile the simulation has already committed it to, halving the
/// remaining gap every half life. It replaced a GLIDE WINDOW, a fixed number of seconds in which the body crossed
/// the whole step and then waited on its tile, and the reason it replaced it is the first test here.
/// <para>The chase's own arithmetic is pinned directly on <see cref="TileChase"/>, and everything about how a
/// client USES it goes through a real <see cref="TileWorldServer"/> and a real <see cref="TileWorldClient"/> over
/// an in-memory transport. Neither half is enough on its own: the arithmetic says nothing about whether the client
/// resets the chase on a teleport, and the loopback says nothing about frame-rate independence at a frame rate no
/// loopback runs at.</para>
/// </summary>
public class TileChaseTests
{
    // A 1/6 s command tick, which is the cadence the feel was ruled on, with the engine's own walk 4 / run 2 step
    // costs: a walking step is 0.667 s and a running one is 0.333 s.
    const float Tick = 1f / 6f;
    const float RunStep = 2f * Tick;
    const float WalkStep = 4f * Tick;
    const int Fps = 60;
    const float Frame = 1f / Fps;
    const float H = TileChase.DefaultHalfLifeSeconds;

    // ---------------------------------------------------------------------------------------------------------
    // The ruling: continuous motion mid route.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// THE test this whole change exists for. A twelve tile run at 60 fps, and no frame may draw the body where
    /// the previous frame drew it while the route is still running.
    /// <para>Measured RED against the glide window this replaced, at its shipped 0.1 s: <c>frames 220, still 157,
    /// longest still run 14, steps 13, still-at-each-commit [1, 8, 17, 31, 45, 59, 73, 87, 101, 115, 129, 143,
    /// 157]</c>. Six moving frames then fourteen dead ones, twelve times over, 71 per cent of the route spent
    /// standing on a tile waiting for the next commit. That is the metronome the playtest reported, and it is
    /// STRUCTURAL rather than a tuning miss: any schedule that finishes the step early leaves a rest gap, and any
    /// schedule that does not finish early is the constant slide the playtest rejected before it.</para>
    /// <para>The assertion is bit-identical positions rather than an epsilon on purpose. A chase has no schedule
    /// to finish, so its velocity decays but never reaches zero, and the difference between "slowing" and
    /// "stopped" is exactly the difference the eye reads as a beat.</para>
    /// </summary>
    [Fact]
    public void A_multi_step_run_never_draws_the_body_where_the_previous_frame_drew_it()
    {
        using var loop = new Loop();
        loop.Join();
        var goal = new TileCoord(10, 22, 0);
        loop.Client.Queue(TileCommand.WalkTo(goal, TileMoveMode.Run));

        Vector3 previous = loop.LocalDrawn;
        int frames = 0, counted = 0, still = 0, commits = 0;
        TileCoord tile = loop.Client.Prediction.PredictedState.Tile;
        while (!loop.Client.Prediction.PredictedState.Tile.Equals(goal) && frames < 600)
        {
            loop.Step();
            frames++;
            Vector3 now = loop.LocalDrawn;
            // Counted from the first COMMIT, which is when the route starts running: a click is queued for the
            // next command tick, and the frames spent waiting for that tick are frames the player is standing
            // still on purpose.
            if (commits > 0) { counted++; if (now == previous) still++; }
            previous = now;
            if (!loop.Client.Prediction.PredictedState.Tile.Equals(tile))
            {
                tile = loop.Client.Prediction.PredictedState.Tile;
                commits++;
            }
        }

        Assert.True(frames < 600, "the route never finished");
        Assert.True(commits >= 10, $"expected a multi step run, saw {commits} commits");
        Assert.True(still == 0, $"{still} of {counted} route frames drew the body exactly where the previous one did");
    }

    /// <summary>
    /// The same route, measured the other way: a frame may not move the body further than the chase itself can
    /// move it. The bound is the chase's OWN arithmetic rather than a magnitude somebody liked the look of, so it
    /// tracks the half life if anyone retunes it.
    /// <para>A running step is one tile, or sqrt(2) on a diagonal, and at steady state the gap right after a
    /// commit is <c>d / (1 - 2^(-step / h))</c>. A frame closes <c>1 - 2^(-frame / h)</c> of whatever gap is
    /// there, so the largest honest frame is the product. What this catches is a POP: the composition mistakes
    /// (adding a correction offset on top of the chase rather than into its target, or resetting the chase on an
    /// ordinary reconcile) move the body a large fraction of a tile in ONE frame, which is nothing like the
    /// bound.</para>
    /// </summary>
    [Fact]
    public void No_frame_moves_the_body_further_than_the_chase_itself_can()
    {
        using var loop = new Loop();
        loop.Join();
        // A dog leg, so diagonal steps are in it: they are the longest step the bound has to cover.
        loop.Client.Queue(TileCommand.WalkTo(new TileCoord(16, 18, 0), TileMoveMode.Run));

        Vector3 previous = loop.LocalDrawn;
        float worst = 0f;
        for (int i = 0; i < 300; i++)
        {
            loop.Step();
            Vector3 now = loop.LocalDrawn;
            worst = MathF.Max(worst, (now - previous).Length());
            previous = now;
        }

        float bound = FrameBound(MathF.Sqrt(2f), RunStep);
        Assert.Equal(new TileCoord(16, 18, 0), loop.Client.Prediction.PredictedState.Tile);
        // A clean walk, so the bound is the chase alone with nothing else folded into it.
        Assert.Equal(0, loop.Client.CorrectionCount);
        // The epsilon is float slack, not headroom: at steady state the frame right after a diagonal commit sits
        // exactly ON this bound, which is the point of deriving it rather than measuring one.
        Assert.True(worst <= bound + 1e-4f,
            $"a frame moved the body {worst} tiles against a chase bound of {bound}");
        // And the bound is not vacuous: the body really did travel.
        Assert.True(worst > bound / 4f, $"worst frame was {worst}, so the bound of {bound} proves nothing");
    }

    /// <summary>
    /// Arrival: when the target stops, the body settles onto it monotonically, with no overshoot, and then rests
    /// EXACTLY there. Five half lives is the settle claim (the standard three-to-five time-constant criterion, and
    /// 2^-5 is 3.1 per cent), and the exact rest is what <see cref="TileChase.SettleTiles"/> buys on top: a
    /// first-order decay is an asymptote, so without it a standing body twitches in the low bits for ever and
    /// nothing can ever be compared for equality.
    /// </summary>
    [Fact]
    public void A_stopped_target_settles_within_five_half_lives_and_then_rests_exactly_on_it()
    {
        var chase = new TileChase(H);
        var target = new Vector2(0f, 0f);
        chase.SnapTo(new Vector2(0f, -1f));                        // a whole tile of gap, the post-commit worst
        float gap0 = 1f;

        float previous = gap0;
        float atFiveHalfLives = float.NaN;
        float restedAt = float.NaN;
        for (int i = 1; i <= 2000; i++)
        {
            Vector2 drawn = chase.Advance(target, Frame);
            float gap = (drawn - target).Length();
            // Monotone and never past the target: a first-order chase cannot overshoot, which is the whole reason
            // the gap is SCALED rather than the position lerped. Asserted anyway, every frame.
            Assert.True(gap <= previous, $"frame {i} moved the gap from {previous} back up to {gap}");
            Assert.True(drawn.Y <= target.Y, $"frame {i} drew at {drawn.Y}, past the target at {target.Y}");
            previous = gap;
            float t = i * Frame;
            if (float.IsNaN(atFiveHalfLives) && t >= 5f * H) atFiveHalfLives = gap;
            if (float.IsNaN(restedAt) && gap == 0f) restedAt = t;
        }

        Assert.True(atFiveHalfLives <= gap0 * 0.032f,
            $"five half lives left {atFiveHalfLives} tiles of a {gap0} tile gap, over the 2^-5 settle claim");
        Assert.True(restedAt <= 1f, $"the body took {restedAt} s to come to an exact rest");
        // Rested means RESTED: further frames are bit identical, so a head may compare two poses without an
        // epsilon and a standing avatar cannot jitter.
        Vector2 a = chase.Advance(target, Frame);
        Assert.Equal(a, chase.Advance(target, Frame));
        Assert.Equal(target, a);
    }

    /// <summary>
    /// The steady-state lag, which is the half of the invariant a game controls, pinned as a NUMBER rather than as
    /// a direction. Chasing a target that steps one tile every step duration, the mean gap is exactly
    /// <c>speed * h / ln 2</c>: the integral of the decaying gap over one period works out independent of the step
    /// size, which is why the knob can be in seconds and still mean the same thing to a walk and to a run.
    /// <para>Run and walk are both taken because they are the case the seconds-versus-share argument turns on: at
    /// one half life the running lag is exactly twice the walking one (twice the speed), and a share-of-the-step
    /// knob would instead have made the two catch up at different wall-clock rates.</para>
    /// </summary>
    [Theory]
    [InlineData(RunStep)]
    [InlineData(WalkStep)]
    public void The_steady_state_lag_is_speed_times_the_half_life_over_ln_two(float stepSeconds)
    {
        int framesPerStep = (int)MathF.Round(stepSeconds / Frame);
        var chase = new TileChase(H);
        chase.SnapTo(Vector2.Zero);
        float target = 0f;
        double area = 0d, seconds = 0d;
        // Twenty steps: ten to reach steady state, ten measured. The gap's own transient is gone in five half
        // lives, so ten steps of run cadence is generous.
        for (int f = 0; f < 20 * framesPerStep; f++)
        {
            if (f % framesPerStep == 0) target += 1f;
            // A TIME average, by the trapezoid rule over each frame, because the quantity being pinned is a mean
            // over the period rather than a mean over samples. Averaging the sampled gaps instead reads about
            // eight per cent low at 60 fps, purely because every sample is taken a whole frame after the commit
            // and so misses the peak: that is a property of the sampling, not of the chase.
            float before = target - chase.Drawn.Y;
            chase.Advance(new Vector2(0f, target), Frame);
            float after = target - chase.Drawn.Y;
            if (f < 10 * framesPerStep) continue;
            area += (before + after) * 0.5d * Frame;
            seconds += Frame;
        }

        float mean = (float)(area / seconds);
        float expected = 1f / stepSeconds * H / MathF.Log(2f);
        Assert.True(MathF.Abs(mean - expected) < expected * 0.02f,
            $"mean lag {mean} tiles against the predicted {expected}");
        // The worked numbers the docs quote, at the default half life and a 1/6 s tick.
        Assert.Equal(stepSeconds == RunStep ? 0.303f : 0.151f, expected, 3);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Frame-rate independence.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 30 fps and 144 fps draw the same body at the same wall-clock instants. That is the property the exponential
    /// form is chosen FOR: the factor is <c>2^(-dt / h)</c> and the exponent is additive, so subdividing a frame
    /// cannot change where the body ends up. A per-frame "move a fixed share of the gap" would be off by more than
    /// a tile over this route.
    /// <para>Each rate advances in its own frames, cut short at every sample instant so both are compared at the
    /// SAME time rather than at whichever frame happened to land nearest. That cut also exercises the uneven dt a
    /// real head hands in.</para>
    /// </summary>
    [Fact]
    public void Thirty_and_a_hundred_and_forty_four_fps_draw_the_same_body_at_the_same_instants()
    {
        var instants = new List<float>();
        for (int i = 1; i <= 60; i++) instants.Add(i * 0.05f);

        List<Vector2> slow = Sampled(1f / 30f, instants);
        List<Vector2> fast = Sampled(1f / 144f, instants);

        float worst = 0f;
        for (int i = 0; i < instants.Count; i++) worst = MathF.Max(worst, (slow[i] - fast[i]).Length());
        Assert.True(worst < 1e-4f, $"30 and 144 fps diverged by {worst} tiles");
        // Not vacuous: the body actually moved several tiles across the sampled window.
        Assert.True((slow[^1] - slow[0]).Length() > 3f);
    }

    // Runs a chase against a target that steps one tile every RunStep seconds, advancing in whole frames of dt but
    // never PAST the next sample instant, and reports the drawn position at each of those instants. Cutting the
    // frame at the instant is what makes two unrelated frame rates comparable at all: 1/30 s is 4.8 frames of
    // 1/144 s, so nothing else lines them up.
    static List<Vector2> Sampled(float dt, List<float> instants)
    {
        var chase = new TileChase(H);
        chase.SnapTo(Vector2.Zero);
        var drawn = new List<Vector2>();
        float t = 0f, target = 0f, nextCommit = RunStep;
        foreach (float instant in instants)
        {
            while (t < instant)
            {
                float step = MathF.Min(dt, MathF.Min(instant, nextCommit) - t);
                t += step;
                chase.Advance(new Vector2(0f, target), step);
                if (t >= nextCommit) { target += 1f; nextCommit += RunStep; }
            }
            drawn.Add(chase.Drawn);
        }
        return drawn;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Discontinuities.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A server teleport draws the local body AT its new tile on the very frame the snapshot lands, with no frame
    /// drawn on the ground between the two places. A chase that pursued a teleport would slide the avatar across
    /// every tile in the gap, which is the one thing a lattice body must never be seen doing, and it would do it
    /// while the head's camera had already been warped by the teleport event.
    /// </summary>
    [Fact]
    public void A_teleport_draws_the_local_body_on_its_new_tile_the_same_frame()
    {
        using var loop = new Loop();
        loop.Join();
        int teleports = 0;
        loop.Client.Teleported += () => teleports++;

        var drawn = new List<float>();
        loop.Server.SetPlayerState(0, TileMoveState.At(new TileCoord(10, 40, 0), TileDirection.S), teleport: true);
        for (int i = 0; i < 60; i++)
        {
            loop.Step();
            drawn.Add(DrawnTileZ(loop.LocalDrawn.Z));
        }

        Assert.Equal(1, teleports);
        Assert.Equal(new TileCoord(10, 40, 0), loop.Client.Prediction.PredictedState.Tile);
        // Every frame drew it on one tile or the other, never on the thirty between them.
        Assert.All(drawn, z => Assert.True(MathF.Abs(z - 10f) < 1e-3f || MathF.Abs(z - 40f) < 1e-3f,
            $"drawn at tile z {z}, which is between the two tiles rather than on either"));
        Assert.Equal(40f, drawn[^1], 3);
    }

    /// <summary>
    /// THE composition test, and the reason the chase target is the bare committed tile rather than the
    /// "corrected" one. A SUB-TILE correction is the case that separates the three candidate compositions: the
    /// authority agrees about which tile the player owns and disagrees only about how far through the step the
    /// body is, which on this lattice is the only kind of correction that GLIDES at all (anything bigger crosses
    /// <see cref="PredictionSettings.HardSnapDistance"/> and cuts).
    /// <para>The right answer is that the drawn body does not move AT ALL: the tile it is committed to never
    /// changed, so the picture was already correct. Adding the prediction layer's offset to the chase's OUTPUT
    /// jumps the body by the whole offset in one frame and then unwinds it, a pop and a reversal. Folding the
    /// offset into the chase's TARGET, which looks like the careful fix, is not much better here: the offset takes
    /// up the POSITION delta while the target would move by the TILE delta, and with the tile unmoved the target
    /// would drift a fraction of a tile PAST the committed tile, in the opposite direction to the correction, and
    /// come back. Bit-identical frames is what a target of the bare tile buys, and it is what this asserts.</para>
    /// </summary>
    [Fact]
    public void A_sub_tile_correction_glides_without_moving_the_drawn_body_at_all()
    {
        using var loop = new Loop();
        loop.Join();
        int teleports = 0;
        loop.Client.Teleported += () => teleports++;
        loop.Frames(60);                                            // settle the chase exactly onto the tile
        Vector3 resting = loop.LocalDrawn;

        // The authority says the player is 90 per cent of the way into the tile it is standing on, rather than
        // parked on it. A LONG step total is what keeps the disagreement sub-tile whatever the replay depth: the
        // client's pending Continues advance the step by a hundredth of a tile each, so the position error is
        // about a tenth of a tile, comfortably inside the half-tile snap distance and comfortably outside the
        // float-noise floor CorrectionCount is gated on.
        TileMoveState midStep = TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.N);
        midStep.StepFrom = new TileCoord(10, 9, 0);
        midStep.StepTotal = 100;
        midStep.StepTicks = 90;
        loop.Server.SetPlayerState(0, midStep);

        var drawn = new List<Vector3>();
        for (int i = 0; i < 90; i++)
        {
            loop.Step();
            drawn.Add(loop.LocalDrawn);
        }

        // A real reconciliation happened, it GLIDED rather than cutting, and the layer really is carrying a
        // decaying offset. Without all three the assertion below is vacuous.
        Assert.True(loop.Client.CorrectionCount > 0, "the two heads never disagreed, so nothing was corrected");
        Assert.Equal(0, loop.Client.SnapCount);
        Assert.Equal(0, teleports);
        Assert.Equal(new TileCoord(10, 10, 0), loop.Client.Prediction.PredictedState.Tile);
        // And the drawn body never moved, by a single float bit, across any of it.
        Assert.All(drawn, p => Assert.Equal(resting, p));
    }

    /// <summary>
    /// The complementary case, and the one the chase alone now carries: a correction that MOVES the committed
    /// tile. The target jumps a whole tile with no hard snap behind it, so the body has to close that tile on the
    /// chase's own curve rather than cutting to it.
    /// <para>Reachable at ordinary cadence. A one-tick skew between the two heads is a quarter of a tile of
    /// position error at walk cost, well inside <see cref="PredictionSettings.HardSnapDistance"/>, while the two
    /// heads' <see cref="TileMoveState.Tile"/> differ by one. The shape is built directly here, with the same long
    /// step total the sub-tile test uses so no replay depth can push the error over the snap distance: the
    /// authority owns tile (10, 11) a quarter of the way in while the client is standing on (10, 10), so the
    /// position error is about 0.25 and the TARGET moves a whole tile.</para>
    /// <para>What is asserted is the CURVE rather than merely that the body moved. Every frame leaves exactly
    /// <c>2^(-frame / h)</c> of the gap before it, which is the same arithmetic an ordinary step is drawn with, so
    /// a tile-moving correction is indistinguishable from movement. A cut would land the whole tile in one frame,
    /// and a correction drawn through the prediction layer's own SmoothDamp would close it on a different curve at
    /// a different rate.</para>
    /// </summary>
    [Fact]
    public void A_correction_that_moves_the_committed_tile_eases_at_the_half_life()
    {
        using var loop = new Loop();
        loop.Join();
        int teleports = 0;
        loop.Client.Teleported += () => teleports++;
        loop.Frames(60);                                            // settle the chase exactly onto tile (10, 10)
        Assert.Equal(10f, DrawnTileZ(loop.LocalDrawn.Z), 5);

        TileMoveState ahead = TileMoveState.At(new TileCoord(10, 11, 0), TileDirection.N);
        ahead.StepFrom = new TileCoord(10, 10, 0);
        ahead.StepTotal = 100;
        ahead.StepTicks = 25;                                       // position z 10.25, a quarter tile of error
        loop.Server.SetPlayerState(0, ahead);

        var gaps = new List<float>();
        float worstFrame = 0f, previous = DrawnTileZ(loop.LocalDrawn.Z);
        for (int i = 0; i < 60; i++)
        {
            loop.Step();
            float drawn = DrawnTileZ(loop.LocalDrawn.Z);
            worstFrame = MathF.Max(worstFrame, MathF.Abs(drawn - previous));
            previous = drawn;
            if (loop.Client.Prediction.PredictedState.Tile.Z == 11) gaps.Add(11f - drawn);
        }

        // A real gliding correction that moved the TILE, with nothing cutting behind it. Without these the curve
        // assertions below would pass on a client that simply walked there.
        Assert.True(loop.Client.CorrectionCount > 0, "the two heads never disagreed, so nothing was corrected");
        Assert.Equal(0, loop.Client.SnapCount);
        Assert.Equal(0, teleports);
        Assert.Equal(new TileCoord(10, 11, 0), loop.Client.Prediction.PredictedState.Tile);
        Assert.True(gaps.Count > 30, $"only {gaps.Count} frames ran after the committed tile moved");

        // It EASED: a whole tile of gap was still open on the frame the correction landed, and the frame that
        // closed the most of it closed no more than the chase itself can.
        float perFrame = MathF.Pow(2f, -Frame / H);
        Assert.Equal(perFrame, gaps[0], 3);
        Assert.True(worstFrame <= 1f - perFrame + 1e-4f,
            $"a frame moved the body {worstFrame} tiles against a chase bound of {1f - perFrame}");
        // And it eased on the CHASE's curve, every frame of it, down to where float noise starts to matter.
        for (int i = 1; i < gaps.Count && gaps[i - 1] > 0.01f; i++)
        {
            Assert.True(MathF.Abs(gaps[i] / gaps[i - 1] - perFrame) < 0.01f,
                $"frame {i} left {gaps[i] / gaps[i - 1]} of the gap before it, against the chase's {perFrame}");
        }
    }

    /// <summary>
    /// A HARD SNAP resets the chase, and the frame it lands on draws the body AT its corrected tile rather than
    /// starting a pursuit toward it. That a hidden tree cuts a walk exactly once, and that a cut step is not a
    /// teleport, is pinned in the loopback suite. What is pinned here is the POSE, which is the half the reset
    /// exists for and the half no assertion covered.
    /// <para>A hard snap is the client having walked somewhere the server never let it go, so the body is drawn a
    /// good fraction of a tile from where the rules now say it is. Chasing that gap would slide the avatar back
    /// across ground it never covered, at the moment the head most needs the picture to agree with the rules.
    /// </para>
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

        loop.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 18, 0), TileMoveMode.Run));
        float lagBefore = 0f;
        for (int i = 0; i < 200 && loop.Client.SnapCount == 0; i++)
        {
            lagBefore = MathF.Abs(loop.Client.Presenter.Pose(loop.Client.Prediction.PredictedState).Position.Z
                - loop.LocalDrawn.Z);
            loop.Step();
        }

        Assert.Equal(1, loop.Client.SnapCount);
        Assert.Equal(0, teleports);                                 // a cut step is not a teleport
        // Not vacuous: the frame before the snap had the body a real distance behind its own committed tile, so
        // what follows is a statement about the snap rather than about a body that was never anywhere else.
        Assert.True(lagBefore > TileChase.SettleTiles * 10f,
            $"the body was only {lagBefore} tiles from its committed tile before the snap");
        // The frame the snap lands on draws the body ON the corrected tile, exactly. The planar axes are the
        // chase's, which is what the reset touches; the vertical is the prediction layer's own and is not this.
        TilePose target = loop.Client.Presenter.Pose(loop.Client.Prediction.PredictedState);
        Vector3 drawn = loop.LocalDrawn;
        Assert.Equal(target.Position.X, drawn.X);
        Assert.Equal(target.Position.Z, drawn.Z);
    }

    // ---------------------------------------------------------------------------------------------------------
    // One knob, both paths.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The local player and a remote are drawn by the same chase code on the same knob, so they cannot feel
    /// different. Asserted where it can actually be observed rather than by reading a field back: both walk the
    /// same straight run, and the mean distance each is drawn BEHIND its own committed tile is the steady-state
    /// lag the half life predicts, on both paths.
    /// <para>What the two paths do NOT share is their timeline. The local player is predicted, so its lag is the
    /// chase alone. A remote rides <see cref="TileWorldClientConfig.InterpolationDelayTicks"/>, so its DIVERGENCE
    /// is that delay on top. Measured against each path's own committed tile, which is what the invariant is
    /// stated against, the two land on the same number.</para>
    /// </summary>
    [Fact]
    public void The_local_body_and_a_remote_lag_their_own_committed_tiles_by_the_same_amount()
    {
        using var loop = new Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.N));
        loop.Frames(20);

        loop.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 30, 0), TileMoveMode.Run));
        loop.Server.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(12, 30, 0), TileMoveMode.Run));

        double localSum = 0d, remoteSum = 0d;
        int localSamples = 0, remoteSamples = 0;
        for (int i = 0; i < 200; i++)
        {
            loop.Step();
            TileMoveState p = loop.Client.Prediction.PredictedState;
            // Steady state only: skip the first and last few tiles, where the body is starting or settling.
            if (p.Tile.Z > 14 && p.Tile.Z < 26)
            {
                localSum += p.Tile.Z - DrawnTileZ(loop.LocalDrawn.Z);
                localSamples++;
            }
            if (!loop.Client.TryGetRemotePose(remote, out TilePose pose)) continue;
            // The remote's OWN committed tile, off the replicated state the client is drawing it from, so the lag
            // below is measured against the same thing the local one is: what the rules say, on that body's own
            // timeline. Reading the SERVER's live tile instead would fold the interpolation delay in and measure
            // a different quantity.
            if (!loop.Client.View.TryGetEntity(remote, out Entity e)) continue;
            if (!loop.Client.World.TryGet(e, out TileMoveState rs)) continue;
            if (rs.Tile.Z <= 14 || rs.Tile.Z >= 26) continue;
            remoteSum += rs.Tile.Z - DrawnTileZ(pose.Position.Z);
            remoteSamples++;
        }

        Assert.True(localSamples > 40 && remoteSamples > 40,
            $"not enough steady-state frames: local {localSamples}, remote {remoteSamples}");
        float expected = 1f / RunStep * H / MathF.Log(2f);
        float local = (float)(localSum / localSamples), remoteLag = (float)(remoteSum / remoteSamples);
        Assert.True(MathF.Abs(local - expected) < expected * 0.15f, $"local lag {local} against {expected}");
        Assert.True(MathF.Abs(remoteLag - expected) < expected * 0.15f, $"remote lag {remoteLag} against {expected}");
        Assert.True(MathF.Abs(local - remoteLag) < expected * 0.15f,
            $"local lag {local} and remote lag {remoteLag} are not the same feel");
    }

    /// <summary>
    /// Same code, same knob, stated as bytes: two chases built at one half life and fed the same targets over the
    /// same frames agree exactly. This is what makes the test above a claim about the CLIENT's wiring rather than
    /// about the chase, and it is what a game building a chase of its own off
    /// <see cref="TileWorldClient.ChaseHalfLifeSeconds"/> is relying on.
    /// </summary>
    [Fact]
    public void Two_chases_on_one_half_life_are_bit_identical()
    {
        using var loop = new Loop();
        var a = new TileChase(loop.Client.ChaseHalfLifeSeconds);
        var b = new TileChase(loop.Client.ChaseHalfLifeSeconds);
        Assert.Equal(TileChase.DefaultHalfLifeSeconds, loop.Client.ChaseHalfLifeSeconds);

        var rng = new Random(20260826);
        var target = new Vector2(3f, 4f);
        for (int i = 0; i < 500; i++)
        {
            if (i % 17 == 0) target += new Vector2(1f, rng.Next(-1, 2));
            float dt = 0.004f + (float)rng.NextDouble() * 0.03f;
            Assert.Equal(a.Advance(target, dt), b.Advance(target, dt));
        }
        Assert.NotEqual(new Vector2(3f, 4f), a.Drawn);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The chase's own edges.
    // ---------------------------------------------------------------------------------------------------------

    // Zero is the strictest reading of the invariant: the body is on its committed tile the instant the tile
    // commits, so the picture never disagrees with the rules at all. A game that wants no visual truth gap sets
    // this, and it must not be reachable by accident, which is why the two ways of failing to name a duration
    // throw instead.
    [Fact]
    public void A_zero_half_life_draws_the_body_on_its_target_at_once()
    {
        var snap = new TileChase(0f);
        snap.SnapTo(Vector2.Zero);
        Assert.Equal(new Vector2(9f, 4f), snap.Advance(new Vector2(9f, 4f), Frame));
        Assert.Equal(new Vector2(9f, 4f), snap.Drawn);
    }

    [Fact]
    public void A_chase_refuses_a_half_life_that_is_not_a_finite_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileChase(-0.01f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileChase(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileChase(float.PositiveInfinity));
        // And the client's own door, so a config that names a half life the chase refuses fails at construction
        // rather than on the first frame that tries to draw with it.
        var hub = new InMemoryTransportHub();
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileWorldClient(hub.CreateClient(),
            new TileWorldClientConfig
            {
                TickSeconds = Tick,
                StepTicks = new TileStepTicks(4, 2),
                ChaseHalfLifeSeconds = float.PositiveInfinity,
            }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld())));
    }

    // The first target PLACES the body rather than being chased from the origin, so a body's first drawn frame is
    // never a slide in from tile (0, 0) across the whole map.
    [Fact]
    public void A_chase_places_its_body_on_the_first_target_rather_than_chasing_from_the_origin()
    {
        var chase = new TileChase(H);
        Assert.False(chase.IsPlaced);
        Assert.Equal(new Vector2(40f, 90f), chase.Advance(new Vector2(40f, 90f), Frame));
        Assert.True(chase.IsPlaced);
    }

    // A frame in which no time passed moves nothing, which is the honest answer and also what keeps a head that
    // draws twice in one frame from drawing two different positions.
    [Fact]
    public void A_frame_of_no_time_moves_nothing()
    {
        var chase = new TileChase(H);
        chase.SnapTo(Vector2.Zero);
        var target = new Vector2(0f, 5f);
        Assert.Equal(Vector2.Zero, chase.Advance(target, 0f));
        Assert.Equal(Vector2.Zero, chase.Advance(target, -1f));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Harness.
    // ---------------------------------------------------------------------------------------------------------

    // The most a single frame may move a chased body: the steady-state gap right after a commit, times the share
    // of a gap one frame closes. Both halves are the chase's own arithmetic, so this tracks the half life rather
    // than freezing a measurement of it.
    static float FrameBound(float stepTiles, float stepSeconds) =>
        stepTiles / (1f - MathF.Pow(2f, -stepSeconds / H)) * (1f - MathF.Pow(2f, -Frame / H));

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
        // placed until it does. A test that started before the seed would watch the chase cut from the default
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

        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }
}
