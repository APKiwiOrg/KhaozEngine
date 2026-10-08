using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

// The continuous position lever. Teleport is the other one, and the pair is documented on IAdminControllable: the
// two queue the identical placement and differ only in whether the teleport epoch advances, which is the client's
// signal to cut. Held in its own partial so both heads carry the identical seam and the frame-loop files stay
// inside the file-size ratchet. See ShardedWorldServer.Placement.cs for the multi-cell twin.
public sealed partial class WorldServer
{
    /// <inheritdoc/>
    public void SetPosition(PlayerRef target, Vector3 position) =>
        admin.Enqueue(new AdminCommand { Kind = AdminCommandKind.SetPosition, Target = target, Position = position });
    /// <summary>Overrides a joined player's authoritative state (and its replicated position). Used by
    /// load-on-join to place the player at the saved position, and by admin/self-rescue teleports; no-op for an
    /// unknown slot. When <paramref name="teleport"/> is true the player's monotonic teleport epoch is advanced
    /// (from the server-held value, ignoring any epoch on the incoming state) so the client cuts to the new position
    /// instead of gliding; otherwise the current epoch is preserved. The per-tick movement path bypasses this and
    /// never advances the epoch.
    /// <para>The incoming position is ABSOLUTE world metres unless the state carries a
    /// <see cref="PlayerMoveState.FrameAnchor"/> saying otherwise (one read back from this server does not: it is
    /// handed out absolute). A framed island converts it in, exactly.</para></summary>
    public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false) =>
        TrySetPlayerState(slot, state, teleport);

    /// <summary>Publishes only an admitted destination. False leaves pose, epoch and events unchanged.</summary>
    public bool TrySetPlayerState(int slot, in PlayerMoveState state, bool teleport = false)
    {
        if (!entityBySlot.TryGetValue(slot, out Entity e)) return false;
        uint baseEpoch = TeleportEpochGuard.BaseEpoch(stateBySlot, slot);
        PlayerMoveState next = ToIsland(state);
        if (teleport)
        {
            next.Move.Commitment = default;
            if (config.ExplicitMovement is not null)
            {
                next.Move = MovementStepResult.HoldState(next.Move);
                next.Move.WaterExcursion = WaterExcursionState.None;
                next.Move.Swimming = false;
                next.Move.Grounded = false;
                next.Move.VerticalVelocity = 0;
                next.Move.HorizontalVelocity = Vector2.Zero;
            }
        }
        using (var read = simulator.BeginExplicitRead(next))
        {
            if (read?.BasisValid == false) return false;
            if (read?.Queries is { } queries)
            {
                var placement = ExplicitCharacterMovement.SettlePlacement(new(next.Move, queries.Frame, null),
                    tuning, config.ExplicitMovement!.Water, queries, read.Boundary);
                if (placement.Outcome != MovementStepOutcome.Advanced) return false;
                next.Move = placement.State.State;
            }
            if (teleport && stateBySlot.TryGetValue(slot, out PlayerMoveState current) && current.Move.Commitment.IsActive)
                QueueMovementCommitmentEnd(slot, current, MovementCommitmentEndReason.Teleported);
            next.TeleportEpoch = teleport ? baseEpoch + 1u : baseEpoch;
            stateBySlot[slot] = next;
            world.Set(e, ReplicatedPosition.InFrame(islandFrame, next.Position));
            MovementComponents.Set(world, e, next);
        }
        return true;
    }

    /// <inheritdoc/>
    public bool TryResetToConfiguredSpawn(int slot) =>
        TryGetConfiguredSpawn(slot, out var spawn) && TrySetPlayerState(slot, spawn, teleport: true);
}
