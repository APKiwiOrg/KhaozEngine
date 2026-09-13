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
        Vector3 spawn = JoinSpawn(slot, persistenceKey);
        PlayerMoveState state = RuntimeFor(host.CellFor(spawn.X, spawn.Z))
            .SpawnClamp(new PlayerMoveState { Position = spawn }, config.TickSeconds);
        long netId = allocator.Next().Value;
        Entity entity = host.SpawnOwned(state.Position.X, state.Position.Z, netId, out CellSim cell);
        cell.World.Set(entity, ReplicatedPosition.FromWorld(state.Position, cell.Frame));
        cell.World.Set(entity, MovementState.From(state));
        if (!string.IsNullOrEmpty(displayName))
            cell.World.Set(entity, new PlayerIdentity { DisplayName = displayName });
        EnsureWired(cell);
        netIdBySlot[slot] = netId;
        lastAckBySlot[slot] = -1;
        accountIdBySlot[slot] = accountId;
        RateLimiter? limiter = config.AntiCheat.CreateLimiter(config.TickSeconds);
        if (limiter is not null) rateBySlot[slot] = limiter; else rateBySlot.Remove(slot);
        correctionStreakBySlot[slot] = 0;
        host.BindClient(slot, netId);
        boundPlayerCellsVersion++;
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
            netIdBySlot.Remove(slot);
            lastAckBySlot.Remove(slot);
            accountIdBySlot.Remove(slot);
            rateBySlot.Remove(slot);
            correctionStreakBySlot.Remove(slot);
            selfRescueReadyAt.Remove(slot);
            deltaReplicator?.Forget(slot);
            deltaCapableSlots.Remove(slot);
            commands.Forget(slot);
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
