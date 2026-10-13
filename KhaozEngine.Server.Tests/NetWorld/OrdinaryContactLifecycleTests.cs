using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using System.Collections.Generic;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothHeadsSettleSavedResumeRestoreTeleportAndResetAtOffsetZero(bool sharded)
    {
        using var heads = new OrdinaryNetworkHeads(sharded, resumeY: 0.76f);
        Assert.Equal(0.75f, heads.State.Position.Y);
        var restored = heads.State;
        restored.Move.Position.Y = 0.76f;
        Assert.True(heads.Place(restored));
        Assert.Equal(0.75f, heads.State.Position.Y);
        restored.Move.Position.X += 1;
        Assert.True(heads.Place(restored, teleport: true));
        Assert.Equal(0.75f, heads.State.Position.Y);
        Assert.True(heads.Reset());
        Assert.Equal(0.75f, heads.State.Position.Y);
        Assert.True(heads.Client.Joined);
    }

    [Fact]
    public void ShardedCrossCellTeleportRestoreAndTransferredContinuationAreAdmittedBeforePublication()
    {
        using var heads = new OrdinaryNetworkHeads(true);
        var server = heads.Sharded!;
        var target = heads.State;
        target.Position = new(260, 0.76f, 4);
        Assert.True(heads.Place(target, teleport: true));
        Assert.Equal(0.75f, heads.State.Position.Y);
        Assert.True(server.TryGetPlayerNetId(0, out long player));
        Assert.True(server.Host.TryGetOwner(player, out var owner, out _));
        Assert.Same(server.Host.CellFor(260, 4), owner);
        var continued = target;
        continued.Move.Position.Y = 2;
        continued.Move.Grounded = false;
        continued.Move.JumpBufferRemaining = 0.125f;
        continued.Move.VerticalVelocity = -2;
        byte[] transfer = Bytes(server.Registry, continued, 99, ReplicationChannels.Migrate);
        Assert.Contains(99L, owner.AdoptFromMigrate(transfer));
        Assert.True(owner.TryGetOwned(99, out var entity));
        var actual = PlayerMoveState.From(owner.World.Get<ReplicatedPosition>(entity), owner.World.Get<MovementState>(entity),
            owner.World.Get<MovementOwnerState>(entity)).Absolute;
        Assert.Equal(continued.Position, actual.Position);
        Assert.Equal(continued.Move.JumpBufferRemaining, actual.Move.JumpBufferRemaining);
        var cold = continued;
        cold.Move.Position.Y = 0.76f;
        cold.Move.VerticalVelocity = 0;
        Assert.Contains(100L, server.RestoreCell(owner.Coord, Bytes(server.Registry, cold, 100, ReplicationChannels.Persist)));
        Assert.True(owner.TryGetOwned(100, out entity));
        Assert.Equal(0.75f, owner.World.Get<ReplicatedPosition>(entity).Value.Y);
        var before = heads.State;
        var forbidden = before;
        forbidden.Move.Position.X = 400000;
        Assert.False(heads.Place(forbidden, teleport: true));
        Assert.Equal(before, heads.State);
        Assert.True(server.Host.TryGetOwner(player, out var afterOwner, out _));
        Assert.Same(owner, afterOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpatialWadeShoreAdmitsOffsetZeroAndInclusiveDepthLineRefusesAtomically(bool sharded)
    {
        using var field = new OrdinaryShoreField();
        var tuning = OrdinaryPlayerWorld.Tuning with { Water = new(WaterTraversalMode.WadeOnly) };
        using var heads = new OrdinaryNetworkHeads(sharded, tuning: tuning, field: field, spawn: new(0.75f, 0.75f, 4));
        var before = heads.State;
        Assert.Equal(0.75f, before.Position.Y);
        Assert.True(heads.Place(before, teleport: true));
        Assert.True(heads.Reset());
        before = heads.State;
        var forbidden = before;
        forbidden.Position = new(4, 1f + 0.75f * (1f - 2f * 0.65f), 4);
        Assert.False(heads.Place(forbidden, teleport: true));
        Assert.Equal(before, heads.State);
        if (!sharded) return;
        var server = heads.Sharded!;
        Assert.True(server.TryGetPlayerNetId(0, out long player));
        Assert.True(server.Host.TryGetOwner(player, out var sourceOwner, out _));
        var staged = new World();
        foreach (var (id, state) in new[] { (99L, before), (100L, forbidden) })
        {
            var entity = staged.Spawn();
            staged.Set(entity, new NetId(id));
            staged.Set(entity, ReplicatedPosition.FromWorld(state.Position, default));
            MovementComponents.Set(staged, entity, state);
        }
        var bytes = SnapshotWriter.WriteFiltered(staged, server.Registry, new HashSet<long> { 99, 100 }, ReplicationChannels.Persist);
        Assert.False(server.TryRestoreCell(sourceOwner.Coord, bytes).Ok);
        Assert.False(sourceOwner.TryGetOwned(99, out _));
        Assert.False(sourceOwner.TryGetOwned(100, out _));
        Assert.True(server.Host.TryGetOwner(player, out var ownerAfter, out _));
        Assert.Same(sourceOwner, ownerAfter);
        Assert.Equal(before, heads.State);
    }

    static byte[] Bytes(ReplicationRegistry registry, PlayerMoveState state, long id, ReplicationChannels channel)
    {
        var world = new World();
        var entity = world.Spawn();
        world.Set(entity, new NetId(id));
        world.Set(entity, ReplicatedPosition.FromWorld(state.Position, default));
        MovementComponents.Set(world, entity, state);
        return SnapshotWriter.WriteFiltered(world, registry, new HashSet<long> { id }, channel);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EveryOrdinaryLifecycleDoorUsesOffsetZero(bool sharded, bool roof)
    {
        using var heads = new OrdinaryNetworkHeads(sharded, roof: roof);
        var spawned = heads.State;
        Assert.Equal(0.75f, spawned.Position.Y);
        var next = spawned;
        next.Position = new(1, 0.75f, 0);
        Assert.True(heads.Place(next));
        Assert.Equal(0.75f, heads.State.Position.Y);
        Assert.True(heads.Place(next, teleport: true));
        Assert.Equal(0.75f, heads.State.Position.Y);
        Assert.True(heads.Reset());
        var reset = heads.State;
        Assert.Equal(0.75f, reset.Position.Y);
        var forbidden = reset;
        forbidden.Position = new(sharded ? 400000 : 21, 0.75f, 0);
        Assert.False(heads.Place(forbidden, teleport: true));
        Assert.Equal(reset, heads.State);
    }
}
