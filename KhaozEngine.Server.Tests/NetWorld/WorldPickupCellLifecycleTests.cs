using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The two halves of a pickup's cell lifecycle, over a real <see cref="ShardedWorldServer"/> with real
/// <see cref="CellPersistence"/> and <see cref="CellEvictor"/> behind it.
/// <para>#326: a pickup is <see cref="Transient"/> the moment it spawns, so it never reaches a cell blob and no
/// restore can resurrect it as a husk, and the same opt-out is reachable by hand for any other server-owned entity
/// (<see cref="ShardedWorldServer.MarkTransient(long)"/>).</para>
/// <para>#374: an evicted cell takes its pickups' TRACKING with it, through the built-in
/// <see cref="CellEvictor.CellEvicted"/> subscription or an explicit <see cref="WorldPickups.ForgetCell"/>, so the
/// seam stops offering an orb nobody can see. The two compose: nothing is offered afterwards, and nothing comes
/// back when the coordinate is recreated.</para>
/// </summary>
public class WorldPickupCellLifecycleTests
{
    private static float Flat(float x, float z) => 0f;

    // CellSize 10, player spawn (5,_,5) -> the player's home cell is (0,0), which is pinned and never evictable.
    // Everything below puts its pickups in (1,0) at x 15, the neighbouring cell, which is.
    private static readonly CellCoord Home = new(0, 0);
    private static readonly CellCoord Next = new(1, 0);

