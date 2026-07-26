using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The networked half of the per-entity speed scale: the wire encoding, the server setter, the reconcile basis, and
/// the anti-cheat correction check. Two cases decide whether the feature works at all.
/// <see cref="A_correction_mid_boost_replays_the_pending_window_at_the_boosted_speed"/> is why the scale rides
/// <see cref="MovementState"/> instead of living only on the sim-local <see cref="MoveState"/>:
/// <see cref="PlayerMoveState.From"/> rebuilds the client's basis from the replicated components ALONE, so a
/// sim-local scale would reset on every correction. <see cref="The_anomaly_check_does_not_flag_a_boosted_player"/>
/// is why <see cref="MovementAnomaly"/> had to learn about it: an intended-target calculation blind to the boost
/// reports a legitimately hasted player as a speed hacker within a few ticks.
/// </summary>
public class SpeedScaleReplicationTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    const float Dt = 1f / 30f;
    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    // ---- Encoding: the default must be exactly 1, and a root exactly 0 ----

    [Fact]
    public void Default_component_decodes_to_exactly_unmodified()
    {
        // Every unboosted player carries this byte on every tick, and the component is default-constructed at spawn
        // and whenever a TryGet misses. Anything but an exact 1 here is a silent global speed change.
        Assert.Equal(1f, MovementState.DecodeSpeedScale(default(MovementState).SpeedScaleQ));
        Assert.Equal(0, MovementState.QuantizeSpeedScale(1f));
    }

    [Theory]
    [InlineData(0f)]        // root: exactly zero, not a slow crawl
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(5f)]        // the speed boost this feature was requested for
    [InlineData(8f)]        // MaxSpeedScale
    public void Round_trips_exactly_at_the_quantum(float scale)
    {
        // The 1/16 quantum is a negative power of two, so every one of these lands dead on with no float slop, which
        // is what lets the server sim and the client replay agree bit-exactly rather than merely closely.
        Assert.Equal(scale, MovementState.DecodeSpeedScale(MovementState.QuantizeSpeedScale(scale)));
    }

    [Fact]
    public void Out_of_range_requests_clamp_instead_of_wrapping()
    {
        Assert.Equal(0f, MovementState.DecodeSpeedScale(MovementState.QuantizeSpeedScale(-5f)));
        Assert.Equal(MovementState.MaxSpeedScale, MovementState.DecodeSpeedScale(MovementState.QuantizeSpeedScale(99f)));
        Assert.Equal(1f, MovementState.DecodeSpeedScale(MovementState.QuantizeSpeedScale(float.NaN)));
        // Defence-in-depth on the READ side: a corrupt or hostile frame must never decode to a negative multiplier,
        // which would drive the character backwards against its own command.
        Assert.Equal(0f, MovementState.DecodeSpeedScale(-127));
    }

    [Fact]
    public void Survives_the_component_round_trip_in_both_directions()
    {
        var state = new PlayerMoveState();
        state.Move.SpeedScale = 2.5f;
        MovementState wire = MovementState.From(state);
        PlayerMoveState back = PlayerMoveState.From(Vector3.Zero, wire);
        Assert.Equal(2.5f, back.Move.SpeedScale);
    }

    // ---- End to end: setter -> authoritative sim -> codec -> wire -> client basis -> prediction ----

    [Fact]
    public void SetSpeedScale_reaches_the_client_and_its_prediction()
    {
        (WorldServer server, WorldClient client, int slot) = Connect();

        // Baseline: the client predicts at walk pace.
        for (int i = 0; i < 10; i++) Frame(server, client, Forward);
        float basePace = client.LocalHorizontalSpeed;
        Assert.Equal(MoveTuning.Default.WalkSpeed, basePace, 1);

        server.SetSpeedScale(PlayerRef.Slot(slot), 4f);
        for (int i = 0; i < 10; i++) Frame(server, client, Forward);

        // The whole chain: the byte survived the codec, the client rebuilt its basis from it, and its prediction is
        // running boosted rather than fighting the server every tick.
        Assert.True(client.TryGetComponent(client.LocalNetId, out MovementState ms));
        Assert.Equal(4f, MovementState.DecodeSpeedScale(ms.SpeedScaleQ));
        Assert.Equal(MoveTuning.Default.WalkSpeed * 4f, client.LocalHorizontalSpeed, 1);
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState authoritative));
        Assert.Equal(4f, authoritative.Move.SpeedScale);
    }

    [Fact]
    public void SetSpeedScale_back_to_one_ends_the_boost()
    {
        // The engine owns no duration, so this is how a game expires a buff. It has to be exactly reversible.
        (WorldServer server, WorldClient client, int slot) = Connect();
        server.SetSpeedScale(PlayerRef.Slot(slot), 4f);
        for (int i = 0; i < 10; i++) Frame(server, client, Forward);

        server.SetSpeedScale(PlayerRef.Slot(slot), 1f);
        for (int i = 0; i < 10; i++) Frame(server, client, Forward);

        Assert.True(client.TryGetComponent(client.LocalNetId, out MovementState ms));
        Assert.Equal(0, ms.SpeedScaleQ);                              // exactly the unmodified default again
        Assert.Equal(MoveTuning.Default.WalkSpeed, client.LocalHorizontalSpeed, 1);
    }

    [Fact]
    public void SetSpeedScale_quantizes_before_the_sim_sees_it()
    {
        // The server must never run a speed it cannot tell its clients about: a requested 1.1x becomes 1.125x on BOTH
        // heads rather than 1.1x authoritative against 1.125x predicted, which would drift for the whole buff.
        (WorldServer server, WorldClient client, int slot) = Connect();
        server.SetSpeedScale(PlayerRef.Slot(slot), 1.1f);
        for (int i = 0; i < 6; i++) Frame(server, client, Forward);

        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState authoritative));
        Assert.True(client.TryGetComponent(client.LocalNetId, out MovementState ms));
        Assert.Equal(1.125f, authoritative.Move.SpeedScale);
        Assert.Equal(authoritative.Move.SpeedScale, MovementState.DecodeSpeedScale(ms.SpeedScaleQ));
    }

    [Fact]
    public void The_sharded_head_applies_the_same_setter()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new ShardedWorldServerConfig { TickSeconds = Dt, MaxPlayers = 8 };
        var server = new ShardedWorldServer(st, config, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire("alice"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(Dt); }
        int slot = server.JoinedSlots.First();

        server.SetSpeedScale(PlayerRef.Account("alice"), 3f);
        for (int i = 0; i < 4; i++) { server.Poll(); server.Tick(Dt); }

        // The sharded head keeps its authoritative state in the owning cell's components, so this also proves the
        // per-cell PlayerMovementSystem is reading the field back out rather than stepping at base speed.
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState s));
        Assert.Equal(3f, s.Move.SpeedScale);
    }

    // ---- The reconcile case the whole shape was chosen for ----

    [Fact]
    public void A_correction_mid_boost_replays_the_pending_window_at_the_boosted_speed()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var settings = PredictionSettings.Default with { TickSeconds = Dt };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);

        // The server has hasted this player. The client learns it the only way it can: through the replicated
        // MovementState it rebuilds its basis from.
        var hasted = new MovementState { Grounded = true, SpeedScaleQ = MovementState.QuantizeSpeedScale(4f) };
        PlayerMoveState basis = PlayerMoveState.From(Vector3.Zero, hasted);
        pred.Reset(basis);

        const int Pending = 3;
        for (int i = 0; i < Pending; i++) pred.Predict(Forward);

        // A correction arrives with none of those commands acked, so all three replay on top of the basis.
        pred.Reconcile(authoritativeTick: 1, basis, lastAcknowledgedSeq: -1);

        float boosted = MoveTuning.Default.WalkSpeed * 4f * Dt * Pending;
        Assert.Equal(-boosted, pred.PredictedState.Position.Z, 4);
        Assert.Equal(4f, pred.PredictedState.Move.SpeedScale);
    }

    [Fact]
    public void The_basis_treats_a_missing_component_as_unmodified()
    {
        // WorldClient reads `world.TryGet(local, out MovementState ms)` and uses the default on a miss (before the
        // first replicated snapshot lands). That path must yield a normal-speed player, not a frozen one.
        Assert.Equal(1f, PlayerMoveState.From(Vector3.Zero, default).Move.SpeedScale);
    }

    // ---- Anti-cheat: the boost must not read as a correction, and must not become an exemption ----

    [Fact]
    public void The_anomaly_check_does_not_flag_a_boosted_player()
    {
        // The calibration this feature has to survive: MaxCorrectionDistance 0.25 against ~0.2 m of run travel per
        // tick. A 5x boost steps ~1 m/tick, so an intended-target calculation blind to the scale reports a ~0.8 m
        // correction on EVERY tick and the streak reports a legitimate player as a speed hacker within a few frames.
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var run = new MoveCommand(new Vector2(0f, 1f), run: true, cameraYaw: 0f);
        var prev = new PlayerMoveState();
        prev.Move.Position = new Vector3(0f, MoveTuning.Default.CapsuleHalfHeight, 0f);
        prev.Move.Grounded = true;
        prev.Move.SpeedScale = 5f;

        var cfg = new AntiCheatConfig { MaxCorrectionDistance = 0.25f, CorrectionStreak = 3 };
        var streaks = new Dictionary<int, int>();
        for (int i = 0; i < 30; i++)
        {
            PlayerMoveState after = sim.Step(prev, run, Dt);
            float correction = MovementAnomaly.CorrectionDistance(prev, after, Dt);
            Assert.True(correction <= cfg.MaxCorrectionDistance, $"tick {i} read as a {correction} m correction");
            Assert.False(MovementAnomaly.RegisterCorrection(streaks, 0, correction, cfg));
            prev = after;
        }
    }

    [Fact]
    public void The_anomaly_check_still_fires_for_a_boosted_player_fighting_a_bound()
    {
        // The scale must not become a blanket exemption: a boosted client driving into an authoritative constraint is
        // still denied every tick, and the streak still raises.
        var bounds = new CircleBounds(Vector2.Zero, 1f);
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, bounds: bounds);
        var run = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f);   // straight at the +Z edge
        var prev = new PlayerMoveState();
        prev.Move.Position = new Vector3(0f, MoveTuning.Default.CapsuleHalfHeight, 1f);
        prev.Move.Grounded = true;
        prev.Move.SpeedScale = 5f;

        var cfg = new AntiCheatConfig { MaxCorrectionDistance = 0.25f, CorrectionStreak = 3 };
        var streaks = new Dictionary<int, int>();
        bool raised = false;
        for (int i = 0; i < 10 && !raised; i++)
        {
            PlayerMoveState after = sim.Step(prev, run, Dt);
            float correction = MovementAnomaly.CorrectionDistance(prev, after, Dt);
            raised = MovementAnomaly.RegisterCorrection(streaks, 0, correction, cfg);
            prev = after;
        }
        Assert.True(raised, "a boosted client pinned against the play-area bound should still raise the signal");
    }

    // ---- Harness ----

    static (WorldServer server, WorldClient client, int slot) Connect()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = Dt, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt });
        for (int i = 0; i < 20 && !client.Joined; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, server.JoinedSlots.First());
    }

    static void Frame(WorldServer server, WorldClient client, in MoveCommand cmd)
    {
        client.SendInput(cmd);
        server.Poll();
        server.Tick(Dt);
        client.Poll();
    }
}
