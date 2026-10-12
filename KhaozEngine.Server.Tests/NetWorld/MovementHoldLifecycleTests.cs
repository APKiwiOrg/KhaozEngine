using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// How a movement hold lives across a seat's lifecycle: a teleport keeps it, an actual leave ends it even when the
/// leave callback throws, and a recycled, unknown or departed slot is never held. On the sharded head a lingering body
/// stays joined and held until its leave, and a cross-cell teleport keeps the hold on the one owning cell.
/// </summary>
public class MovementHoldLifecycleTests
{
    const float Dt = MovementHoldRig.Dt;
    static readonly MoveCommand Walk = new(Vector2.UnitY, run: false, cameraYaw: 0f);

    static void SendWalk(NetClient client, int sequence) =>
        client.Send(MoveProtocol.EncodeMove(sequence, Walk), NetChannelReliability.ReliableOrdered);

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Teleport_preserves_hold_and_advances_epoch(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        int slot = rig.JoinRaw("a", out NetClient client, out _);
        for (int i = 0; i < 6; i++) rig.Step();
        rig.Server.HoldMovement(PlayerRef.Account("a"));
        rig.Step();
        Assert.True(rig.Server.IsMovementHeld(slot));
        PlayerMoveState before = rig.Authority(slot);

        rig.Server.Teleport(PlayerRef.Slot(slot), new Vector3(8f, 0f, 6f));
        rig.Step();

        PlayerMoveState placed = rig.Authority(slot);
        Assert.True(rig.Server.IsMovementHeld(slot));
        Assert.True(placed.TeleportEpoch > before.TeleportEpoch,
            $"teleport epoch did not advance ({before.TeleportEpoch} -> {placed.TeleportEpoch})");
        Assert.Equal(8f, placed.Position.X, 3);
        Assert.Equal(6f, placed.Position.Z, 3);

        for (int sequence = 0; sequence < 10; sequence++)
        {
            SendWalk(client, sequence);
            rig.Step();
        }
        PlayerMoveState after = rig.Authority(slot);
        Assert.True(rig.Server.IsMovementHeld(slot));
        Assert.Equal(placed.Position.X, after.Position.X, 3);
        Assert.Equal(placed.Position.Z, after.Position.Z, 3);
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Actual_leave_and_throwing_leave_handler_clear_hold(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        int dropped = rig.JoinRaw("a", out _, out INetTransport link);
        rig.Server.HoldMovement(PlayerRef.Slot(dropped));
        rig.Step();
        Assert.True(rig.Server.IsMovementHeld(dropped));

        rig.Hub.DisconnectClient(link);
        rig.Server.Poll();
        Assert.False(rig.Server.TryGetPlayerNetId(dropped, out _));
        Assert.False(rig.Server.IsMovementHeld(dropped));
        Assert.Equal(1, rig.Leaves);

        int kicked = rig.JoinRaw("b", out _, out _);
        rig.Server.HoldMovement(PlayerRef.Account("b"));
        rig.Step();
        Assert.True(rig.Server.IsMovementHeld(kicked));

        rig.ThrowOnLeave = true;
        Assert.Throws<InvalidOperationException>(() => rig.Server.Disconnect(kicked));
        rig.ThrowOnLeave = false;
        Assert.Equal(2, rig.Leaves);
        Assert.False(rig.Server.TryGetPlayerNetId(kicked, out _));
        Assert.False(rig.Server.IsMovementHeld(kicked));
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Recycled_and_unknown_slots_are_not_held(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        Assert.False(rig.Server.IsMovementHeld(0));
        Assert.False(rig.Server.IsMovementHeld(-1));

        rig.Server.HoldMovement(PlayerRef.Slot(3));
        rig.Server.HoldMovement(PlayerRef.Account("nobody"));
        rig.Step();
        Assert.False(rig.Server.IsMovementHeld(3));

        int slot = rig.JoinRaw("a", out _, out INetTransport link);
        Assert.False(rig.Server.IsMovementHeld(slot));
        rig.Server.HoldMovement(PlayerRef.Slot(slot));
        rig.Step();
        Assert.True(rig.Server.IsMovementHeld(slot));

        rig.Hub.DisconnectClient(link);
        rig.Server.Poll();
        Assert.False(rig.Server.IsMovementHeld(slot));   // departed

        int recycled = rig.JoinRaw("b", out NetClient client, out _);
        Assert.Equal(slot, recycled);
        for (int i = 0; i < 6; i++) rig.Step();
        Assert.False(rig.Server.IsMovementHeld(recycled));
        PlayerMoveState start = rig.Authority(recycled);
        for (int sequence = 0; sequence < 10; sequence++)
        {
            SendWalk(client, sequence);
            rig.Step();
        }
        Assert.NotEqual(start.Position.Z, rig.Authority(recycled).Position.Z);
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Queued_slot_hold_does_not_land_on_a_recycled_seat(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head);
        int slot = rig.JoinRaw("a", out _, out INetTransport link);
        rig.Server.HoldMovement(PlayerRef.Slot(slot));

        rig.Hub.DisconnectClient(link);
        int recycled = rig.JoinRaw("b", out NetClient client, out _);   // one Poll: the leave, then the join
        Assert.Equal(slot, recycled);
        Assert.True(rig.Server.TryGetAccountId(recycled, out string account));
        Assert.Equal("b", account);
        for (int i = 0; i < 6; i++) rig.Step();

        Assert.False(rig.Server.IsMovementHeld(recycled));
        PlayerMoveState start = rig.Authority(recycled);
        for (int sequence = 0; sequence < 10; sequence++)
        {
            SendWalk(client, sequence);
            rig.Step();
        }
        Assert.NotEqual(start.Position.Z, rig.Authority(recycled).Position.Z);

        rig.Server.HoldMovement(PlayerRef.Slot(recycled));   // queued for the current occupant, so it lands
        rig.Step();
        Assert.True(rig.Server.IsMovementHeld(recycled));
    }

    [Theory]
    [InlineData(MovementHoldHead.Flat)]
    [InlineData(MovementHoldHead.Sharded)]
    public void Hold_leaves_momentum_and_commitment_untouched(MovementHoldHead head)
    {
        using var rig = new MovementHoldRig(head, tuning: MoveTuning.Default with { AirMomentum = true });
        int flying = rig.JoinRaw("a", out _, out _);
        int committed = rig.JoinRaw("b", out _, out _);
        for (int i = 0; i < 6; i++) rig.Step();

        PlayerMoveState launched = rig.Authority(flying);
        launched.Position = new Vector3(launched.Position.X, 5f, launched.Position.Z);
        launched.VerticalVelocity = 0f;
        launched.Grounded = false;
        launched.Move.HorizontalVelocity = new Vector2(3f, 0f);
        rig.Server.SetPlayerState(flying, launched);
        PlayerMoveState origin = rig.Authority(committed);
        MovementCommitmentRequest request = MovementCommitmentRequest.ForBallisticArc(
            Vector2.UnitX, distance: 4f, apexHeight: 1.25f, durationSeconds: 0.8f,
            recoverySeconds: 0.15f, timeoutSeconds: 3f);
        uint sequence = rig.Server.BeginMovementCommitment(PlayerRef.Slot(committed), request);
        rig.Server.HoldMovement(PlayerRef.Slot(flying));
        rig.Server.HoldMovement(PlayerRef.Slot(committed));
        rig.Step();

        Assert.True(rig.Server.IsMovementHeld(flying));
        Assert.True(rig.Server.IsMovementHeld(committed));
        PlayerMoveState coasting = rig.Authority(flying);
        Assert.False(coasting.Move.Grounded);
        Assert.True(coasting.Move.HorizontalVelocity.X > 2.9f,
            $"momentum cleared to {coasting.Move.HorizontalVelocity}");
        Assert.True(coasting.Position.X > launched.Position.X + 0.05f, $"held body stopped at X {coasting.Position.X}");
        PlayerMoveState arcing = rig.Authority(committed);
        Assert.True(arcing.Move.Commitment.IsActive);
        Assert.Equal(sequence, arcing.Move.Commitment.Sequence);

        for (int i = 0; i < 60 && rig.Authority(committed).Move.Commitment.IsActive; i++) rig.Step();
        PlayerMoveState arrived = rig.Authority(committed);
        Assert.False(arrived.Move.Commitment.IsActive);
        Assert.InRange(arrived.Position.X - origin.Position.X, 3.95f, 4.05f);
        Assert.True(rig.Server.IsMovementHeld(committed));
    }

    [Fact]
    public void Cross_cell_teleport_preserves_hold_and_one_owner()
    {
        using var rig = new MovementHoldRig(MovementHoldHead.Sharded);
        ShardedWorldServer server = rig.Sharded!;
        int slot = rig.JoinRaw("a", out NetClient client, out _);
        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        for (int i = 0; i < 6; i++) rig.Step();
        server.HoldMovement(PlayerRef.Slot(slot));
        rig.Step();
        Assert.True(server.Host.TryGetOwner(netId, out CellSim home, out _));
        CellCoord homeCoord = home.Coord;

        server.Teleport(PlayerRef.Account("a"), new Vector3(150f, 0f, 150f));
        for (int sequence = 0; sequence < 10; sequence++)
        {
            SendWalk(client, sequence);
            rig.Step();
        }

        Assert.True(server.IsMovementHeld(slot));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim owner, out _));
        Assert.NotEqual(homeCoord, owner.Coord);
        int owners = 0;
        foreach (CellSim cell in server.Host.Cells)
            if (cell.TryGetOwned(netId, out _)) owners++;
        Assert.Equal(1, owners);
        PlayerMoveState state = rig.Authority(slot);
        Assert.Equal(150f, state.Position.X, 3);
        Assert.Equal(150f, state.Position.Z, 3);
    }

