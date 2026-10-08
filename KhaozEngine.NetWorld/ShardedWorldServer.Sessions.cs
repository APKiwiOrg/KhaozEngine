using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

public sealed partial class ShardedWorldServer
{
    private void OnJoin(int slot, string subject, string displayName, string verifiedPersistenceKey)
    {
        // NetServer seats a returning account on the slot its lingering body holds. That body leaves first through
        // the ordinary path, so its save is published before this join's load and the resume hint seats the new body
        // where it stood. OnLeave's ReleaseHeldSlot is a no-op on a reclaim, because the slot is reseated, no longer
        // held. On a recycled slot whose newcomer already dropped in this Poll, the hold was asked for the old body,
        // and the release frees the newcomer's hold, so the newcomer's own Left then leaves at once.
        if (IsLingering(slot)) OnLeave(slot);
        if (ReservedSubjectGuard.IsReserved(subject, slot)) { net.Disconnect(slot); return; }
        string accountId = string.IsNullOrEmpty(subject) ? $"{ResumePositionCache.GuestAccountPrefix}{slot}" : subject;
        if (banStore is not null && banStore.IsBanned(accountId))
        {
            SendNoticeTo(slot, new ServerNotice(ServerNoticeKind.Banned, string.Empty));
            net.Disconnect(slot);
            return;
        }
        if (!TryBindPersistenceKey(slot, accountId, verifiedPersistenceKey, out string persistenceKey))
        {
            net.Disconnect(slot);
            return;
        }

        commands.Forget(slot);
        deltaReplicator?.Forget(slot);
        deltaCapableSlots.Remove(slot);
        tickedSlots.Remove(slot);
        replication.Forget(slot);
        Vector3 spawn = JoinSpawn(slot, persistenceKey);
        using (var read = RuntimeFor(host.CellFor(spawn.X, spawn.Z)).BeginPlacement(
            new PlayerMoveState { Position = spawn }, config.TickSeconds, out PlayerMoveState state, out bool accepted))
        {
            if (!accepted)
            {
                ReleasePersistenceKey(slot);
                net.Disconnect(slot);
                return;
            }
            long netId = allocator.Next().Value;
            Entity entity = host.SpawnOwned(state.Position.X, state.Position.Z, netId, out CellSim cell);
            cell.World.Set(entity, ReplicatedPosition.FromWorld(state.Position, cell.Frame));
            MovementComponents.Set(cell.World, entity, state);
            if (!string.IsNullOrEmpty(displayName))
                cell.World.Set(entity, new PlayerIdentity { DisplayName = displayName });
            EnsureWired(cell);
            netIdBySlot[slot] = netId;
            slotByNetId[netId] = slot;
            lastAckBySlot[slot] = -1;
            accountIdBySlot[slot] = accountId;
            RateLimiter? limiter = config.AntiCheat.CreateLimiter(config.TickSeconds);
            if (limiter is not null) rateBySlot[slot] = limiter; else rateBySlot.Remove(slot);
            correctionStreakBySlot[slot] = 0;
            host.BindClient(slot, netId);
            boundPlayerCellsVersion++;
        }
        PlayerJoined?.Invoke(slot, accountId);
    }

    private void OnLeave(int slot)
    {
        bool joined = netIdBySlot.TryGetValue(slot, out long netId);
        try
        {
            if (joined)
            {
                desiredSpeedScaleByNetId.Remove(netId);
                if (TryGetPlayerState(slot, out PlayerMoveState final))
                {
                    if (final.Move.Commitment.IsActive)
                    {
                        MovementCommitmentEnded?.Invoke(Result(slot, final, MovementCommitmentEndReason.Disconnected));
                        final.Move.Commitment = default;
                    }
                    if (accountIdBySlot.TryGetValue(slot, out string? account))
                        PlayerLeaving?.Invoke(slot, account, final);
                }
            }
        }
        finally
        {
            ReleasePersistenceKey(slot);
            if (joined && host.TryGetOwner(netId, out CellSim cell, out Entity entity) && cell.World.IsAlive(entity))
            {
                cell.UnregisterOwned(netId);
                cell.World.Despawn(entity);
            }
            host.UnbindClient(slot);
            boundPlayerCellsVersion++;
            if (joined) slotByNetId.Remove(netId);
            netIdBySlot.Remove(slot);
            lastAckBySlot.Remove(slot);
            accountIdBySlot.Remove(slot);
            rateBySlot.Remove(slot);
            correctionStreakBySlot.Remove(slot);
            selfRescueReadyAt.Remove(slot);
            deltaReplicator?.Forget(slot);
            deltaCapableSlots.Remove(slot);
            tickedSlots.Remove(slot);
            replication.Left(slot);   // no longer pending a writer restart, stream dropped
            commands.Forget(slot);
            if (lingerUntilBySlot.Remove(slot)) net.ReleaseHeldSlot(slot);   // last, after the despawn
        }
    }

    private static bool PositionAccessor(World world, Entity entity, out float x, out float y)
    {
        if (world.TryGet(entity, out ReplicatedPosition position))
        {
            x = position.Value.X;
            y = position.Value.Z;
            return true;
        }
        x = y = 0f;
        return false;
    }
}
