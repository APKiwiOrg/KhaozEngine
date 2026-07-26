using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The networked half of airborne horizontal momentum: the wire encoding of the carried arc, the reconcile basis it
/// has to survive, the sharded head's per-tick carry, and the anti-cheat check that momentum breaks unless it is
/// fixed alongside. Three cases decide whether the feature works at all.
/// <see cref="A_correction_mid_flight_replays_the_carried_arc_and_converges"/> is why
/// <see cref="MovementState.HorizontalVelocityXQ"/> exists: <see cref="PlayerMoveState.From"/> rebuilds the client's
/// basis from the replicated components ALONE and <c>Reconcile</c> overwrites the whole predicted state with it, so a
/// carried field missing from that seed resets to zero on every correction and the client drops an arc the server is
/// still flying. <see cref="The_sharded_head_carries_the_arc_across_ticks_and_onto_the_wire"/> is the same failure on
/// the head an MMO consumer actually runs, where the per-cell step rebuilds its <see cref="MoveState"/> from the
/// component every tick. <see cref="A_momentum_flight_with_released_input_reports_no_denial"/> is why
/// <see cref="MovementAnomaly"/> had to stop rebuilding its intended target from the command direction: under momentum
/// the direction of travel is the conserved velocity, so a player who lets go mid-air measured as a full-speed denial
/// on every single airborne tick.
/// </summary>
public class AirMomentumReplicationTests
{
    const float Dt = 1f / 30f;
    const float Quantum = MovementState.HorizontalVelocityQuantum;   // 1/256 m/s

    // The solid-box staircase ClimbSignalTests / StairGlideReconcileParityTests / ShardedClimbSignalExportTests already
    // share (grade 0.75, riser 0.30 / tread 0.40, climbing in -Z from the origin). Real Bepu geometry rather than a flat
    // fixture, so the flight below is swept against something that can actually deny it, tick by tick, on both heads.
    const float Riser = 0.30f, Tread = 0.40f;
    const int Risers = 33;

    static readonly Func<float, float, float> Ground = (x, z) => 0f;
    static readonly Func<float, float, float> FarBelow = (x, z) => -1000f;   // so a flight never lands
    static readonly Func<float, float, Vector3> Normal = (x, z) => Vector3.UnitY;

    static MoveTuning Momentum => MoveTuning.Default with { AirMomentum = true };

    // Camera yaw 0: forward is -Z, right is +X. The staircase climbs in -Z, so Backward travels DOWN it and out into
    // open air, which is what makes the launch below a real flight over real geometry rather than a hand-seeded one.
    static MoveCommand DownhillWalk => new(new Vector2(0f, -1f), run: false, cameraYaw: 0f);
    static MoveCommand DownhillJump => new(new Vector2(0f, -1f), run: true, cameraYaw: 0f, jump: true);
    static MoveCommand Right => new(new Vector2(1f, 0f), run: true, cameraYaw: 0f);
    // The same axis as Downhill, named for the fixture it drives: the wall below sits at +Z.
    static MoveCommand IntoTheWall => new(new Vector2(0f, -1f), run: true, cameraYaw: 0f);

    static void AddStairs(IPhysicsWorld world)
    {
        float backZ = -Tread * Risers - 2f;
        const float halfX = 20f;
        for (int i = 0; i < Risers; i++)
        {
            float treadTop = Riser * (i + 1);
            float centerZ = 0.5f * (-Tread * i + backZ);
            float depth = -Tread * i - backZ;
            world.AddStatic(new BoxShape(new Vector3(halfX, treadTop * 0.5f, depth * 0.5f)),
                Pose.At(new Vector3(0f, treadTop * 0.5f, centerZ)));
        }
    }

    // ---- Encoding: zero must be exactly zero, and the range must cap rather than wrap ----

