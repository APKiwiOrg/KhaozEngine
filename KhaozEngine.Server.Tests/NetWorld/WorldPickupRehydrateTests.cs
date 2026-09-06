using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldPickupRehydrateTests
{
    private sealed class Host : IWorldPickupHost
    {
        private readonly Dictionary<long, Entity> owned = new();
        private readonly int[] slots = { 0 };

        public World World { get; } = new();
        public IReadOnlyCollection<int> JoinedSlots => slots;
        public long PlayerNetId { get; } = 900;
        public PlayerMoveState PlayerState = new() { Position = new Vector3(5f, 2f, 5f) };

        public bool TryGetPlayerNetId(int slot, out long netId)
        {
            netId = PlayerNetId;
            return slot == 0;
        }

        public bool TryGetPlayerState(int slot, out PlayerMoveState state)
        {
            state = PlayerState;
            return slot == 0;
        }

        public long SpawnEntity(float x, float z, Action<World, Entity>? configure = null) =>
            throw new NotSupportedException();

        public bool TryGetEntity(long netId, out World world, out Entity entity)
        {
            world = World;
            return owned.TryGetValue(netId, out entity) && World.IsAlive(entity);
        }

        public bool DespawnEntity(long netId)
        {
            if (!owned.Remove(netId, out Entity entity)) return false;
            World.Despawn(entity);
            return true;
        }

        public bool TryGetCellCoord(float x, float z, out CellCoord coord)
        {
            coord = new CellCoord((int)MathF.Floor(x / 10f), (int)MathF.Floor(z / 10f));
            return true;
        }

        public Entity Add(long netId, Vector3 position, long payloadId, long ownerNetId = 0, bool own = true)
        {
            Entity entity = World.Spawn();
            World.Set(entity, new NetId(netId));
            World.Set(entity, ReplicatedPosition.FromWorld(position, WorldFrame.Origin));
            World.Set(entity, new PickupState { PayloadId = payloadId, OwnerNetId = ownerNetId });
            if (own) owned[netId] = entity;
            return entity;
        }
    }

    [Fact]
    public async Task Async_cell_restore_event_rehydrates_after_real_host_ownership_is_available()
    {
        var store = new InMemoryWorldStore();
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = 0.1f,
            CellSize = 10f,
            OverlapMargin = 2f,
            InterestRadius = 2f,
        };
        (INetTransport sourceTransport, _) = LoopbackTransport.CreatePair();
        using (var source = new ShardedWorldServer(sourceTransport, config, static (_, _) => 0f, MoveTuning.Default))
        {
            var sourcePickups = new WorldPickups(source);
            long id = sourcePickups.Spawn(new Vector3(15f, 2f, 5f), payloadId: 123, ownerNetId: 456);
            Assert.True(source.ClearTransient(id));
            var writer = new CellPersistence(source, store);
            writer.SaveDirtyPass();
            await writer.FlushAsync();
        }

        var gated = new GatedWorldStore(store);
        (INetTransport destinationTransport, _) = LoopbackTransport.CreatePair();
        using var destination = new ShardedWorldServer(
            destinationTransport, config, static (_, _) => 0f, MoveTuning.Default);
        var pickups = new WorldPickups(destination);
        var persistence = new CellPersistence(destination, gated);
        CellCoord coord = destination.Host.CoordFor(15f, 5f);
        int events = 0;
        int adopted = 0;
        persistence.CellRestoreApplied += applied =>
        {
            events++;
            Assert.Equal(coord, applied.Coord);
            Assert.True(destination.Host.TryGetCell(coord, out CellSim cell));
            Assert.True(destination.TryGetEntity(applied.NetIds[0], out World ownerWorld, out _));
            Assert.Same(cell.World, ownerWorld);
            adopted += pickups.Rehydrate(cell.World);
        };

        destination.Host.EnsureCell(coord);
        Assert.Equal(1, gated.PendingLoads);
        gated.ReleaseLoads();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await gated.WaitForCompletedLoadAsync(timeout.Token);

        var elapsed = Stopwatch.StartNew();
        while (events == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            persistence.Update(0f);
            if (events == 0) await Task.Delay(1);
        }

        Assert.Equal(1, events);
        Assert.Equal(1, adopted);
        Assert.True(pickups.TryGet(pickups.LiveNetIds[0], out PickupInfo info));
        long restoredId = info.NetId;
        Assert.Equal(123, info.PayloadId);
        Assert.Equal(456, info.OwnerNetId);
        Assert.Equal(new Vector3(15f, 2f, 5f), info.Position);

        long freshId = pickups.Spawn(new Vector3(16f, 2f, 5f), payloadId: 789);
        Assert.True(freshId > restoredId);
        Assert.True(destination.TryGetEntity(restoredId, out World restoredWorld, out Entity restoredEntity));
        Assert.True(destination.TryGetEntity(freshId, out World freshWorld, out Entity freshEntity));
        Assert.Same(restoredWorld, freshWorld);
        Assert.NotEqual(restoredEntity, freshEntity);
    }

    [Fact]
    public void Rehydrate_adopts_once_with_persisted_state_and_fresh_runtime_defaults()
    {
        var host = new Host();
        Vector3 position = new(5f, 2f, 5f);
        host.Add(41, position, payloadId: 712, ownerNetId: 99);
        var removed = new List<PickupRemoval>();
        var pickups = new WorldPickups(host, new WorldPickupsConfig
        {
            DefaultRadius = 2.5f,
            DefaultTimeToLiveSeconds = 3f,
            OnRemoved = removed.Add,
        });

        Assert.Equal(1, pickups.Rehydrate(host.World));
        Assert.Equal(0, pickups.Rehydrate(host.World));
        Assert.True(pickups.TryGet(41, out PickupInfo info));
        Assert.Equal(712, info.PayloadId);
        Assert.Equal(99, info.OwnerNetId);
        Assert.Equal(position, info.Position);
        Assert.Equal(2.5f, info.Radius);
        Assert.Equal(3f, info.TimeToLiveSeconds);
        Assert.Equal(0f, info.AgeSeconds);
        Assert.Equal(new CellCoord(0, 0), info.Cell);

        pickups.Update(1f);
        Assert.Equal(0, pickups.Rehydrate(host.World));
        Assert.True(pickups.TryGet(41, out info));
        Assert.Equal(1f, info.AgeSeconds);
        pickups.Update(2f);

        Assert.False(pickups.IsLive(41));
        Assert.Single(removed);
        Assert.Equal(PickupRemovalReason.Expired, removed[0].Reason);
        Assert.False(host.TryGetEntity(41, out _, out _));
    }

    [Fact]
    public void Rehydrated_pickup_is_offered_with_empty_history_and_can_be_collected()
    {
        var host = new Host();
        host.Add(51, host.PlayerState.Position, payloadId: 888, ownerNetId: host.PlayerNetId);
        var offers = new List<PickupCollect>();
        var pickups = new WorldPickups(host, new WorldPickupsConfig
        {
            DefaultRadius = 1f,
            OnCollect = offer => { offers.Add(offer); return true; },
        });

        Assert.Equal(1, pickups.Rehydrate(host.World));
        pickups.Update(0f);

        Assert.Single(offers);
        Assert.Equal(888, offers[0].PayloadId);
        Assert.False(pickups.IsLive(51));
        Assert.False(host.TryGetEntity(51, out _, out _));
    }

    [Fact]
    public void Rehydrated_pickups_support_explicit_despawn_and_cell_forget()
    {
        var host = new Host();
        host.Add(61, new Vector3(5f, 0f, 5f), payloadId: 1);
        host.Add(62, new Vector3(15f, 0f, 5f), payloadId: 2);
        var removals = new List<PickupRemoval>();
        var pickups = new WorldPickups(host, new WorldPickupsConfig { OnRemoved = removals.Add });

        Assert.Equal(2, pickups.Rehydrate(host.World));
        Assert.True(pickups.Despawn(61));
        Assert.Equal(1, pickups.ForgetCell(new CellCoord(1, 0)));

        Assert.Equal(0, pickups.Count);
        Assert.Equal(PickupRemovalReason.Despawned, removals[0].Reason);
        Assert.Equal(PickupRemovalReason.CellEvicted, removals[1].Reason);
        Assert.False(host.TryGetEntity(61, out _, out _));
        Assert.False(host.TryGetEntity(62, out _, out _));
    }

    [Fact]
    public void Rehydrate_skips_invalid_unowned_and_duplicate_entities()
    {
        var host = new Host();
        Entity valid = host.Add(71, new Vector3(5f, 0f, 5f), payloadId: 1);
        host.Add(71, new Vector3(6f, 0f, 5f), payloadId: 2, own: false);
        host.Add(72, new Vector3(float.NaN, 0f, 5f), payloadId: 3);
        host.Add(73, new Vector3(5f, 0f, 5f), payloadId: 4, own: false);
        host.Add(0, new Vector3(5f, 0f, 5f), payloadId: 5);
        Entity missingPosition = host.World.Spawn();
        host.World.Set(missingPosition, new NetId(74));
        host.World.Set(missingPosition, new PickupState { PayloadId = 6 });

        var pickups = new WorldPickups(host);

        Assert.Equal(1, pickups.Rehydrate(host.World));
        Assert.Equal(new long[] { 71 }, pickups.LiveNetIds);
        Assert.True(host.TryGetEntity(71, out _, out Entity owner));
        Assert.Equal(valid, owner);
    }

    [Fact]
    public void Rehydrate_requires_a_world()
    {
        var pickups = new WorldPickups(new Host());
        Assert.Throws<ArgumentNullException>(() => pickups.Rehydrate(null!));
    }
}
