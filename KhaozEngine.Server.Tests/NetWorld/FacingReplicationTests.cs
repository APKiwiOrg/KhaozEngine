using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The networked half of authoritative facing: the command flag's wire encoding (the run byte became a FLAGS byte,
/// which is half of wire generation 10), the quantized <see cref="MovementState.FacingYawQ"/> that carries the heading
/// to every client (the other half), and the carry on both server heads.
/// <para><see cref="MoveState.FacingYaw"/> is CARRIED state, not a per-tick event: a mid-turn has to survive
/// reconciliation, which is why it rides the wire where <c>LandingImpactSpeed</c> deliberately does not. That makes
/// this the <see cref="MovementState.HorizontalVelocityXQ"/> pattern end to end - the codec, the
/// <see cref="PlayerMoveState.From(System.Numerics.Vector3, in MovementState)"/> seed, and the sharded head's carry-in
/// AND carry-back-out, which is the half that is easy to forget because the single-<c>World</c> head keeps its whole
/// state per slot and needs neither.</para>
/// </summary>
public class FacingReplicationTests
{
    const float Dt = 1f / 30f;
    const float Quantum = MovementState.FacingYawQuantum;

    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static MoveCommand Face(float yaw) => new(Vector2.Zero, run: false, cameraYaw: yaw, jump: false, faceCamera: true);
    static MoveCommand Run => new(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: false);

    // ---- The move frame: the run byte is now a flags byte ----

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_move_frame_round_trips_every_flag_combination(bool run, bool faceCamera)
    {
        var cmd = new MoveCommand(new Vector2(0.25f, -0.5f), run, cameraYaw: 1.75f, jump: true, faceCamera: faceCamera);
        byte[] wire = MoveProtocol.EncodeMove(seq: 9, cmd);

        Assert.True(MoveProtocol.TryDecodeMove(wire, out int seq, out MoveCommand back));
        Assert.Equal(9, seq);
        Assert.Equal(run, back.Run);
        Assert.Equal(faceCamera, back.FaceCamera);
        Assert.True(back.Jump);
        Assert.Equal(cmd.Move, back.Move);
        Assert.Equal(1.75f, back.CameraYaw, 5);
    }

    [Fact]
    public void The_move_frame_is_still_eighteen_bytes()
    {
        // The whole point of packing the flag into the existing run byte. The client-to-server demux keys the move on
        // LENGTH 18 (MoveProtocol's aliasing contract, and EncodeGameMessage pads specifically to avoid landing on
        // it), so widening the frame would have silently re-routed every move through the game-message decode.
        Assert.Equal(18, MoveProtocol.EncodeMove(0, MoveCommand.Idle).Length);
        Assert.Equal(18, MoveProtocol.EncodeMove(0, Face(1f)).Length);
        // And a game message of the same natural length is still pushed off 18, so the two still cannot alias.
        byte[] msg = MoveProtocol.EncodeGameMessage(7, new byte[13]);
        Assert.NotEqual(18, msg.Length);
        Assert.True(MoveProtocol.TryDecodeGameMessage(msg, out ushort kind, out ReadOnlySpan<byte> payload));
        Assert.Equal(7, kind);
        Assert.Equal(13, payload.Length);
    }