    [Fact]
    public void Lingering_body_stays_held_until_expiry_and_reclaim_leave()
    {
        using var rig = new MovementHoldRig(MovementHoldHead.Sharded, lingerTicks: 3);
        ShardedWorldServer server = rig.Sharded!;
        int expiring = rig.JoinRaw("a", out _, out INetTransport expiringLink);
        server.HoldMovement(PlayerRef.Slot(expiring));
        rig.Step();
        rig.Hub.DisconnectClient(expiringLink);
        rig.Server.Poll();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(server.IsLingering(expiring));
            Assert.True(server.TryGetPlayerNetId(expiring, out _));
            Assert.True(server.IsMovementHeld(expiring));
            server.Tick(Dt);
        }
        Assert.True(server.IsMovementHeld(expiring));
        Assert.Equal(0, rig.Leaves);
        server.Tick(Dt);
        Assert.Equal(1, rig.Leaves);
        Assert.False(server.TryGetPlayerNetId(expiring, out _));
        Assert.False(server.IsMovementHeld(expiring));

        rig.LingerTicks = 300;
        int reclaimed = rig.JoinRaw("b", out _, out INetTransport reclaimedLink);
        server.HoldMovement(PlayerRef.Account("b"));
        rig.Step();
        rig.Hub.DisconnectClient(reclaimedLink);
        rig.Server.Poll();
        rig.Step();
        Assert.True(server.IsLingering(reclaimed));
        Assert.True(server.IsMovementHeld(reclaimed));

        int again = rig.JoinRaw("b", out _, out _);
        Assert.Equal(reclaimed, again);
        Assert.Equal(2, rig.Leaves);
        Assert.False(server.IsLingering(again));
        Assert.False(server.IsMovementHeld(again));
    }
}
