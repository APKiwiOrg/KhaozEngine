using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

public sealed partial class WorldServer
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
        PlayerMoveState state = simulator.Step(ToIsland(new PlayerMoveState { Position = spawn }),
            MoveCommand.Idle, config.TickSeconds);
        long netId = allocator.Next().Value;
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(netId));
        world.Set(entity, ReplicatedPosition.InFrame(islandFrame, state.Position));
        world.Set(entity, MovementState.From(state));
        netIdBySlot[slot] = netId;
        entityBySlot[slot] = entity;
        stateBySlot[slot] = state;
        lastAckBySlot[slot] = -1;
        accountIdBySlot[slot] = accountId;
        RateLimiter? limiter = config.AntiCheat.CreateLimiter(config.TickSeconds);
        if (limiter is not null) rateBySlot[slot] = limiter; else rateBySlot.Remove(slot);
        correctionStreakBySlot[slot] = 0;
        if (!string.IsNullOrEmpty(displayName)) world.Set(entity, new PlayerIdentity { DisplayName = displayName });
        PlayerJoined?.Invoke(slot, accountId);
    }

    private void OnLeave(int slot)
    {
        try
        {
            if (stateBySlot.TryGetValue(slot, out PlayerMoveState final))
            {
                PlayerMoveState absolute = ToAbsolute(final);
                if (absolute.Move.Commitment.IsActive)
                {
                    MovementCommitmentEnded?.Invoke(Result(slot, absolute, MovementCommitmentEndReason.Disconnected));
                    absolute.Move.Commitment = default;
                }
                if (accountIdBySlot.TryGetValue(slot, out string? account))
                    PlayerLeaving?.Invoke(slot, account, absolute);
            }
        }
        finally
        {
            ReleasePersistenceKey(slot);
            if (entityBySlot.TryGetValue(slot, out Entity entity) && world.IsAlive(entity)) world.Despawn(entity);
            netIdBySlot.Remove(slot);
            entityBySlot.Remove(slot);
            stateBySlot.Remove(slot);
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
}