    [Fact]
    public void Every_flag_byte_value_decodes_safely()
    {
        // A reverse-engineered client can put any bit pattern in that byte, including the 254 values that were never
        // a legal "run" bool. Unknown bits are IGNORED rather than rejected: the frame is still a well-formed move,
        // and rejecting it would let a client with a stray bit disappear from the sim instead of merely moving.
        byte[] wire = MoveProtocol.EncodeMove(seq: 3, new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f));
        for (int v = 0; v <= 255; v++)
        {
            wire[12] = (byte)v;
            Assert.True(MoveProtocol.TryDecodeMove(wire, out int seq, out MoveCommand back), $"flags byte {v} rejected");
            Assert.Equal(3, seq);
            Assert.Equal((v & 1) != 0, back.Run);
            Assert.Equal((v & 2) != 0, back.FaceCamera);
        }
    }

    // ---- The quantizer ----

    [Fact]
    public void Wire_generation_bumped_for_the_flags_byte_and_the_facing_field()
    {
        // One bump covers both halves. MovementState is a built-in and is NOT length-prefixed, so an older client
        // cannot skip the two new bytes, and the flags byte re-reads generation 9's run byte with a new meaning.
        // Pinned as ">= 10" rather than "== 10" (the shape the swim / teleport / momentum generations already use):
        // the claim is that the generation MOVED for this, and a later built-in change moves it again.
        Assert.True(MoveProtocol.WireProtocolVersion >= 10);
    }

    [Fact]
    public void A_default_component_decodes_to_the_default_heading()
    {
        // 0 is a legal heading (-Z, the camera-yaw-0 direction), not a sentinel, so a spawn, a missed TryGet and a
        // pre-facing save all read as facing forward rather than as facing nowhere.
        Assert.Equal(0f, MovementState.DecodeFacingYaw(default(MovementState).FacingYawQ));
        Assert.Equal(0f, PlayerMoveState.From(Vector3.Zero, default).Move.FacingYaw);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(1.5707964f)]     // pi/2
    [InlineData(-3.1415927f)]    // -pi, the closed end of the canonical range
    [InlineData(3.0f)]
    [InlineData(-3.0f)]
    public void Round_trips_within_half_a_quantum(float yaw)
    {
        float back = MovementState.DecodeFacingYaw(MovementState.QuantizeFacingYaw(yaw));
        Assert.True(MathF.Abs(CharacterMovement.WrapYaw(back - yaw)) <= 0.5f * Quantum + 1e-7f,
            $"{yaw} came back as {back}, more than half a quantum ({Quantum}) away");
    }

    [Fact]
    public void An_out_of_range_angle_wraps_rather_than_clamping()
    {
        // An angle has no out-of-range value, only a non-canonical representative, so the quantizer WRAPS. Clamping
        // would park a character that was handed 3*pi at the range's edge and leave it facing the wrong way for good,
        // and the value is carried state, so it would never self-correct.
        foreach (float yaw in new[] { MathF.PI, 3f * MathF.PI, -3f * MathF.PI, 7.5f, -7.5f, 100f })
        {
            float back = MovementState.DecodeFacingYaw(MovementState.QuantizeFacingYaw(yaw));
            Assert.InRange(back, -MathF.PI, MathF.PI);
            Assert.True(MathF.Abs(CharacterMovement.WrapYaw(back - yaw)) <= 0.5f * Quantum + 1e-4f,
                $"{yaw} wrapped to {back}, which is not the same heading");
        }
    }

    [Fact]
    public void NaN_encodes_as_the_default_heading()
    {
        // The heading feeds the next tick, so a NaN reaching it would strand the character's facing for the session
        // rather than corrupting one frame - the same reasoning that put the guard on the carried velocity.
        Assert.Equal(0, MovementState.QuantizeFacingYaw(float.NaN));
        Assert.Equal(0, MovementState.QuantizeFacingYaw(float.PositiveInfinity));
        Assert.Equal(0, MovementState.QuantizeFacingYaw(float.NegativeInfinity));
    }

    [Fact]
    public void Survives_the_component_round_trip_in_both_directions()
    {
        var state = new PlayerMoveState();
        state.Move.FacingYaw = 2.25f;
        MovementState wire = MovementState.From(state);
        PlayerMoveState back = PlayerMoveState.From(Vector3.Zero, wire);
        Assert.Equal(0f, MathF.Abs(CharacterMovement.WrapYaw(back.Move.FacingYaw - 2.25f)), 3);
    }

    [Fact]
    public void The_heading_round_trips_through_the_movement_codec()
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
            FacingYawQ = MovementState.QuantizeFacingYaw(-2.75f),
        });

        Assert.Equal(-2.75f, MovementState.DecodeFacingYaw(back.FacingYawQ), 3);
        // Every field already on the codec still round-trips alongside the new one. The short was appended at the END
        // of both lambdas, and a write and read that fell out of order would not fail loudly: it would decode as
        // plausible garbage, which is exactly what this pins.
        Assert.Equal(-1.5f, back.VerticalVelocity, 5);
        Assert.False(back.Grounded);
        Assert.Equal(42u, back.TeleportEpoch);
        Assert.Equal(-0.5f, MovementState.DecodeClimbRate(back.ClimbRateQ), 5);
        Assert.Equal(2.5f, MovementState.DecodeSpeedScale(back.SpeedScaleQ));
        Assert.Equal(18.75f, MovementState.DecodeHorizontalVelocity(back.HorizontalVelocityXQ));
        Assert.Equal(-23.5f, MovementState.DecodeHorizontalVelocity(back.HorizontalVelocityZQ));
    }

    // ---- The sharded head: the per-cell step rebuilds MoveState from the component every tick ----

    [Fact]
    public void The_sharded_cell_step_carries_the_heading_across_ticks_and_onto_the_wire()
    {
        // PlayerMovementSystem reconstructs a fresh MoveState from the MovementState component on every tick, so the
        // heading has to be read IN and written back OUT there or it does not exist on that head at all. Both halves
        // fail the same way: a heading that is not persisted is frozen at its spawn value, and one that is not read
        // back in re-derives from 0 every tick, so a FINITE turn rate (which needs the previous heading to know where
        // it is turning FROM) never gets anywhere. The rate is what makes this test able to see either failure.
        MoveTuning t = MoveTuning.Default with { FacingTurnSpeed = 2f };
        var sys = new PlayerMovementSystem(Flat, t);
        var ecs = new World();
        Entity e = ecs.Spawn();
        var seed = new Vector3(0f, t.CapsuleHalfHeight, 0f);
        ecs.Set(e, new NetId(1));
        ecs.Set(e, ReplicatedPosition.FromWorld(seed, WorldFrame.Origin));
        ecs.Set(e, new MovementState { Grounded = true });
        ecs.Set(e, new PendingMove { Command = MoveCommand.Idle });

        // The single-World head reference: the full MoveState carried tick to tick, as WorldServer keeps it per slot.
        var refState = new MoveState { Position = seed, Grounded = true };

        const int Ticks = 50;   // 2.5 rad at 2 rad/s is 1.25 s, i.e. 38 ticks, plus room to sit on the target
        float maxGap = 0f;
        for (int i = 0; i < Ticks; i++)
        {
            MoveCommand cmd = Face(2.5f);
            ecs.Set(e, new PendingMove { Command = cmd });
            refState = CharacterMovement.Step(refState, cmd, Dt, Flat, t);
            sys.Update(ecs, Dt);

            float sharded = MovementState.DecodeFacingYaw(ecs.Get<MovementState>(e).FacingYawQ);
            maxGap = MathF.Max(maxGap, MathF.Abs(CharacterMovement.WrapYaw(sharded - refState.FacingYaw)));
        }

        Assert.Equal(2.5f, refState.FacingYaw, 4);   // the reference completed the turn, so the fixture turned at all
        // The two heads cannot be BIT-identical: this one runs its carried heading through the wire quantum EVERY
        // tick where the single-World head keeps full float precision. And unlike a per-tick input, the error of a
        // carried integrator ACCUMULATES while the turn is in progress - the per-tick step is a fixed number of
        // radians, so its sub-quantum remainder rounds the same way every tick and adds up, bounded by half a quantum
        // per turning tick. It is still a fraction of a degree, and it UNWINDS: both heads land on the target exactly
        // once the last of the gap fits inside one step's budget, so the drift is a transient of the turn itself and
        // never a permanent offset between the two heads.
        Assert.True(maxGap < 0.5f * Ticks * Quantum + Quantum,
            $"the sharded heading diverged {maxGap} rad from the single-World head (quantum {Quantum})");
        Assert.True(maxGap < 0.01f, $"the sharded drift reached {maxGap} rad, past a fraction of a degree");
        float ended = MovementState.DecodeFacingYaw(ecs.Get<MovementState>(e).FacingYawQ);
        Assert.Equal(2.5f, ended, 3);   // and the drift is gone by the end of the turn, on both heads
    }

    [Fact]
    public void A_skipped_entity_keeps_its_heading()
    {
        // The opposite of the CommandedVelocity / LandingImpactSpeed rule on that same skip path. Those are per-tick
        // EVENTS and are zeroed so a stale one cannot read as this tick's. A heading is CARRIED state, so zeroing it
        // would spin every ghost and every in-flight migrating entity back to facing -Z.
        var sys = new PlayerMovementSystem(Flat, MoveTuning.Default);
        var ecs = new World();
        Entity e = ecs.Spawn();
        ecs.Set(e, new NetId(1));
        ecs.Set(e, ReplicatedPosition.FromWorld(new Vector3(0f, 1f, 0f), WorldFrame.Origin));
        ecs.Set(e, new MovementState { FacingYawQ = MovementState.QuantizeFacingYaw(1.5f) });
        ecs.Set(e, new PendingMove { Command = MoveCommand.Idle });
        ecs.Set(e, new KhaozEngine.Sharding.Ghost());

        sys.Update(ecs, Dt);

        Assert.Equal(1.5f, MovementState.DecodeFacingYaw(ecs.Get<MovementState>(e).FacingYawQ), 3);
    }

    // ---- Both server heads, through the real wire ----

    static (WorldServer server, NetClient client, WorldServerConfig cfg) ConnectSingle()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = Dt, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire());
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        return (server, client, cfg);
    }

    static (ShardedWorldServer server, NetClient client, ShardedWorldServerConfig cfg) ConnectSharded()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var cfg = new ShardedWorldServerConfig
        {
            TickSeconds = Dt, CellSize = 60f, OverlapMargin = 24f, InterestRadius = 24f,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire());
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        return (server, client, cfg);
    }

    [Fact]
    public void WorldServer_ReadsTheHeadingPerSlot_DrivenByTheFaceCameraFlag()
    {
        (WorldServer server, NetClient client, WorldServerConfig cfg) = ConnectSingle();

        // A STATIONARY turn, which is the case that was impossible before this: the command carries no move axis at
        // all, so a position-delta derivation has nothing to read.
        client.Send(MoveProtocol.EncodeMove(0, Face(1.9f)), NetChannelReliability.ReliableOrdered);
        client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);

        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState st));
        Assert.Equal(1.9f, st.Move.FacingYaw, 5);
        Assert.Equal(Vector3.Zero.X, st.Position.X, 5);   // and the turn moved nothing

        // Without the flag the heading follows the commanded direction instead: +X is -pi/2 in the camera-yaw basis.
        client.Send(MoveProtocol.EncodeMove(1, Run), NetChannelReliability.ReliableOrdered);
        client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState moved));
        Assert.Equal(0f, MathF.Abs(CharacterMovement.WrapYaw(moved.Move.FacingYaw + MathF.PI / 2f)), 4);
    }

    [Fact]
    public void ShardedWorldServer_ReadsTheHeadingPerSlot_ThroughTheCellWriteBack()
    {
        // The same read, on the head where it has to survive the per-cell write-back into the component and the
        // rebuild back out through PlayerMoveState.From.
        (ShardedWorldServer server, NetClient client, ShardedWorldServerConfig cfg) = ConnectSharded();

        client.Send(MoveProtocol.EncodeMove(0, Face(-2.1f)), NetChannelReliability.ReliableOrdered);
        client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);

        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState st));
        Assert.Equal(0f, MathF.Abs(CharacterMovement.WrapYaw(st.Move.FacingYaw + 2.1f)), 3);

        client.Send(MoveProtocol.EncodeMove(1, Run), NetChannelReliability.ReliableOrdered);
        client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState moved));
        Assert.Equal(0f, MathF.Abs(CharacterMovement.WrapYaw(moved.Move.FacingYaw + MathF.PI / 2f)), 3);
    }

    [Fact]
    public void BothHeads_AgreeOnAHeldStationaryTurn()
    {
        // The same command stream on both heads, compared per tick. A finite turn rate makes the comparison mean
        // something: it is the carried heading, not a per-tick recompute, so a head that lost the carry drifts away
        // immediately instead of landing on the same snapped value by luck.
        var tuning = MoveTuning.Default with { FacingTurnSpeed = 3f };
        (LoopbackTransport st1, LoopbackTransport ct1) = LoopbackTransport.CreatePair();
        (LoopbackTransport st2, LoopbackTransport ct2) = LoopbackTransport.CreatePair();
        var flatCfg = new WorldServerConfig { TickSeconds = Dt, SpawnPosition = _ => Vector3.Zero };
        var shardCfg = new ShardedWorldServerConfig
        {
            TickSeconds = Dt, CellSize = 60f, OverlapMargin = 24f, InterestRadius = 24f,
            SpawnPosition = _ => Vector3.Zero,
        };
        var flat = new WorldServer(st1, flatCfg, Flat, tuning);
        var sharded = new ShardedWorldServer(st2, shardCfg, Flat, tuning);
        var c1 = new NetClient(ct1, TestHandshake.Wire());
        var c2 = new NetClient(ct2, TestHandshake.Wire());
        for (int i = 0; i < 10; i++)
        {
            c1.Poll(); flat.Poll(); flat.Tick(Dt);
            c2.Poll(); sharded.Poll(); sharded.Tick(Dt);
        }

        var gaps = new List<float>();
        for (int i = 0; i < 40; i++)
        {
            byte[] frame = MoveProtocol.EncodeMove(i, Face(2.8f));
            c1.Send(frame, NetChannelReliability.ReliableOrdered);
            c2.Send(frame, NetChannelReliability.ReliableOrdered);
            c1.Poll(); flat.Poll(); flat.Tick(Dt);
            c2.Poll(); sharded.Poll(); sharded.Tick(Dt);

            Assert.True(flat.TryGetPlayerState(c1.Slot, out PlayerMoveState a));
            Assert.True(sharded.TryGetPlayerState(c2.Slot, out PlayerMoveState b));
            gaps.Add(MathF.Abs(CharacterMovement.WrapYaw(a.Move.FacingYaw - b.Move.FacingYaw)));
        }

        float worst = 0f;
        foreach (float g in gaps) worst = MathF.Max(worst, g);
        // Same bound and same reason as the per-cell test above: the sharded head requantizes its carried heading
        // every tick, which accumulates at most half a quantum per turning tick and unwinds when the turn lands.
        Assert.True(worst < 0.5f * gaps.Count * Quantum + Quantum,
            $"the two heads' headings diverged {worst} rad (quantum {Quantum})");
        Assert.True(flat.TryGetPlayerState(c1.Slot, out PlayerMoveState end));
        Assert.True(sharded.TryGetPlayerState(c2.Slot, out PlayerMoveState shardedEnd));
        Assert.Equal(2.8f, end.Move.FacingYaw, 4);          // the fixture actually completed the turn
        Assert.Equal(2.8f, shardedEnd.Move.FacingYaw, 3);   // and both heads finished on the same heading
    }

    // ---- Harness ----

    static MovementState RoundTrip(MovementState src)
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var serverWorld = new World();
        Entity e = serverWorld.Spawn();
        serverWorld.Set(e, new NetId(1));
        serverWorld.Set(e, ReplicatedPosition.FromWorld(Vector3.Zero, WorldFrame.Origin));
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
