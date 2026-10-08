using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

// The continuous position lever, the multi-cell twin of WorldServer.Placement.cs. Teleport is the other one, and
// the pair is documented on IAdminControllable: the two queue the identical placement and differ only in whether
// the teleport epoch advances, which is the client's signal to cut. Held in its own partial so both heads carry the
// identical seam and the frame-loop files stay inside the file-size ratchet.
public sealed partial class ShardedWorldServer
{
    /// <inheritdoc/>
    public void SetPosition(PlayerRef target, Vector3 position) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.SetPosition, Target = target, Position = position });

    /// <summary>Places a joined player. Explicit movement validates the destination and immediately
    /// transfers cross-cell ownership. Unconfigured legacy callers retain next-tick handoff.</summary>
    public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false) =>
        TrySetPlayerState(slot, state, teleport);

    /// <summary>False leaves pose, ownership, epoch and movement events unchanged.</summary>
    public bool TrySetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
    {
        if (!netIdBySlot.TryGetValue(slot, out long netId) || !host.TryGetOwner(netId, out CellSim source, out Entity entity))
            return false;
        uint epoch = TeleportEpochGuard.BaseEpoch(source.World, entity, slot);
        PlayerMoveState next = state.Absolute;
        if (teleport)
        {
            next.Move.Commitment = default;
            if (config.ExplicitMovementFactory is not null)
            {
                next.Move = MovementStepResult.HoldState(next.Move);
                next.Move.WaterExcursion = WaterExcursionState.None;
                next.Move.Swimming = false;
                next.Move.Grounded = false;
                next.Move.VerticalVelocity = 0;
                next.Move.HorizontalVelocity = Vector2.Zero;
            }
        }
        next.TeleportEpoch = teleport ? epoch + 1u : epoch;
        bool hadCurrent = TryGetPlayerState(slot, out PlayerMoveState current);
        if (config.ExplicitMovementFactory is null)
        {
            source.World.Set(entity, ReplicatedPosition.FromWorld(next.Position, source.Frame));
            MovementComponents.Set(source.World, entity, next);
        }
        else
        {
            CellSim destination = host.CellFor(next.Position.X, next.Position.Z);
            if (ReferenceEquals(source, destination))
            {
                using var read = RuntimeFor(destination).BeginPlacement(next, config.TickSeconds, out var placed, out bool admitted);
                if (!admitted) return false;
                source.World.Set(entity, ReplicatedPosition.FromWorld(placed.Position, source.Frame));
                MovementComponents.Set(source.World, entity, placed);
            }
            else
            {
                if (!host.TryRelocateOwned(netId, destination.Coord, (staged, candidate) =>
                {
                    staged.Set(candidate, ReplicatedPosition.FromWorld(next.Position, destination.Frame));
                    MovementComponents.Set(staged, candidate, next);
                }, out _)) return false;
                boundPlayerCellsVersion++;
            }
        }
        if (teleport && hadCurrent && current.Move.Commitment.IsActive)
            QueueMovementCommitmentEnd(slot, current, MovementCommitmentEndReason.Teleported);
        return true;
    }

    /// <inheritdoc/>
    public bool TryResetToConfiguredSpawn(int slot) =>
        TryGetConfiguredSpawn(slot, out var spawn) && TrySetPlayerState(slot, spawn, teleport: true);
}
