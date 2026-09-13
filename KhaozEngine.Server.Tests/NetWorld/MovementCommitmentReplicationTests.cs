using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class MovementCommitmentReplicationTests
{
    private const float Dt = 1f / 30f;

    [Fact]
    public void Commitment_round_trips_through_components_and_the_wire_codec()
    {
        var source = new PlayerMoveState
        {
            Move = new MoveState
            {
                Commitment = new MovementCommitment(91u, Vector2.Normalize(new Vector2(3f, -4f)),
                    12.5f, 8.25f, 0.3f, 4.5f),
            },
        };
        source.Move.Commitment = source.Move.Commitment with
        {
            Phase = MovementCommitmentPhase.Airborne,
            PreparationRemaining = 0f,
            TimeoutRemaining = 3.75f,
        };

        MovementState encoded = MovementState.From(source);
        MovementState decoded = RoundTrip(encoded);
        PlayerMoveState result = PlayerMoveState.From(Vector3.Zero, decoded);

        Assert.Equal(91u, result.Move.Commitment.Sequence);
        Assert.Equal(MovementCommitmentPhase.Airborne, result.Move.Commitment.Phase);
        Assert.InRange(Vector2.Distance(source.Move.Commitment.Direction, result.Move.Commitment.Direction), 0f, 0.001f);
        Assert.Equal(12.5f, result.Move.Commitment.HorizontalSpeed, 3);
        Assert.Equal(8.25f, result.Move.Commitment.VerticalSpeed, 3);
        Assert.Equal(3.75f, result.Move.Commitment.TimeoutRemaining, 3);
    }

    [Fact]
    public void Reconcile_replay_preserves_the_committed_arc_against_hostile_commands()
    {
        const float Dt = 1f / 30f;
        static float Flat(float _, float __) => 0f;
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var authoritative = new PlayerMoveState
        {
            Move = new MoveState
            {
                Position = new Vector3(0f, MoveTuning.Default.CapsuleHalfHeight, 0f),
                Grounded = true,
                FacingYaw = 0.6f,
                Commitment = new MovementCommitment(4u, Vector2.UnitX, 10f, 7f, 0f, 5f),
            },
        };
        MovementState wire = RoundTrip(MovementState.From(authoritative));
        PlayerMoveState basis = PlayerMoveState.From(authoritative.Position, wire);
        MoveCommand hostile = new(-Vector2.UnitX, run: true, cameraYaw: -2f, jump: true, faceCamera: true);

        PlayerMoveState replayed = basis;
        for (int i = 0; i < 6; i++) replayed = sim.Step(replayed, hostile, Dt);

        Assert.True(replayed.Position.X > 1.5f, replayed.Position.ToString());
        Assert.InRange(MathF.Abs(replayed.Position.Z), 0f, 1e-4f);
        Assert.Equal(0.6f, replayed.Move.FacingYaw, 3);
        Assert.Equal(4u, replayed.Move.Commitment.Sequence);
    }

    [Fact]
    public void Wire_generation_moves_for_the_commitment_payload()
    {
        Assert.True(MoveProtocol.WireProtocolVersion >= 11);
    }

    [Fact]
    public void World_server_publishes_landing_once_before_recovery_end_and_after_tick()
    {
        static float Flat(float _, float __) => 0f;
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 2 };
        var server = new WorldServer(serverTransport, config, Flat, MoveTuning.Default);
        var client = new NetClient(clientTransport, TestHandshake.Wire("committed"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(Dt);
        }
        int slot = Assert.Single(server.JoinedSlots);

        int landed = 0, ended = 0;
        bool afterTickObservedLanding = false;
        MovementCommitmentResult landing = default;
        server.MovementCommitmentLanded += result => { landed++; landing = result; };
        server.MovementCommitmentEnded += _ => ended++;
        server.OnAfterTick += _ => afterTickObservedLanding |= landed > 0;

        MovementCommitmentRequest request = MovementCommitmentRequest.ForBallisticArc(
            Vector2.UnitX, distance: 4f, apexHeight: 1.25f, durationSeconds: 0.8f,
            recoverySeconds: 0.15f, timeoutSeconds: 3f);
        uint sequence = server.BeginMovementCommitment(PlayerRef.Slot(slot), request);

        for (int i = 0; i < 180 && ended == 0; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i,
                new MoveCommand(-Vector2.UnitX, run: true, cameraYaw: -2f, jump: true, faceCamera: true)),
                NetChannelReliability.ReliableOrdered);
            server.Poll();
            server.Tick(Dt);
            client.Poll();
        }

        Assert.Equal(1, landed);
        Assert.Equal(1, ended);
        Assert.True(afterTickObservedLanding);
        Assert.Equal(sequence, landing.Sequence);
        Assert.Equal(slot, landing.Slot);
        Assert.Equal(MovementCommitmentEndReason.Landed, landing.Reason);
        Assert.Equal(Vector2.UnitX, landing.Direction);
        Assert.True(landing.Position.X > 3f, landing.Position.ToString());

        for (int i = 0; i < 10; i++) { server.Poll(); server.Tick(Dt); }
        Assert.Equal(1, landed);
        Assert.Equal(1, ended);
    }

    [Fact]
    public void Ballistic_factory_derives_the_requested_arc()
    {
        MovementCommitmentRequest request = MovementCommitmentRequest.ForBallisticArc(
            new Vector2(3f, 4f), distance: 9f, apexHeight: 2f, durationSeconds: 1.5f,
            recoverySeconds: 0.2f, timeoutSeconds: 4f);

        Assert.Equal(new Vector2(0.6f, 0.8f), request.Direction);
        Assert.Equal(6f, request.HorizontalSpeed, 5);
        Assert.Equal(16f / 3f, request.VerticalSpeed, 5);
        Assert.Equal(64f / 9f, request.Gravity, 5);
        Assert.Equal(0.2f, request.RecoverySeconds);
    }

    [Fact]
    public void Player_self_rescue_cannot_interrupt_an_active_commitment()
    {
        static float Flat(float _, float __) => 0f;
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig
        {
            TickSeconds = Dt,
            MaxPlayers = 2,
            SelfRescueDestination = _ => new Vector3(50f, 20f, 50f),
        };
        var server = new WorldServer(serverTransport, config, Flat, MoveTuning.Default);
        var client = new NetClient(clientTransport, TestHandshake.Wire("rescue"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++)
        {
            client.Poll(); server.Poll(); server.Tick(Dt);
        }
        int slot = Assert.Single(server.JoinedSlots);
        uint sequence = server.BeginMovementCommitment(PlayerRef.Slot(slot),
            MovementCommitmentRequest.ForBallisticArc(Vector2.UnitX, 8f, 2f, 1.2f));
        server.Tick(Dt);

        client.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.SelfRescue),
            NetChannelReliability.ReliableOrdered);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(Dt); }

        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState state));
        Assert.Equal(sequence, state.Move.Commitment.Sequence);
        Assert.True(state.Move.Commitment.IsActive);
        Assert.True(state.Position.X < 10f, state.Position.ToString());
        Assert.True(state.Position.Z < 10f, state.Position.ToString());
    }

    [Fact]
    public void Sharded_commitment_survives_an_airborne_cell_handoff_and_lands_once()
    {
        static float Flat(float _, float __) => 0f;
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = Dt,
            CellSize = 4f,
            OverlapMargin = 2f,
            InterestRadius = 2f,
            SpawnPosition = _ => new Vector3(1f, 0f, 1f),
        };
        using var server = new ShardedWorldServer(serverTransport, config, Flat, MoveTuning.Default);
        var client = new NetClient(clientTransport, TestHandshake.Wire("sharded"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++)
        {
            client.Poll(); server.Poll(); server.Tick(Dt);
        }
        int slot = Assert.Single(server.JoinedSlots);
        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        uint sequence = server.BeginMovementCommitment(PlayerRef.Slot(slot),
            MovementCommitmentRequest.ForBallisticArc(Vector2.UnitX, 8f, 2f, 1.2f));
        int landed = 0;
        server.MovementCommitmentLanded += result =>
        {
            Assert.Equal(sequence, result.Sequence);
            landed++;
        };

        CellCoord? previous = null;
        bool crossedAirborne = false;
        for (int i = 0; i < 180 && landed == 0; i++)
        {
            server.Poll(); server.Tick(Dt); client.Poll();
            Assert.True(server.Host.TryGetOwner(netId, out CellSim owner, out Entity entity));
            MovementState state = owner.World.Get<MovementState>(entity);
            Assert.Equal(sequence, state.Commitment.Sequence);
            if (previous is CellCoord cell && cell != owner.Coord
                && state.Commitment.Phase == MovementCommitmentPhase.Airborne)
                crossedAirborne = true;
            previous = owner.Coord;
        }

        Assert.True(crossedAirborne);
        Assert.Equal(1, landed);
    }

    [Fact]
    public void Deep_water_aborts_without_publishing_a_landing()
    {
        static float Flat(float _, float __) => 0f;
        static MovementMedium DeepWater(float _, float __, float ___) => new(4f, inWater: true);
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 2 };
        var server = new WorldServer(serverTransport, config, Flat, MoveTuning.Default, medium: DeepWater);
        var client = new NetClient(clientTransport, TestHandshake.Wire("water"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++)
        {
            client.Poll(); server.Poll(); server.Tick(Dt);
        }
        int slot = Assert.Single(server.JoinedSlots);
        int landed = 0;
        MovementCommitmentResult ended = default;
        server.MovementCommitmentLanded += _ => landed++;
        server.MovementCommitmentEnded += result => ended = result;

        uint sequence = server.BeginMovementCommitment(PlayerRef.Slot(slot),
            MovementCommitmentRequest.ForBallisticArc(Vector2.UnitX, 4f, 1f, 0.8f));
        server.Tick(Dt);

        Assert.Equal(0, landed);
        Assert.Equal(sequence, ended.Sequence);
        Assert.Equal(MovementCommitmentEndReason.EnteredWater, ended.Reason);
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState state));
        Assert.True(state.Swimming);
    }

    private static MovementState RoundTrip(MovementState source)
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity entity = server.Spawn();
        server.Set(entity, new NetId(1));
        server.Set(entity, ReplicatedPosition.FromWorld(Vector3.Zero, WorldFrame.Origin));
        server.Set(entity, source);
        byte[] bytes = SnapshotWriter.Write(server, registry);

        var view = new ClientReplicationView(registry);
        var client = new World();
        view.Apply(client, bytes);
        Assert.True(view.TryGetEntity(1, out Entity local));
        return client.Get<MovementState>(local);
    }
}
