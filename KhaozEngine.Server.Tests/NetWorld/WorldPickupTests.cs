using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// End-to-end coverage of the world-pickup seam (<see cref="WorldPickups"/> over <see cref="IWorldPickupHost"/>):
/// spawn + replication of <see cref="PickupState"/>, the time-to-live, the once-per-entry offer policy and its three
/// re-offer routes, the owner tag, explicit despawn, and the wire-generation gate the new built-in component forced.
/// Every behavioural test is a theory over BOTH server types, so single-world and sharded parity is asserted on every
/// case rather than in one token parity test.
/// </summary>
public class WorldPickupTests
{
    private static float Flat(float x, float z) => 0f;

    private static ShardedWorldServerConfig SmallCells() => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = _ => new Vector3(5f, 0f, 5f),   // player home cell (0,0)
    };

    private static WorldServerConfig SingleWorld() => new()
    {
        TickSeconds = 1f / 30f,
        MaxPlayers = 8,
        InterestRadius = 50f,
        SpawnPosition = _ => new Vector3(5f, 0f, 5f),
    };

    /// <summary>One wired-up server + client + seam, behind the members every test below needs, so the same test body
    /// runs against <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/>.</summary>
    private sealed class Rig
    {
        public Rig(IWorldPickupHost host, WorldPickups pickups, WorldClient client,
            Action<int> pump, Action<int, Vector3> movePlayer, float tickSeconds)
        {
            Host = host;
            Pickups = pickups;
            Client = client;
            Pump = pump;
            MovePlayer = movePlayer;
            TickSeconds = tickSeconds;
        }

        public IWorldPickupHost Host { get; }
        public WorldPickups Pickups { get; }
        public WorldClient Client { get; }
        public Action<int> Pump { get; }
        public Action<int, Vector3> MovePlayer { get; }
        public float TickSeconds { get; }

        /// <summary>The joined player's authoritative position (what the proximity test measures against).</summary>
        public Vector3 PlayerPosition
        {
            get
            {
                Assert.True(Host.TryGetPlayerState(0, out PlayerMoveState state));
                return state.Position;
            }
        }

        public long PlayerNetId
        {
            get
            {
                Assert.True(Host.TryGetPlayerNetId(0, out long netId));
                return netId;
            }
        }

        public bool ClientSees(long netId)
        {
            foreach (EntityRenderState e in Client.Snapshot())
                if (e.Id.Value == netId) return true;
            return false;
        }
    }

    // Builds the rig and pumps it until the client has joined, so every test starts from a live player whose
    // authoritative position is readable. Deterministic: Poll/Tick/Poll, no wall clock and no threads.
    private static Rig NewRig(bool sharded, WorldPickupsConfig config)
    {
        Rig rig = sharded ? NewShardedRig(config) : NewSingleWorldRig(config);
        rig.Pump(8);
        Assert.True(rig.Client.Joined);
        return rig;
    }

    private static Rig NewShardedRig(WorldPickupsConfig config)
    {
        (INetTransport serverTransport, INetTransport clientTransport) = LoopbackTransport.CreatePair();
        ShardedWorldServerConfig cfg = SmallCells();
        var server = new ShardedWorldServer(serverTransport, cfg, Flat, MoveTuning.Default);
        var pickups = new WorldPickups(server, config);
        server.OnBeforeTick += pickups.Update;   // the seam is driven by the consumer, never by the engine
        var client = new WorldClient(clientTransport, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds, InterpolateRemotes = false });
        return new Rig(server, pickups, client,
            frames => { for (int i = 0; i < frames; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); } },
            (slot, position) => Place(server.TryGetPlayerState, (s, st) => server.SetPlayerState(s, st, true), slot, position),
            cfg.TickSeconds);
    }

    private static Rig NewSingleWorldRig(WorldPickupsConfig config)
    {
        (INetTransport serverTransport, INetTransport clientTransport) = LoopbackTransport.CreatePair();
        WorldServerConfig cfg = SingleWorld();
        var server = new WorldServer(serverTransport, cfg, Flat, MoveTuning.Default);
        var pickups = new WorldPickups(server, config);
        server.OnBeforeTick += pickups.Update;
        var client = new WorldClient(clientTransport, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds, InterpolateRemotes = false });
        return new Rig(server, pickups, client,
            frames => { for (int i = 0; i < frames; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); } },
            (slot, position) => Place(server.TryGetPlayerState, (s, st) => server.SetPlayerState(s, st, true), slot, position),
            cfg.TickSeconds);
    }

    private delegate bool TryGetState(int slot, out PlayerMoveState state);

    private static void Place(TryGetState read, Action<int, PlayerMoveState> write, int slot, Vector3 position)
    {
        Assert.True(read(slot, out PlayerMoveState state));
        state.Position = position;
        write(slot, state);
    }

    // ---- spawn + replication ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Spawn_replicates_the_pickup_and_the_client_reads_its_payload_and_owner(bool sharded)
    {
        Rig rig = NewRig(sharded, new WorldPickupsConfig());
        Vector3 at = rig.PlayerPosition + new Vector3(1f, 0.5f, 0f);
        long owner = rig.PlayerNetId;

        long netId = rig.Pickups.Spawn(at, payloadId: 0x00040001L, ownerNetId: owner, radius: 2f);
        rig.Pump(6);

        Assert.Equal(1, rig.Pickups.Count);
        Assert.True(rig.Pickups.IsLive(netId));
        Assert.NotEqual(netId, rig.Client.LocalNetId);     // a fresh id off the same allocator, never a player's
        Assert.True(rig.ClientSees(netId));

        Assert.True(rig.Client.TryGetComponent(netId, out PickupState state));
        Assert.Equal(0x00040001L, state.PayloadId);        // the opaque payload arrives verbatim
        Assert.Equal(owner, state.OwnerNetId);
        Assert.True(state.IsOwned);
        Assert.True(state.AllowsCollectBy(owner));
        Assert.False(state.AllowsCollectBy(owner + 1));

        // The full Y is honoured rather than flattened to the ground plane by SpawnEntity's (x, 0, z) pre-set.
        Assert.True(rig.Client.TryGetComponent(netId, out ReplicatedPosition replicated));
        Assert.Equal(at.Y, replicated.Value.Y, 3);

        Assert.True(rig.Pickups.TryGet(netId, out PickupInfo info));
        Assert.Equal(2f, info.Radius);
        Assert.Equal(0f, info.TimeToLiveSeconds);
        Assert.Equal(at, info.Position);
        Assert.Equal(new List<long> { netId }, rig.Pickups.LiveNetIds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_accepted_collect_despawns_it_and_the_removal_reaches_the_client(bool sharded)
    {
        var collects = new List<PickupCollect>();
        var removals = new List<PickupRemoval>();
        var config = new WorldPickupsConfig
        {
            OnCollect = c => { collects.Add(c); return true; },
            OnRemoved = removals.Add,
        };
        Rig rig = NewRig(sharded, config);
        long netId = rig.Pickups.Spawn(rig.PlayerPosition, payloadId: 77L, radius: 2f);
        rig.Pump(6);

        Assert.Single(collects);
        Assert.Equal(netId, collects[0].PickupNetId);
        Assert.Equal(77L, collects[0].PayloadId);
        Assert.Equal(0, collects[0].Slot);
        Assert.Equal(rig.PlayerNetId, collects[0].PlayerNetId);
        Assert.True(collects[0].Distance <= 2f);

        Assert.Single(removals);
        Assert.Equal(PickupRemovalReason.Collected, removals[0].Reason);
        Assert.Equal(netId, removals[0].PickupNetId);
        Assert.Equal(0, removals[0].Slot);

        Assert.Equal(0, rig.Pickups.Count);
        Assert.False(rig.Pickups.IsLive(netId));
        Assert.False(rig.ClientSees(netId));                                   // the despawn propagated
        Assert.False(rig.Client.TryGetComponent(netId, out PickupState _));
        Assert.False(rig.Host.TryGetEntity(netId, out World _, out Entity _)); // and the entity is really gone
    }

    // ---- time to live ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimeToLive_expires_the_pickup_and_the_removal_reaches_the_client(bool sharded)
    {
        var removals = new List<PickupRemoval>();
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnRemoved = removals.Add });

        // Far from the player, so nothing but the clock can remove it.
        long netId = rig.Pickups.Spawn(rig.PlayerPosition + new Vector3(0f, 0f, 3f),
            payloadId: 5L, radius: 0.5f, timeToLiveSeconds: 10f * rig.TickSeconds);

        rig.Pump(6);
        Assert.True(rig.Pickups.IsLive(netId));      // still inside its lifetime
        Assert.True(rig.ClientSees(netId));
        Assert.Empty(removals);

        rig.Pump(8);
        Assert.False(rig.Pickups.IsLive(netId));
        Assert.Equal(0, rig.Pickups.Count);
        Assert.Single(removals);
        Assert.Equal(PickupRemovalReason.Expired, removals[0].Reason);
        Assert.Equal(-1, removals[0].Slot);          // nobody collected it
        Assert.Equal(0L, removals[0].PlayerNetId);
        Assert.False(rig.ClientSees(netId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_pickup_without_a_time_to_live_never_expires(bool sharded)
    {
        Rig rig = NewRig(sharded, new WorldPickupsConfig());
        long netId = rig.Pickups.Spawn(rig.PlayerPosition + new Vector3(0f, 0f, 3f), payloadId: 5L, radius: 0.5f);

        rig.Pump(120);

        Assert.True(rig.Pickups.IsLive(netId));
        Assert.True(rig.Pickups.TryGet(netId, out PickupInfo info));
        Assert.True(info.AgeSeconds > 3f);           // it aged, it just has nothing to age out against
    }

    // ---- the offer policy ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Standing_on_a_declined_pickup_offers_exactly_once_not_once_per_tick(bool sharded)
    {
        int offers = 0;
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnCollect = _ => { offers++; return false; } });

        long netId = rig.Pickups.Spawn(rig.PlayerPosition, payloadId: 1L, radius: 2f);
        rig.Pump(40);

        Assert.Equal(1, offers);                     // 40 ticks inside the radius, one callback
        Assert.True(rig.Pickups.IsLive(netId));      // a decline leaves it standing...
        Assert.True(rig.ClientSees(netId));          // ...and visible
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_null_collect_handler_declines_everything(bool sharded)
    {
        Rig rig = NewRig(sharded, new WorldPickupsConfig());   // no OnCollect: the engine grants nothing on its own
        long netId = rig.Pickups.Spawn(rig.PlayerPosition, payloadId: 1L, radius: 2f);

        rig.Pump(30);

        Assert.True(rig.Pickups.IsLive(netId));
        Assert.True(rig.ClientSees(netId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_player_outside_the_radius_is_never_offered(bool sharded)
    {
        int offers = 0;
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnCollect = _ => { offers++; return true; } });

        rig.Pickups.Spawn(rig.PlayerPosition + new Vector3(0f, 0f, 3f), payloadId: 1L, radius: 1f);
        rig.Pump(30);

        Assert.Equal(0, offers);
        Assert.Equal(1, rig.Pickups.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Leaving_and_re_entering_the_radius_offers_again(bool sharded)
    {
        int offers = 0;
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnCollect = _ => { offers++; return false; } });

        Vector3 home = rig.PlayerPosition;
        long netId = rig.Pickups.Spawn(home, payloadId: 1L, radius: 1.5f);
        rig.Pump(10);
        Assert.Equal(1, offers);

        rig.MovePlayer(0, home + new Vector3(4f, 0f, 0f));   // out of the radius, same cell
        rig.Pump(10);
        Assert.Equal(1, offers);                             // outside: no further offers

        rig.MovePlayer(0, home);                             // back in: a fresh entry
        rig.Pump(10);
        Assert.Equal(2, offers);
        Assert.True(rig.Pickups.IsLive(netId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reoffer_offers_again_without_the_player_moving(bool sharded)
    {
        int offers = 0;
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnCollect = _ => { offers++; return false; } });

        long netId = rig.Pickups.Spawn(rig.PlayerPosition, payloadId: 1L, radius: 2f);
        rig.Pump(20);
        Assert.Equal(1, offers);

        Assert.True(rig.Pickups.Reoffer(netId));     // the game's decline went stale (a bag slot freed)
        rig.Pump(20);
        Assert.Equal(2, offers);                     // re-offered once, standing still, and then quiet again

        Assert.False(rig.Pickups.Reoffer(netId + 12345));   // unknown pickup
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RetryDeclinedSeconds_offers_again_on_a_timer(bool sharded)
    {
        int offers = 0;
        var config = new WorldPickupsConfig
        {
            OnCollect = _ => { offers++; return false; },
            RetryDeclinedSeconds = 10f / 30f,        // ten ticks
        };
        Rig rig = NewRig(sharded, config);

        rig.Pickups.Spawn(rig.PlayerPosition, payloadId: 1L, radius: 2f);
        rig.Pump(5);
        Assert.Equal(1, offers);                     // the entry offer, and not yet due for a retry

        rig.Pump(30);
        // Bounded by the timer, nowhere near the 35 ticks that have run.
        Assert.InRange(offers, 3, 5);
    }

    // ---- the owner tag ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_non_owner_is_never_offered_and_SetOwner_hands_the_pickup_over(bool sharded)
    {
        var collects = new List<PickupCollect>();
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnCollect = c => { collects.Add(c); return true; } });

        long player = rig.PlayerNetId;
        long netId = rig.Pickups.Spawn(rig.PlayerPosition, payloadId: 9L, ownerNetId: player + 1000L, radius: 2f);
        rig.Pump(20);

        Assert.Empty(collects);                      // owned by somebody else: the callback is never even asked
        Assert.True(rig.Pickups.IsLive(netId));
        Assert.True(rig.Client.TryGetComponent(netId, out PickupState owned));
        Assert.Equal(player + 1000L, owned.OwnerNetId);

        Assert.True(rig.Pickups.SetOwner(netId, player));   // killer-only lapsed, hand it to this player
        rig.Pump(6);

        Assert.Single(collects);
        Assert.Equal(player, collects[0].OwnerNetId);
        Assert.False(rig.Pickups.IsLive(netId));
        Assert.False(rig.Pickups.SetOwner(netId, player));  // already gone
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetOwner_to_unowned_replicates_the_new_tag_and_re_offers(bool sharded)
    {
        int offers = 0;
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnCollect = _ => { offers++; return false; } });

        long player = rig.PlayerNetId;
        long netId = rig.Pickups.Spawn(rig.PlayerPosition, payloadId: 9L, ownerNetId: player, radius: 2f);
        rig.Pump(15);
        Assert.Equal(1, offers);

        Assert.True(rig.Pickups.SetOwner(netId, 0L));       // free-for-all now
        rig.Pump(10);

        Assert.Equal(2, offers);                            // the tag changed, so the standing decline was re-offered
        Assert.True(rig.Client.TryGetComponent(netId, out PickupState state));
        Assert.Equal(0L, state.OwnerNetId);
        Assert.False(state.IsOwned);
        Assert.Equal(9L, state.PayloadId);                  // the payload survives the re-tag
    }

    // ---- explicit removal ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_despawn_removes_it_and_propagates_to_the_client(bool sharded)
    {
        var removals = new List<PickupRemoval>();
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnRemoved = removals.Add });

        long netId = rig.Pickups.Spawn(rig.PlayerPosition + new Vector3(0f, 0f, 3f), payloadId: 4L, radius: 0.5f);
        rig.Pump(6);
        Assert.True(rig.ClientSees(netId));

        Assert.True(rig.Pickups.Despawn(netId));
        Assert.False(rig.Pickups.Despawn(netId));           // idempotent: a second call is a no-op, not a double removal

        rig.Pump(6);
        Assert.Equal(0, rig.Pickups.Count);
        Assert.False(rig.ClientSees(netId));
        Assert.Single(removals);
        Assert.Equal(PickupRemovalReason.Despawned, removals[0].Reason);
        Assert.Equal(4L, removals[0].PayloadId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DespawnAll_clears_every_tracked_pickup(bool sharded)
    {
        var removals = new List<PickupRemoval>();
        Rig rig = NewRig(sharded, new WorldPickupsConfig { OnRemoved = removals.Add });

        Vector3 home = rig.PlayerPosition;
        for (int i = 0; i < 4; i++) rig.Pickups.Spawn(home + new Vector3(0f, 0f, 3f + i), payloadId: i, radius: 0.5f);
        rig.Pump(4);
        Assert.Equal(4, rig.Pickups.Count);

        Assert.Equal(4, rig.Pickups.DespawnAll());
        Assert.Equal(0, rig.Pickups.Count);
        Assert.Equal(0, rig.Pickups.DespawnAll());          // nothing left to clear
        Assert.Equal(4, removals.Count);
        Assert.All(removals, r => Assert.Equal(PickupRemovalReason.Despawned, r.Reason));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_collect_handler_may_despawn_the_pickup_it_was_offered(bool sharded)
    {
        // The documented reentrancy contract: a handler that removes the pickup itself (and accepts) must not produce
        // a second removal, and the seam must stay consistent afterwards.
        var removals = new List<PickupRemoval>();
        WorldPickups? seam = null;
        var config = new WorldPickupsConfig
        {
            OnCollect = c => { seam!.Despawn(c.PickupNetId); return true; },
            OnRemoved = removals.Add,
        };
        Rig rig = NewRig(sharded, config);
        seam = rig.Pickups;

        long netId = rig.Pickups.Spawn(rig.PlayerPosition, payloadId: 3L, radius: 2f);
        rig.Pump(10);

        Assert.Single(removals);
        Assert.Equal(PickupRemovalReason.Despawned, removals[0].Reason);   // the handler's removal, not a second one
        Assert.Equal(0, rig.Pickups.Count);
        Assert.False(rig.ClientSees(netId));
    }

    // ---- the host surface ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DespawnEntity_refuses_a_player_and_an_unknown_net_id(bool sharded)
    {
        Rig rig = NewRig(sharded, new WorldPickupsConfig());

        Assert.False(rig.Host.DespawnEntity(rig.PlayerNetId));
        Assert.False(rig.Host.TryGetEntity(rig.PlayerNetId, out World _, out Entity _));
        Assert.False(rig.Host.DespawnEntity(987654321L));

        rig.Pump(4);
        Assert.True(rig.Client.Joined);                                   // the player is untouched
        Assert.True(rig.Host.TryGetPlayerState(0, out PlayerMoveState _));
    }

    [Fact]
    public void ShardedWorldServer_DespawnEntity_clears_an_entity_the_seam_never_spawned()
    {
        // The boot-sweep primitive for the CellPersistence hazard: a pickup resurrected out of a cell save is a plain
        // owned entity carrying PickupState that WorldPickups knows nothing about. It still has to be removable, and
        // on the sharded server it is, because DespawnEntity resolves through the shard host's ownership index.
        (INetTransport serverTransport, INetTransport clientTransport) = LoopbackTransport.CreatePair();
        ShardedWorldServerConfig cfg = SmallCells();
        var server = new ShardedWorldServer(serverTransport, cfg, Flat, MoveTuning.Default);
        var client = new WorldClient(clientTransport, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds, InterpolateRemotes = false });

        // Stands in for a restored entity: spawned straight through the server, never through the seam.
        long stray = server.SpawnEntity(5f, 5f, (w, e) => w.Set(e, new PickupState { PayloadId = 1234L }));
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }
        Assert.True(client.TryGetComponent(stray, out PickupState found));
        Assert.Equal(1234L, found.PayloadId);

        var sweep = new List<long>();
        foreach (CellSim cell in server.Host.Cells)
            foreach (Entity e in cell.World.Query().With<PickupState>().Entities())
                if (cell.World.TryGet(e, out NetId id)) sweep.Add(id.Value);
        Assert.Equal(new List<long> { stray }, sweep);

        foreach (long netId in sweep) Assert.True(server.DespawnEntity(netId));
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }

        Assert.False(client.TryGetComponent(stray, out PickupState _));
        Assert.False(server.DespawnEntity(stray));
    }

    // ---- argument guards ----

    [Fact]
    public void Spawn_rejects_a_non_finite_position_a_negative_radius_and_a_negative_lifetime()
    {
        var rig = NewRig(true, new WorldPickupsConfig());

        Assert.Throws<ArgumentException>(() => rig.Pickups.Spawn(new Vector3(float.NaN, 0f, 0f), 1L));
        Assert.Throws<ArgumentException>(() => rig.Pickups.Spawn(new Vector3(0f, float.PositiveInfinity, 0f), 1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Pickups.Spawn(Vector3.Zero, 1L, radius: -1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Pickups.Spawn(Vector3.Zero, 1L, radius: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => rig.Pickups.Spawn(Vector3.Zero, 1L, timeToLiveSeconds: -1f));
        Assert.Equal(0, rig.Pickups.Count);
    }

    [Fact]
    public void The_seam_requires_a_host()
    {
        Assert.Throws<ArgumentNullException>(() => new WorldPickups(null!));
    }

    // ---- the wire generation the new built-in forced ----

    [Fact]
    public void Pickup_wire_generation_is_gated_cleanly_at_connect()
    {
        // PickupState is a BUILT-IN (id 5 < FirstExtensionTypeId), so it is unframed: a client whose registry has no
        // id 5 cannot skip those bytes and would hard-fail its snapshot decode the first time a pickup entered its
        // area of interest, mid-session. The generation bump turns that into a clean IncompatibleVersion at connect.
        Assert.True(MoveProtocol.WireProtocolVersion >= 8);
        Assert.True(MoveProtocol.PickupTypeId < ReplicationRegistry.FirstExtensionTypeId);
        Assert.False(ReplicationRegistry.IsExtension(MoveProtocol.PickupTypeId));

        // A server one generation BEHIND stands in for a pre-pickup build (a live build's own WireProtocolVersion is
        // a const, so both live ends always match, and this is how the existing skew tests model a different build).
        (INetTransport serverTransport, INetTransport clientTransport) = LoopbackTransport.CreatePair();
        int olderGeneration = MoveProtocol.WireProtocolVersion - 1;
        var cfg = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(serverTransport, cfg, Flat, MoveTuning.Default,
            authenticator: new WireGenerationAuthenticator(olderGeneration, new AllowAllAuthenticator()));
        var client = new WorldClient(clientTransport, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds });

        for (int i = 0; i < 10; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }

        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.False(client.Joined);
        Assert.Equal(0, server.PlayerCount);   // never admitted, so never a snapshot to misparse
    }

    [Fact]
    public void The_pickup_component_round_trips_through_the_default_registry()
    {
        // The codec is symmetric and the component is registered by DEFAULT (no consumer opt-in), which is what makes
        // it a built-in rather than an extension.
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var authoritative = new World();
        Entity source = authoritative.Spawn();
        authoritative.Set(source, new NetId(1));
        authoritative.Set(source, new PickupState { PayloadId = long.MinValue + 7L, OwnerNetId = 4242L });

        byte[] snapshot = SnapshotWriter.Write(authoritative, registry);
        var mirror = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(mirror, snapshot);

        Assert.True(view.TryGetEntity(1L, out Entity replica));
        Assert.True(mirror.TryGet(replica, out PickupState decoded));
        Assert.Equal(long.MinValue + 7L, decoded.PayloadId);
        Assert.Equal(4242L, decoded.OwnerNetId);
    }
}
