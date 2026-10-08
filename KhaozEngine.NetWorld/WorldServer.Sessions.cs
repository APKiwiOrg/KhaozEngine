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
        tickedSlots.Remove(slot);
        replication.Forget(slot);
        Vector3 spawn = JoinSpawn(slot, persistenceKey);
        PlayerMoveState basis = ToIsland(new PlayerMoveState { Position = spawn });
        bool admitted;
        using (var read = simulator.BeginExplicitRead(basis))
        {
            admitted = read?.BasisValid != false;
            if (admitted)
            {
                PlayerMoveState state = simulator.Step(basis, MoveCommand.Idle, config.TickSeconds);
                admitted = simulator.LastExplicitOutcome is null or MovementStepOutcome.Advanced or MovementStepOutcome.Blocked;
                if (admitted)
                {
                    long netId = allocator.Next().Value;
                    Entity entity = world.Spawn();
                    world.Set(entity, new NetId(netId));
                    world.Set(entity, ReplicatedPosition.InFrame(islandFrame, state.Position));
                    MovementComponents.Set(world, entity, state);
                    netIdBySlot[slot] = netId;
                    entityBySlot[slot] = entity;
                    stateBySlot[slot] = state;
                    lastAckBySlot[slot] = -1;
                    accountIdBySlot[slot] = accountId;
                    RateLimiter? limiter = config.AntiCheat.CreateLimiter(config.TickSeconds);
                    if (limiter is not null) rateBySlot[slot] = limiter; else rateBySlot.Remove(slot);
                    correctionStreakBySlot[slot] = 0;
                    if (!string.IsNullOrEmpty(displayName)) world.Set(entity, new PlayerIdentity { DisplayName = displayName });
                }
            }
        }
        if (!admitted) { net.Disconnect(slot); return; }
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
            tickedSlots.Remove(slot);
            replication.Left(slot);   // no longer pending a writer restart, stream dropped
            commands.Forget(slot);
        }
    }
}