    [Fact]
    public void Default_component_decodes_to_a_zero_velocity()
    {
        // Every grounded player carries these two shorts on every tick, the component is default-constructed at spawn
        // and whenever a TryGet misses, and a pre-momentum save decodes through the same path. Anything but an exact
        // zero here is a phantom drift applied to every player in the world, injected through the reconcile basis
        // where no gameplay code is looking.
        Assert.Equal(0f, MovementState.DecodeHorizontalVelocity(default(MovementState).HorizontalVelocityXQ));
        Assert.Equal(0f, MovementState.DecodeHorizontalVelocity(default(MovementState).HorizontalVelocityZQ));
        Assert.Equal(Vector2.Zero, PlayerMoveState.From(Vector3.Zero, default).Move.HorizontalVelocity);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f / 256f)]     // exactly one quantum
    [InlineData(-1f / 256f)]
    [InlineData(6f)]            // the default walk speed
    [InlineData(12f)]           // the default run speed
    [InlineData(-30f)]          // a hasted takeoff, the arc this feature exists to preserve
    [InlineData(96f)]           // RunSpeed * MaxSpeedScale, the fastest the replicated speed scale can produce
    [InlineData(MovementState.MaxHorizontalSpeed)]
    [InlineData(-MovementState.MaxHorizontalSpeed)]
    public void Round_trips_exactly_at_the_quantum(float v)
    {
        // 1/256 is a negative power of two, so the decode is an exact multiply and every one of these lands dead on
        // rather than a hair beside it. That exactness is what lets a corrected client replay the arc the server flew,
        // and it matters more here than on any other quantized field on the component: the carry FEEDS the next tick,
        // so a hair would compound for the whole length of the flight instead of washing out on the next frame.
        Assert.Equal(v, MovementState.DecodeHorizontalVelocity(MovementState.QuantizeHorizontalVelocity(v)));
    }

    [Fact]
    public void Out_of_range_requests_clamp_instead_of_wrapping()
    {
        // Wrapping is the failure that matters here. An int cast overflowing the short would REVERSE a hurtling arc
        // rather than cap it, so an absurd speed would fling the character backwards along its own flight instead of
        // merely capping how fast it goes.
        Assert.Equal(MovementState.MaxHorizontalSpeed,
            MovementState.DecodeHorizontalVelocity(MovementState.QuantizeHorizontalVelocity(5000f)));
        Assert.Equal(-MovementState.MaxHorizontalSpeed,
            MovementState.DecodeHorizontalVelocity(MovementState.QuantizeHorizontalVelocity(-5000f)));
        Assert.Equal(MovementState.MaxHorizontalSpeed,
            MovementState.DecodeHorizontalVelocity(MovementState.QuantizeHorizontalVelocity(float.PositiveInfinity)));
        Assert.Equal(-MovementState.MaxHorizontalSpeed,
            MovementState.DecodeHorizontalVelocity(MovementState.QuantizeHorizontalVelocity(float.NegativeInfinity)));
    }

    [Fact]
    public void NaN_encodes_as_carrying_nothing()
    {
        // The carry feeds the next tick, so a NaN reaching it would not corrupt one frame: it would strand the
        // character for the rest of the session. Zero is the only safe reading and it is what a pre-momentum state
        // carries anyway, so the degenerate case lands on the degenerate-but-playable state.
        Assert.Equal(0, MovementState.QuantizeHorizontalVelocity(float.NaN));
        Assert.Equal(0f, MovementState.DecodeHorizontalVelocity(MovementState.QuantizeHorizontalVelocity(float.NaN)));
    }

    // ---- The two round trips: through the component pair, and through the wire codec ----

    [Fact]
    public void Survives_the_component_round_trip_in_both_directions()
    {
        var state = new PlayerMoveState();
        state.Move.HorizontalVelocity = new Vector2(12.5f, -30.25f);   // both exact at the quantum
        MovementState wire = MovementState.From(state);
        PlayerMoveState back = PlayerMoveState.From(Vector3.Zero, wire);
        Assert.Equal(new Vector2(12.5f, -30.25f), back.Move.HorizontalVelocity);
    }

    [Fact]
    public void The_carried_arc_round_trips_through_the_movement_codec()
    {
        MovementState back = RoundTrip(new MovementState
        {
            VerticalVelocity = -1.5f,
            Grounded = false,
            TeleportEpoch = 42u,
            ClimbRateQ = MovementState.QuantizeClimbRate(-0.5f),
            SpeedScaleQ = MovementState.QuantizeSpeedScale(2.5f),
            HorizontalVelocityXQ = MovementState.QuantizeHorizontalVelocity(18.75f),
            HorizontalVelocityZQ = MovementState.QuantizeHorizontalVelocity(-23.5f),
        });

        Assert.Equal(18.75f, MovementState.DecodeHorizontalVelocity(back.HorizontalVelocityXQ));
        Assert.Equal(-23.5f, MovementState.DecodeHorizontalVelocity(back.HorizontalVelocityZQ));
        // Every field already on the codec still round-trips alongside the two new ones. The two shorts were appended
        // at the END of both lambdas, and a write and read that fell out of order would not fail loudly: it would
        // decode as plausible garbage, which is exactly what this pins.
        Assert.Equal(-1.5f, back.VerticalVelocity, 5);
        Assert.False(back.Grounded);
        Assert.Equal(42u, back.TeleportEpoch);
        Assert.Equal(-0.5f, MovementState.DecodeClimbRate(back.ClimbRateQ), 5);
        Assert.Equal(2.5f, MovementState.DecodeSpeedScale(back.SpeedScaleQ));
    }

    [Fact]
    public void Wire_generation_bumped_for_the_carried_arc()
    {
        // MovementState is a built-in and is NOT length-prefixed, so an older client cannot skip the four new bytes.
        // The generation is what turns that into a clean IncompatibleVersion rejection at connect instead of a
        // misparse that reads the arc's bytes as somebody else's field. Pinned as ">= 7" rather than "== 7" (the
        // shape PlayerMoveSwimTests and TeleportEpochTests already use): this feature's claim is that the generation
        // MOVED for it, and a later built-in change moves it again without weakening that.
        Assert.True(MoveProtocol.WireProtocolVersion >= 7);
    }

    // ---- Acceptance 2 and 4: a correction mid-flight, on real geometry, must converge ----

    readonly record struct Frame(Vector3 Pos, Vector2 Carried, bool Grounded);

    static Frame Snap(in PlayerMoveState s) => new(s.Position, s.Move.HorizontalVelocity, s.Grounded);

    // A continuous (lag-free) authoritative chain and the SAME chain driven through ClientPrediction with a periodic
    // reconcile at ack lag `lag`, exactly as WorldClient does it: the basis is rebuilt from the replicated components
    // through MovementState.From + PlayerMoveState.From, so anything missing from that seed is missing from the replay.
    // Both share one static Bepu world (queried read-only), so matching physics means the ONLY thing that can separate
    // the two streams is what the basis failed to carry.
    static (List<Frame> cont, List<Frame> recon, List<MovementState> wire) DriveContinuousAndReconciled(
        MoveCommand flight, int lag, int ticks)
    {
        MoveTuning t = Momentum;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world);
        world.Step(Dt);
        var sim = new PlayerMoveSimulator(Ground, t, Normal, physics: world);

        // Standing on the TOP tread facing down the staircase. Tick 0 is a running jump off the top, so the takeoff
        // speed is the grounded run speed BY CONSTRUCTION (the jump fires at the end of the step) and everything after
        // it is a real flight out over the descending treads rather than a hand-seeded velocity.
        var seed = new PlayerMoveState
        {
            Move = new MoveState
            {
                Position = new Vector3(0f, Riser * Risers + t.CapsuleHalfHeight, -14f),
                Grounded = true,
            },
        };
        MoveCommand Cmd(int i) => i == 0 ? DownhillJump : flight;

        var contStates = new List<PlayerMoveState> { seed };
        for (int j = 0; j < ticks; j++) contStates.Add(sim.Step(contStates[j], Cmd(j), Dt));

        var cont = new List<Frame>();
        var wire = new List<MovementState>();
        for (int j = 1; j <= ticks; j++) { cont.Add(Snap(contStates[j])); wire.Add(MovementState.From(contStates[j])); }

        var settings = PredictionSettings.Default with { TickSeconds = Dt };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);
        pred.Reset(seed);
        var recon = new List<Frame>();
        for (int i = 0; i < ticks; i++)
        {
            pred.Predict(Cmd(i));   // returns seq i
            if (i >= lag)
            {
                // The authoritative state that has folded command (i - lag), rebuilt the only way a client can rebuild
                // it: through the replicated components. Pending = seqs (i - lag + 1 .. i), replayed on top.
                int ackSeq = i - lag;
                PlayerMoveState authFull = contStates[ackSeq + 1];
                PlayerMoveState basis = PlayerMoveState.From(authFull.Position, MovementState.From(authFull));
                pred.Reconcile(i, basis, ackSeq);
            }
            recon.Add(Snap(pred.PredictedState));
        }
        return (cont, recon, wire);
    }

    // Two flights, both launched by a RUN jump so the arc leaves the ground at RunSpeed. The command underneath is
    // then either released or a WALK, and the walk case is deliberate rather than incidental: holding a RUN down the
    // same axis at full air control commands exactly the speed the arc is already carrying, so momentum becomes
    // invisible and a basis that dropped the carry replays the identical flight. That variant passes with the seed
    // deleted, which makes it worth nothing as a guard. A slower command is the honest held case, and it is also the
    // design's headline one: the arc outlives the command that is no longer fast enough to produce it.
    [Theory]
    [InlineData(true)]    // input RELEASED mid-flight: the carried arc is the only thing moving the character
    [InlineData(false)]   // input held at WALK under a RUN takeoff: the arc outruns its own command
    public void A_correction_mid_flight_replays_the_carried_arc_and_converges(bool releaseInput)
    {
        const int Lag = 4;   // ~130 ms RTT at 30 Hz, the same realistic short window the stair-glide parity test uses
        const int Ticks = 34;
        var (cont, recon, wire) = DriveContinuousAndReconciled(releaseInput ? MoveCommand.Idle : DownhillWalk, Lag, Ticks);
        string tag = releaseInput ? "released" : "held at walk";

        // Harness validity. If the fixture is not actually flying at speed with a carried arc on the wire, the
        // convergence assertion below is green for the wrong reason and pins nothing.
        int airborne = 0;
        float peakCarried = 0f, peakWire = 0f;
        for (int i = 0; i < Ticks; i++)
        {
            if (!cont[i].Grounded) airborne++;
            peakCarried = MathF.Max(peakCarried, cont[i].Carried.Length());
            peakWire = MathF.Max(peakWire, new Vector2(
                MovementState.DecodeHorizontalVelocity(wire[i].HorizontalVelocityXQ),
                MovementState.DecodeHorizontalVelocity(wire[i].HorizontalVelocityZQ)).Length());
        }
        Assert.True(airborne > 25, $"{tag}: the fixture spent only {airborne} of {Ticks} ticks airborne");
        Assert.True(peakCarried > 10f, $"{tag}: the flight carried only {peakCarried} m/s, too slow to prove anything");
        Assert.True(peakWire > 10f, $"{tag}: the carried arc never reached the wire (peak {peakWire} m/s)");

        // The convergence itself. With the arc seeded from the wire the replay reproduces the authoritative flight.
        // Without it every correction resets the carry to zero and the replayed window travels Lag ticks' worth of
        // nothing (released) or of walk speed (held), which is metres rather than millimetres.
        float maxPosErr = 0f, maxCarryGap = 0f;
        for (int i = 0; i < Ticks; i++)
        {
            maxPosErr = MathF.Max(maxPosErr, Vector3.Distance(recon[i].Pos, cont[i].Pos));
            maxCarryGap = MathF.Max(maxCarryGap, (recon[i].Carried - cont[i].Carried).Length());
        }
        string metrics = $"{tag} (lag {Lag}): maxPosErr={maxPosErr * 1000:F2} mm, maxCarryGap={maxCarryGap * 1000:F2} mm/s, " +
                         $"peakCarried={peakCarried:F2} m/s over {airborne} airborne ticks";

        Assert.True(maxPosErr < 0.05f, metrics + " - the replayed flight diverged from the authoritative one");
        // The only legitimate gap is the wire quantum itself, applied per axis and re-applied at every reconcile.
        Assert.True(maxCarryGap < 8f * Quantum, metrics + " - the replayed carry is more than a few quanta off");
    }

    // ---- The sharded head: the per-cell step rebuilds MoveState from the component every tick ----

    [Fact]
    public void The_sharded_head_carries_the_arc_across_ticks_and_onto_the_wire()
    {
        // ShardedWorldServer's per-cell PlayerMovementSystem reconstructs a fresh MoveState from the MovementState
        // component on every tick, so the carry has to be read IN and written back OUT there or it does not exist on
        // that head at all. Both halves fail the same way and both are covered here: the arc is steered by a
        // perpendicular command at half air control, so a carry that is not persisted (frozen at the spawn value) and
        // a carry that is not read back (re-derived from zero) both diverge from the reference immediately.
        MoveTuning t = Momentum with { AirControl = 0.5f };
        var sys = new PlayerMovementSystem(Ground, t, Normal, bounds: null, physics: null, medium: null);
        var ecs = new World();
        Entity e = ecs.Spawn();
        var seed = new Vector3(0f, t.CapsuleHalfHeight, 0f);
        ecs.Set(e, new NetId(1));
        ecs.Set(e, new ReplicatedPosition { Value = seed });
        ecs.Set(e, new MovementState { Grounded = true });
        ecs.Set(e, new PendingMove { Command = DownhillJump });

        // The single-World head reference: the full MoveState carried tick to tick, as WorldServer keeps it per slot.
        var refState = new MoveState { Position = seed, Grounded = true };

        const int Ticks = 20;
        float maxCarryGap = 0f, maxPosErr = 0f, peakTurn = 0f;
        for (int i = 0; i < Ticks; i++)
        {
            MoveCommand cmd = i == 0 ? DownhillJump : Right;   // launch down the axis, then steer across it
            ecs.Set(e, new PendingMove { Command = cmd });
            refState = CharacterMovement.Step(refState, cmd, Dt, Ground, t, Normal);
            sys.Update(ecs, Dt);

            MovementState ms = ecs.Get<MovementState>(e);
            var sharded = new Vector2(
                MovementState.DecodeHorizontalVelocity(ms.HorizontalVelocityXQ),
                MovementState.DecodeHorizontalVelocity(ms.HorizontalVelocityZQ));
            maxCarryGap = MathF.Max(maxCarryGap, (sharded - refState.HorizontalVelocity).Length());
            maxPosErr = MathF.Max(maxPosErr, Vector3.Distance(ecs.Get<ReplicatedPosition>(e).Value, refState.Position));
            peakTurn = MathF.Max(peakTurn, refState.HorizontalVelocity.X);
        }

        Assert.False(refState.Grounded, "the fixture landed early, so most of it never exercised the airborne carry");
        Assert.True(peakTurn > 8f, $"the reference arc barely turned ({peakTurn} m/s across), so the carry is untested");
        // The two heads cannot be BIT-identical the way the climb signal is: this head runs its simulation through the
        // wire quantum every tick where the single-World head keeps full float precision, so the honest bar is the
        // quantum rather than zero. It is still three orders of magnitude below the failure, which parks the arc at
        // zero or at the spawn value for the entire flight.
        Assert.True(maxCarryGap < 4f * Quantum,
            $"sharded carried arc diverged {maxCarryGap * 1000:F2} mm/s from the single-World head");
        Assert.True(maxPosErr < 0.01f, $"sharded position diverged {maxPosErr * 1000:F2} mm from the single-World head");
    }

    // ---- Acceptance 5: the anomaly check must not read a legitimate arc as a denial ----

    static AntiCheatConfig Cfg => new() { MaxCorrectionDistance = 0.25f, CorrectionStreak = 3 };

    // What the PRE-FIX check would have reported on this tick: the intended target rebuilt from the COMMAND direction
    // plus the exported scalar speed. The scalar overload is still public and still correct for a non-momentum caller,
    // so the regression can be pinned here permanently rather than only in a one-off experiment.
    static float OldCommandDirectionForm(in PlayerMoveState prev, in MoveCommand cmd, in PlayerMoveState after)
        => Vector2.Distance(
            CharacterMovement.IntendedHorizontalTargetAtSpeed(prev.Position, cmd, Dt, after.Move.CommandedSpeed),
            new Vector2(after.Position.X, after.Position.Z));

    [Fact]
    public void A_momentum_flight_with_released_input_reports_no_denial()
    {
        // The failure this fix exists for. The check used to rebuild the intended target from the COMMAND direction,
        // which collapses back onto the capsule the moment the player lets go, so every airborne tick of a legitimate
        // 30 m/s arc measured as a full stride of denial and the streak reported an ordinary jump as speed hacking.
        var sim = new PlayerMoveSimulator(FarBelow, Momentum);
        var prev = new PlayerMoveState
        {
            Move = new MoveState
            {
                Position = new Vector3(0f, 60f, 0f), Grounded = false, TimeSinceGrounded = 1f,
                HorizontalVelocity = new Vector2(18f, -24f),   // 30 m/s, off-axis so a direction bug cannot hide
            },
        };

        var streaks = new Dictionary<int, int>();
        float worst = 0f, worstOld = 0f;
        for (int i = 0; i < 60; i++)
        {
            PlayerMoveState after = sim.Step(prev, MoveCommand.Idle, Dt);
            float correction = MovementAnomaly.CorrectionDistance(prev, after, Dt);
            worst = MathF.Max(worst, correction);
            worstOld = MathF.Max(worstOld, OldCommandDirectionForm(prev, MoveCommand.Idle, after));

            Assert.True(correction <= 1e-3f, $"tick {i} read a {correction} m denial on an undenied free-flight tick");
            Assert.False(MovementAnomaly.RegisterCorrection(streaks, 0, correction, Cfg),
                $"tick {i} raised the anomaly streak on a legitimate momentum flight");
            prev = after;
        }

        Assert.True(prev.Move.HorizontalVelocity.Length() > 29f,
            $"the fixture stopped flying, carrying {prev.Move.HorizontalVelocity}");
        // The fixture has to actually exercise the regression, or the green above means nothing.
        Assert.True(worstOld > Cfg.MaxCorrectionDistance,
            $"the old command-direction form only read {worstOld} m, so this fixture does not reproduce the bug");
        Assert.True(worstOld > 100f * worst,
            $"old form {worstOld} m vs new form {worst} m: the gap is too small to be the fix rather than noise");
    }

    [Fact]
    public void A_momentum_flight_denied_by_a_wall_is_still_reported()
    {
        // The fix must not blind the detector: reading the exported velocity instead of the command direction changes
        // WHERE the intended target is, never whether a denial counts. A momentum flight driven into real geometry is
        // still measured at the full magnitude of the stride it was denied, and still raises the streak. The input is
        // HELD into the wall, which is what a client fighting a constraint actually looks like. Releasing it produces
        // one denied tick and then silence, correctly: the wall clips the carry to zero, so from the next tick on the
        // character is intending nothing and there is nothing left to deny.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var v = new[]
        {
            new Vector3(-20f, -10f, 8f), new Vector3(20f, -10f, 8f),
            new Vector3(20f, 20f, 8f), new Vector3(-20f, 20f, 8f),
        };
        world.AddStatic(new TriangleMeshShape(v, new[] { 0, 2, 1, 0, 3, 2 }), Pose.At(Vector3.Zero));
        world.Step(Dt);

        var sim = new PlayerMoveSimulator(FarBelow, Momentum with { Gravity = 0f }, Normal, physics: world);
        var prev = new PlayerMoveState
        {
            Move = new MoveState
            {
                Position = new Vector3(0f, 3f, 0f), Grounded = false, TimeSinceGrounded = 1f,
                HorizontalVelocity = new Vector2(0f, 20f),   // flying head-on at the wall at 20 m/s
            },
        };

        var streaks = new Dictionary<int, int>();
        bool raised = false;
        float worst = 0f;
        for (int i = 0; i < 60 && !raised; i++)
        {
            PlayerMoveState after = sim.Step(prev, IntoTheWall, Dt);
            float correction = MovementAnomaly.CorrectionDistance(prev, after, Dt);
            worst = MathF.Max(worst, correction);
            raised = MovementAnomaly.RegisterCorrection(streaks, 0, correction, Cfg);
            prev = after;
        }

        Assert.True(prev.Position.Z < 8f, $"the flight tunnelled the wall, z={prev.Position.Z}");
        Assert.True(raised, $"a momentum flight pinned against a wall must still raise the signal (worst {worst} m)");
    }

    // ---- Harness ----

    static MovementState RoundTrip(MovementState src)
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var serverWorld = new World();
        Entity e = serverWorld.Spawn();
        serverWorld.Set(e, new NetId(1));
        serverWorld.Set(e, new ReplicatedPosition { Value = Vector3.Zero });
        serverWorld.Set(e, src);
        byte[] snapshot = SnapshotWriter.Write(serverWorld, registry);

        var view = new ClientReplicationView(registry);
        var clientWorld = new World();
        view.Apply(clientWorld, snapshot);
        Assert.True(view.TryGetEntity(1, out Entity ce));
        Assert.True(clientWorld.TryGet(ce, out MovementState back));
        return back;
    }
}
