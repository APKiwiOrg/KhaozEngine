using System;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The queued movement hold on both server heads: a held seat steps on <see cref="MoveCommand.Idle"/> in place of
/// each dequeued command, so its acknowledgement still advances and gravity still steps while its input moves nothing.
/// </summary>
public class MovementHoldTests
{
    static readonly MoveCommand Walk = new(Vector2.UnitY, run: false, cameraYaw: 0f);
    static readonly MoveCommand Run = new(Vector2.UnitY, run: true, cameraYaw: 0f);
    static readonly MoveCommand Jump = new(Vector2.UnitY, run: false, cameraYaw: 0f, jump: true);

    static MoveCommand Alternating(int i) => (i % 3) switch { 0 => Walk, 1 => Run, _ => Jump };

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Hold_and_release_apply_on_next_tick_in_queue_order(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        using MovementHoldSeat seat = rig.JoinClient("a");
        int slot = seat.Slot;
        PlayerMoveState start = rig.Authority(slot);

        rig.Server.HoldMovement(PlayerRef.Slot(slot));
        Assert.False(rig.Server.IsMovementHeld(slot));
        rig.Server.Tick(MovementHoldRig.Dt);
        Assert.True(rig.Server.IsMovementHeld(slot));

        rig.Server.ReleaseMovement(PlayerRef.Account("a"));
        Assert.True(rig.Server.IsMovementHeld(slot));
        rig.Server.Tick(MovementHoldRig.Dt);
        Assert.False(rig.Server.IsMovementHeld(slot));

        rig.Server.HoldMovement(PlayerRef.Slot(slot));
        rig.Server.ReleaseMovement(PlayerRef.Account("a"));
        rig.Server.Tick(MovementHoldRig.Dt);
        Assert.False(rig.Server.IsMovementHeld(slot));

        rig.Server.ReleaseMovement(PlayerRef.Slot(slot));
        rig.Server.HoldMovement(PlayerRef.Account("a"));
        rig.Server.Tick(MovementHoldRig.Dt);
        Assert.True(rig.Server.IsMovementHeld(slot));

        rig.Server.HoldMovement(PlayerRef.Slot(slot));
        rig.Server.HoldMovement(PlayerRef.Account("a"));
        for (int i = 0; i < 3; i++) rig.Step();   // an empty movement queue steps the held body in place
        Assert.True(rig.Server.IsMovementHeld(slot));
        PlayerMoveState held = rig.Authority(slot);
        Assert.Equal(start.Position.X, held.Position.X, 3);
        Assert.Equal(start.Position.Z, held.Position.Z, 3);

        rig.Server.ReleaseMovement(PlayerRef.Slot(slot));
        rig.Server.ReleaseMovement(PlayerRef.Slot(slot));
        rig.Step();
        Assert.False(rig.Server.IsMovementHeld(slot));
        rig.Server.ReleaseMovement(PlayerRef.Account("a"));
        rig.Step();
        Assert.False(rig.Server.IsMovementHeld(slot));
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Held_walk_run_jump_consumes_every_sequence(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        using MovementHoldSeat seat = rig.JoinClient("a");
        PlayerMoveState start = rig.Authority(seat.Slot);
        Assert.True(start.Move.Grounded);
        rig.Server.HoldMovement(PlayerRef.Slot(seat.Slot));
        rig.Serve(seat);

        for (int i = 0; i < 60; i++)
        {
            int sentSequence = seat.Client.SendInput(Alternating(i));
            PlayerMoveState state = rig.Serve(seat);
            int decodedAcknowledgement = seat.Wire.LastAcknowledgedSequence;

            Assert.Equal(start.Position.X, state.Position.X, 3);
            Assert.Equal(start.Position.Z, state.Position.Z, 3);
            Assert.Equal(start.Position.Y, state.Position.Y, 3);
            Assert.True(state.Move.Grounded);
            Assert.Equal(sentSequence, decodedAcknowledgement);
        }
        Assert.True(rig.Server.IsMovementHeld(seat.Slot));
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Held_body_falls_and_release_walks(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        using MovementHoldSeat seat = rig.JoinClient("a");
        int slot = seat.Slot;
        rig.Server.HoldMovement(PlayerRef.Slot(slot));
        rig.Serve(seat);
        Assert.True(rig.Server.IsMovementHeld(slot));

        PlayerMoveState lifted = rig.Authority(slot);
        lifted.Position = new Vector3(lifted.Position.X, 10f, lifted.Position.Z);
        lifted.VerticalVelocity = 0f;
        lifted.Grounded = false;
        rig.Server.SetPlayerState(slot, lifted);

        PlayerMoveState first = rig.Serve(seat);
        Assert.True(first.Position.Y < 10f, $"held body did not fall, Y {first.Position.Y}");
        PlayerMoveState landed = first;
        for (int i = 0; i < 180 && !landed.Move.Grounded; i++)
        {
            seat.Client.SendInput(Walk);
            landed = rig.Serve(seat);
        }
        Assert.True(landed.Move.Grounded);
        Assert.Equal(rig.Tuning.CapsuleHalfHeight, landed.Position.Y, 3);   // flat ground is Y 0
        Assert.Equal(lifted.Position.X, landed.Position.X, 3);
        Assert.Equal(lifted.Position.Z, landed.Position.Z, 3);

        rig.Server.ReleaseMovement(PlayerRef.Slot(slot));
        int walkSequence = seat.Client.SendInput(Walk);
        PlayerMoveState afterRelease = rig.Serve(seat);
        Assert.False(rig.Server.IsMovementHeld(slot));
        Assert.Equal(walkSequence, seat.Wire.LastAcknowledgedSequence);
        Assert.NotEqual(landed.Position.Z, afterRelease.Position.Z);
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Held_input_reconciles_without_pending_backlog(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        using MovementHoldSeat seat = rig.JoinClient("a");
        PlayerMoveState start = rig.Authority(seat.Slot);
        rig.Server.HoldMovement(PlayerRef.Slot(seat.Slot));
        rig.Serve(seat);
        ClientPrediction<PlayerMoveState, MoveCommand> prediction = seat.NewPrediction(rig.Authority(seat.Slot));
        int authoritativeTick = 0;

        for (int i = 0; i < 60; i++)
        {
            MoveCommand command = Alternating(i);
            int sentSequence = seat.Client.SendInput(command);
            Assert.Equal(sentSequence, prediction.Predict(command));
            Assert.True(prediction.PendingCommandCount > 0);
            PlayerMoveState state = rig.Serve(seat);

            prediction.Reconcile(++authoritativeTick, state, seat.Wire.LastAcknowledgedSequence);
            Assert.Equal(sentSequence, seat.Wire.LastAcknowledgedSequence);
            Assert.Equal(0, prediction.PendingCommandCount);
            Assert.Equal(start.Position.X, prediction.PredictedState.Position.X, 3);
            Assert.Equal(start.Position.Z, prediction.PredictedState.Position.Z, 3);
        }
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Unheld_movement_is_unchanged(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        using MovementHoldSeat seat = rig.JoinClient("a");
        PlayerMoveState start = rig.Authority(seat.Slot);
        ClientPrediction<PlayerMoveState, MoveCommand> prediction = seat.NewPrediction(start);
        int authoritativeTick = 0;
        float highest = start.Position.Y;

        for (int i = 0; i < 60; i++)
        {
            MoveCommand command = Alternating(i);
            int sentSequence = seat.Client.SendInput(command);
            Assert.Equal(sentSequence, prediction.Predict(command));
            PlayerMoveState predicted = prediction.PredictedState;
            PlayerMoveState state = rig.Serve(seat);

            Assert.Equal(predicted.Position.X, state.Position.X, 3);
            Assert.Equal(predicted.Position.Y, state.Position.Y, 3);
            Assert.Equal(predicted.Position.Z, state.Position.Z, 3);
            Assert.Equal(sentSequence, seat.Wire.LastAcknowledgedSequence);
            prediction.Reconcile(++authoritativeTick, state, seat.Wire.LastAcknowledgedSequence);
            Assert.Equal(0, prediction.PendingCommandCount);
            highest = MathF.Max(highest, state.Position.Y);
        }

        Assert.False(rig.Server.IsMovementHeld(seat.Slot));
        Assert.True(rig.Authority(seat.Slot).Position.Z < start.Position.Z - 1f,
            $"unheld walk did not travel, Z {rig.Authority(seat.Slot).Position.Z}");
        Assert.True(highest > start.Position.Y + 0.1f, $"unheld jump never left the ground, peak {highest}");
    }
}

public enum MovementHoldHead { Flat, Sharded }

/// <summary>The public operations the hold tests drive, implemented identically by both server heads.</summary>
internal interface IMovementHoldServer
{
    void HoldMovement(PlayerRef target);
    void ReleaseMovement(PlayerRef target);
    bool IsMovementHeld(int slot);
    void Poll();
    void Tick(float dt);
    void Teleport(PlayerRef target, Vector3 position);
    void Disconnect(int slot);
    bool TryGetPlayerState(int slot, out PlayerMoveState state);
    void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false);
    bool TryGetPlayerNetId(int slot, out long netId);
    bool TryGetAccountId(int slot, out string accountId);
    uint BeginMovementCommitment(PlayerRef target, in MovementCommitmentRequest request);
}

/// <summary>One server head on an in-memory hub, with strict oldest-first input so each tick acknowledges the
/// command sent for it. The sharded head can grant a disconnect linger.</summary>
internal sealed class MovementHoldRig : IDisposable
{
    public const float Dt = 1f / 30f;
    public static readonly Func<float, float, float> Ground = (_, _) => 0f;

    public readonly InMemoryTransportHub Hub = new();
    public readonly IMovementHoldServer Server;
    public readonly ShardedWorldServer? Sharded;
    public readonly MoveTuning Tuning;
    public int LingerTicks;
    public bool ThrowOnLeave;
    public int Leaves;

    public MovementHoldRig(MovementHoldHead head, int? lingerTicks = null, MoveTuning? tuning = null)
    {
        Tuning = tuning ?? MoveTuning.Default;
        LingerTicks = lingerTicks ?? 0;
        if (head == MovementHoldHead.Flat)
        {
            var flat = new WorldServer(Hub.Server, new WorldServerConfig
            {
                TickSeconds = Dt,
                InterestRadius = 500f,
                MaxPlayers = 4,
                MaxInputBacklog = 0,
            }, Ground, Tuning);
            flat.PlayerLeaving += (_, _, _) => OnLeaving();
            Server = new FlatHead(flat);
        }
        else
        {
            Sharded = new ShardedWorldServer(Hub.Server, new ShardedWorldServerConfig
            {
                TickSeconds = Dt,
                MaxPlayers = 4,
                MaxInputBacklog = 0,
                DisconnectLingerTicks = lingerTicks is null ? null : (_, _) => LingerTicks,
            }, Ground, Tuning);
            Sharded.PlayerLeaving += (_, _, _) => OnLeaving();
            Server = new ShardedHead(Sharded);
        }
    }

    void OnLeaving()
    {
        Leaves++;
        if (ThrowOnLeave) throw new InvalidOperationException("leave callback failed");
    }

    public void Step()
    {
        Server.Poll();
        Server.Tick(Dt);
    }

    public PlayerMoveState Serve(MovementHoldSeat seat)
    {
        Step();
        seat.Client.Poll();
        return Authority(seat.Slot);
    }

    public PlayerMoveState Authority(int slot)
    {
        Assert.True(Server.TryGetPlayerState(slot, out PlayerMoveState state));
        return state;
    }

    public int SlotOf(string account)
    {
        for (int slot = 0; slot < 4; slot++)
            if (Server.TryGetAccountId(slot, out string id) && id == account) return slot;
        return -1;
    }

    public MovementHoldSeat JoinClient(string account)
    {
        var wire = new MovementHoldAckTransport(Hub.CreateClient());
        var settings = PredictionSettings.Default with { TickSeconds = Dt, CorrectionRate = 0f };
        var client = new WorldClient(wire, Ground, Tuning,
            new WorldClientConfig { TickSeconds = Dt, Prediction = settings }, token: Encoding.UTF8.GetBytes(account));
        for (int i = 0; i < 30 && !client.Joined; i++)
        {
            Step();
            client.Poll();
        }
        for (int i = 0; i < 6; i++)   // settle onto the ground before any command is sent
        {
            Step();
            client.Poll();
        }
        Assert.True(client.Joined);
        int slot = SlotOf(account);
        Assert.True(slot >= 0);
        return new MovementHoldSeat(client, wire, slot, Tuning, settings);
    }

    public int JoinRaw(string account, out NetClient client, out INetTransport link)
    {
        link = Hub.CreateClient();
        client = new NetClient(link, TestHandshake.Wire(account));
        client.Poll();
        Server.Poll();
        client.Poll();
        Assert.True(client.Slot >= 0);
        Assert.True(Server.TryGetPlayerNetId(client.Slot, out _));
        return client.Slot;
    }

    public void Dispose()
    {
        Sharded?.Dispose();
        Hub.Dispose();
    }

    sealed class FlatHead(WorldServer server) : IMovementHoldServer
    {
        public void HoldMovement(PlayerRef target) => server.HoldMovement(target);
        public void ReleaseMovement(PlayerRef target) => server.ReleaseMovement(target);
        public bool IsMovementHeld(int slot) => server.IsMovementHeld(slot);
        public void Poll() => server.Poll();
        public void Tick(float dt) => server.Tick(dt);
        public void Teleport(PlayerRef target, Vector3 position) => server.Teleport(target, position);
        public void Disconnect(int slot) => server.Disconnect(slot);
        public bool TryGetPlayerState(int slot, out PlayerMoveState state) => server.TryGetPlayerState(slot, out state);
        public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false) =>
            server.SetPlayerState(slot, state, teleport);
        public bool TryGetPlayerNetId(int slot, out long netId) => server.TryGetPlayerNetId(slot, out netId);
        public bool TryGetAccountId(int slot, out string accountId) => server.TryGetAccountId(slot, out accountId);
        public uint BeginMovementCommitment(PlayerRef target, in MovementCommitmentRequest request) =>
            server.BeginMovementCommitment(target, request);
    }

    sealed class ShardedHead(ShardedWorldServer server) : IMovementHoldServer
    {
        public void HoldMovement(PlayerRef target) => server.HoldMovement(target);
        public void ReleaseMovement(PlayerRef target) => server.ReleaseMovement(target);
        public bool IsMovementHeld(int slot) => server.IsMovementHeld(slot);
        public void Poll() => server.Poll();
        public void Tick(float dt) => server.Tick(dt);
        public void Teleport(PlayerRef target, Vector3 position) => server.Teleport(target, position);
        public void Disconnect(int slot) => server.Disconnect(slot);
        public bool TryGetPlayerState(int slot, out PlayerMoveState state) => server.TryGetPlayerState(slot, out state);
        public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false) =>
            server.SetPlayerState(slot, state, teleport);
        public bool TryGetPlayerNetId(int slot, out long netId) => server.TryGetPlayerNetId(slot, out netId);
        public bool TryGetAccountId(int slot, out string accountId) => server.TryGetAccountId(slot, out accountId);
        public uint BeginMovementCommitment(PlayerRef target, in MovementCommitmentRequest request) =>
            server.BeginMovementCommitment(target, request);
    }
}

/// <summary>A joined <see cref="WorldClient"/> with the acknowledgement its last snapshot header carried.</summary>
internal sealed class MovementHoldSeat(WorldClient client, MovementHoldAckTransport wire, int slot, MoveTuning tuning,
    PredictionSettings settings) : IDisposable
{
    public WorldClient Client { get; } = client;
    public MovementHoldAckTransport Wire { get; } = wire;
    public int Slot { get; } = slot;

    public ClientPrediction<PlayerMoveState, MoveCommand> NewPrediction(in PlayerMoveState basis)
    {
        var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(
            new PlayerMoveSimulator(MovementHoldRig.Ground, tuning), settings);
        prediction.Reset(basis);
        return prediction;
    }

    public void Dispose() => Client.Dispose();
}

/// <summary>Decodes the acknowledgement from each plain or ticked snapshot header the client receives.</summary>
internal sealed class MovementHoldAckTransport(INetTransport inner) : INetTransport
{
    public int LastAcknowledgedSequence { get; private set; } = -1;

    public bool TryDequeueEvent(out NetEvent value)
    {
        if (!inner.TryDequeueEvent(out value)) return false;
        if (value.Type == NetEventType.Data && SessionFrame.ReadOpcode(value.Data) == SessionOpcode.Data
            && MoveProtocol.TryDecodeServerFrame(SessionFrame.ReadBody(value.Data), out var kind, out byte[] body))
        {
            if (kind is MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta
                && MoveProtocol.TryDecodeSnapshotFrame(body, out _, out int sequence, out _))
                LastAcknowledgedSequence = sequence;
            else if (kind is MoveProtocol.ServerFrameKind.TickedSnapshot or MoveProtocol.ServerFrameKind.TickedDelta
                && MoveProtocol.TryDecodeTickedSnapshotFrame(body, out _, out _, out int ticked, out _))
                LastAcknowledgedSequence = ticked;
        }
        return true;
    }

    public void Poll() => inner.Poll();
    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
        inner.Send(target, payload, reliability);
    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) =>
        inner.Disconnect(connection, reason);
    public void Dispose() => inner.Dispose();
}