    private static ShardedWorldServerConfig Cfg() => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = _ => new Vector3(5f, 0f, 5f),
    };

    /// <summary>A server with a joined player, so the proximity pass has someone to offer to.</summary>
    private sealed class Rig
    {
        public Rig(WorldPickupsConfig config, bool wireEvictor)
        {
            (INetTransport serverTransport, INetTransport clientTransport) = LoopbackTransport.CreatePair();
            Config = Cfg();
            Store = new InMemoryWorldStore();
            Server = new ShardedWorldServer(serverTransport, Config, Flat, MoveTuning.Default);
            Persistence = new CellPersistence(Server, Store);
            Evictor = new CellEvictor(Server, Persistence);
            Pickups = new WorldPickups(Server,
                wireEvictor ? Rewire(config, Evictor) : config);
            Server.OnBeforeTick += Pickups.Update;
            Client = new NetClient(clientTransport, TestHandshake.Wire(Encoding.UTF8.GetBytes("acct-1")));
            Pump(60);
            Assert.True(Server.TryGetPlayerNetId(Client.Slot, out _));
        }

        public ShardedWorldServerConfig Config { get; }
        public InMemoryWorldStore Store { get; }
        public ShardedWorldServer Server { get; }
        public CellPersistence Persistence { get; }
        public CellEvictor Evictor { get; }
        public WorldPickups Pickups { get; }
        public NetClient Client { get; }

        public void Pump(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Client.Poll();
                Server.Poll();
                Server.Tick(Config.TickSeconds);
            }
        }

        public void MovePlayerTo(Vector3 position)
        {
            Assert.True(Server.TryGetPlayerState(Client.Slot, out PlayerMoveState state));
            state.Position = position;
            Server.SetPlayerState(Client.Slot, state, true);
        }

        // Drives one eviction to completion: request, let the store write land, then the server-thread finalize pass.
        public async Task EvictAsync(CellCoord coord)
        {
            Assert.True(Evictor.RequestEvict(coord));
            await Persistence.FlushAsync();
            Evictor.Update(0f);
        }

        // WorldPickupsConfig is init-only, so a rig that wants the built-in subscription rebuilds it with the
        // evictor attached rather than mutating the one the test wrote.
        private static WorldPickupsConfig Rewire(WorldPickupsConfig c, CellEvictor evictor) => new()
        {
            OnCollect = c.OnCollect,
            OnRemoved = c.OnRemoved,
            DefaultRadius = c.DefaultRadius,
            DefaultTimeToLiveSeconds = c.DefaultTimeToLiveSeconds,
            RetryDeclinedSeconds = c.RetryDeclinedSeconds,
            Evictor = evictor,
        };
    }

    // ---- #326: a pickup is never persisted ----

    [Fact]
    public void ASpawnedPickupIsTransientAndAbsentFromItsCellBlob()
    {
        var rig = new Rig(new WorldPickupsConfig(), wireEvictor: false);
        long netId = rig.Pickups.Spawn(new Vector3(15f, 0f, 5f), payloadId: 42L);
        rig.Pump(4);

        Assert.True(rig.Server.IsTransient(netId));
        ICellPersistenceHost host = rig.Server;
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next));   // entity count 0: the blob never saw it
    }

    [Fact]
    public void APickupDoesNotComeBackFromASnapshotButAnUnmarkedEntityDoes()
    {
        var rig = new Rig(new WorldPickupsConfig(), wireEvictor: false);
        long pickupId = rig.Pickups.Spawn(new Vector3(15f, 0f, 5f), payloadId: 42L);
        long plainId = rig.Server.SpawnEntity(16f, 5f);
        rig.Pump(4);

        ICellPersistenceHost host = rig.Server;
        byte[]? blob = host.SnapshotCell(Next);
        Assert.NotNull(blob);

        // Drop the cell and restore it from its own bytes: the plain entity is back, the pickup never was in there.
        Assert.True(rig.Server.Host.RemoveCell(Next));
        rig.Server.EnsureCell(Next);
        CellRestoreResult restored = host.TryRestoreCell(Next, blob!);
        Assert.True(restored.Ok);
        Assert.Contains(plainId, restored.NetIds);
        Assert.DoesNotContain(pickupId, restored.NetIds);
        Assert.False(rig.Server.TryGetEntity(pickupId, out World _, out Entity _));
    }

    [Fact]
    public void MarkTransientAndClearTransientMoveAnyOwnedEntityInAndOutOfTheBlob()
    {
        var rig = new Rig(new WorldPickupsConfig(), wireEvictor: false);
        long netId = rig.Server.SpawnEntity(15f, 5f);
        rig.Pump(2);

        ICellPersistenceHost host = rig.Server;
        Assert.False(rig.Server.IsTransient(netId));
        Assert.NotEqual(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next));

        Assert.True(rig.Server.MarkTransient(netId));
        Assert.True(rig.Server.IsTransient(netId));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next));

        Assert.True(rig.Server.ClearTransient(netId));
        Assert.False(rig.Server.IsTransient(netId));
        Assert.NotEqual(new byte[] { 0, 0, 0, 0 }, host.SnapshotCell(Next));
    }

    [Fact]
    public void MarkTransientRefusesAPlayerAndAnUnknownNetId()
    {
        var rig = new Rig(new WorldPickupsConfig(), wireEvictor: false);
        Assert.True(rig.Server.TryGetPlayerNetId(rig.Client.Slot, out long playerNetId));

        Assert.False(rig.Server.MarkTransient(playerNetId));   // players persist on their own record, not a cell blob
        Assert.False(rig.Server.IsTransient(playerNetId));
        Assert.False(rig.Server.MarkTransient(999_999L));
        Assert.False(rig.Server.ClearTransient(999_999L));
    }

    // ---- #374: an evicted cell takes the tracking with it ----

    [Fact]
    public async Task AnEvictedCellDropsItsPickupsThroughTheBuiltInSubscription()
    {
        var removals = new List<PickupRemoval>();
        var collects = new List<PickupCollect>();
        var rig = new Rig(new WorldPickupsConfig
        {
            OnCollect = c => { collects.Add(c); return true; },
            OnRemoved = removals.Add,
        }, wireEvictor: true);

        var at = new Vector3(15f, 0f, 5f);
        long netId = rig.Pickups.Spawn(at, payloadId: 42L, radius: 3f);
        rig.Pump(4);
        Assert.True(rig.Pickups.TryGet(netId, out PickupInfo info));
        Assert.Equal(Next, info.Cell);

        await rig.EvictAsync(Next);

        Assert.Equal(1, rig.Evictor.EvictedCount);
        Assert.Equal(0, rig.Pickups.Count);
        Assert.False(rig.Pickups.IsLive(netId));
        Assert.Single(removals);
        Assert.Equal(PickupRemovalReason.CellEvicted, removals[0].Reason);
        Assert.Equal(netId, removals[0].PickupNetId);
        Assert.Equal(42L, removals[0].PayloadId);
        Assert.Equal(-1, removals[0].Slot);

        // Walking onto where it stood offers nothing: the record is gone, so there is nothing left to collect.
        rig.MovePlayerTo(at);
        rig.Pump(8);
        Assert.Empty(collects);
    }

    [Fact]
    public async Task RecreatingTheEvictedCoordResurrectsNoGhostOffer()
    {
        var collects = new List<PickupCollect>();
        var rig = new Rig(new WorldPickupsConfig
        {
            OnCollect = c => { collects.Add(c); return true; },
        }, wireEvictor: true);

        var at = new Vector3(15f, 0f, 5f);
        long netId = rig.Pickups.Spawn(at, payloadId: 42L, radius: 3f);
        rig.Pump(4);
        await rig.EvictAsync(Next);

        // The evicted blob is what a recreate restores from, and it never carried the pickup (#326). Compared against
        // the blob of a cell that genuinely held nothing rather than decoded: the envelope (magic + schema version +
        // body) is coord-independent, so identical bytes mean the pickup's cell was saved as an empty one.
        var empty = new CellCoord(3, 0);
        rig.Server.EnsureCell(empty);
        await rig.EvictAsync(empty);
        byte[]? saved = await rig.Store.LoadAsync("cell:1:0");
        Assert.NotNull(saved);
        Assert.Equal(await rig.Store.LoadAsync("cell:3:0"), saved);

        // Recreate the coordinate the way a player walking in would: the cached snapshot restores inside the create.
        CellSim again = rig.Server.Host.EnsureCell(Next);
        Assert.Equal(1, rig.Evictor.RestoredFromCacheCount);
        Assert.False(again.TryGetOwned(netId, out Entity _));
        Assert.False(rig.Server.TryGetEntity(netId, out World _, out Entity _));
        Assert.Equal(0, rig.Pickups.Count);

        rig.MovePlayerTo(at);
        rig.Pump(8);
        Assert.Empty(collects);
    }

    [Fact]
    public async Task ForgetCellDropsThatCellsPickupsAndLeavesTheRest()
    {
        var removals = new List<PickupRemoval>();
        // No evictor wired: this is the host that unloads cells its own way and calls the seam by hand.
        var rig = new Rig(new WorldPickupsConfig { OnRemoved = removals.Add }, wireEvictor: false);

        long here = rig.Pickups.Spawn(new Vector3(5f, 0f, 5f), payloadId: 1L);
        long there = rig.Pickups.Spawn(new Vector3(15f, 0f, 5f), payloadId: 2L);
        long alsoThere = rig.Pickups.Spawn(new Vector3(18f, 0f, 8f), payloadId: 3L);
        rig.Pump(4);
        Assert.Equal(3, rig.Pickups.Count);

        await rig.EvictAsync(Next);
        Assert.Equal(3, rig.Pickups.Count);            // nothing is subscribed, so the seam has not heard yet

        Assert.Equal(2, rig.Pickups.ForgetCell(Next));
        Assert.Equal(1, rig.Pickups.Count);
        Assert.True(rig.Pickups.IsLive(here));
        Assert.False(rig.Pickups.IsLive(there));
        Assert.False(rig.Pickups.IsLive(alsoThere));
        Assert.Equal(2, removals.Count);
        Assert.All(removals, r => Assert.Equal(PickupRemovalReason.CellEvicted, r.Reason));
        Assert.Equal(new[] { there, alsoThere }, new[] { removals[0].PickupNetId, removals[1].PickupNetId });

        Assert.Equal(0, rig.Pickups.ForgetCell(Next));  // idempotent: nothing left in that cell
    }

    [Fact]
    public void ForgetWhereDropsWhatThePredicateAcceptsAndReportsADespawn()
    {
        var removals = new List<PickupRemoval>();
        var rig = new Rig(new WorldPickupsConfig { OnRemoved = removals.Add }, wireEvictor: false);

        long keep = rig.Pickups.Spawn(new Vector3(5f, 0f, 5f), payloadId: 1L);
        long drop = rig.Pickups.Spawn(new Vector3(15f, 0f, 5f), payloadId: 2L);
        rig.Pump(4);

        Assert.Equal(1, rig.Pickups.ForgetWhere(p => p.PayloadId == 2L));
        Assert.True(rig.Pickups.IsLive(keep));
        Assert.False(rig.Pickups.IsLive(drop));
        Assert.Single(removals);
        Assert.Equal(PickupRemovalReason.Despawned, removals[0].Reason);

        // The cell is on PickupInfo too, so a predicate can express the same rule ForgetCell does.
        Assert.Equal(1, rig.Pickups.ForgetWhere(p => p.Cell == Home));
        Assert.Equal(0, rig.Pickups.Count);
    }

    [Fact]
    public void TrackEvictionsIsIdempotentAndReversible()
    {
        var removals = new List<PickupRemoval>();
        var rig = new Rig(new WorldPickupsConfig { OnRemoved = removals.Add }, wireEvictor: true);

        Assert.False(rig.Pickups.TrackEvictions(rig.Evictor));   // the config already subscribed it
        rig.Pickups.Spawn(new Vector3(15f, 0f, 5f), payloadId: 1L);
        rig.Pump(4);

        Assert.True(rig.Pickups.StopTrackingEvictions(rig.Evictor));
        Assert.False(rig.Pickups.StopTrackingEvictions(rig.Evictor));
        Assert.True(rig.Pickups.TrackEvictions(rig.Evictor));
    }

    [Fact]
    public void ASingleWorldServerHasNoCellsSoAPickupBelongsToNone()
    {
        // IWorldPickupHost.TryGetCellCoord's default answer, and what it means downstream: nothing to forget.
        (INetTransport serverTransport, INetTransport _) = LoopbackTransport.CreatePair();
        var server = new WorldServer(serverTransport, new WorldServerConfig
        {
            TickSeconds = 1f / 30f,
            MaxPlayers = 4,
            InterestRadius = 50f,
            SpawnPosition = _ => new Vector3(5f, 0f, 5f),
        }, Flat, MoveTuning.Default);
        var pickups = new WorldPickups(server);

        long netId = pickups.Spawn(new Vector3(15f, 0f, 5f), payloadId: 1L);
        Assert.True(pickups.TryGet(netId, out PickupInfo info));
        Assert.Null(info.Cell);
        Assert.Equal(0, pickups.ForgetCell(Next));
        Assert.Equal(1, pickups.Count);
    }
}
