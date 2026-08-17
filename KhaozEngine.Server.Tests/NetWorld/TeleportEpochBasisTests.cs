using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The BASIS behind the teleport-epoch stamp: the epoch a live player already carries, which both heads read and
/// increment in <c>SetPlayerState</c>. <see cref="TeleportEpochTests"/> covers what the stamp DOES (it advances on a
/// teleport, never on movement, and reaches the client). This class covers the one way it could go wrong instead:
/// a stamp built on a missing basis, which lands as 0 or 1 in place of an established value and moves the
/// authoritative epoch BACKWARDS.
///
/// <para>That is worth its own class because of what #409 changed downstream. The client holds the epoch as a
/// high-water mark and cuts only on an advance past it, so a backwards stamp no longer flip-flops, it goes SILENT:
/// every teleport until the counter climbs back past the watermark lands with no cut and no transition. A defect
/// here has no symptom of its own, so the invariants that forbid it are pinned by test rather than by reading.</para>
///
/// <para>#637 asked whether either head's basis read can miss on a live joined player and found it cannot. The rows
/// below drive the windows that investigation named: the join seam before the first tick, a teleport across cell
/// handoffs, an entity frozen mid-handoff, a cell restore into the very cell a player stands in, an eviction pass
/// around a joined player, and the two #642 paths that place a freshly reseeded entity.</para>
/// </summary>
public class TeleportEpochBasisTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    const float Dt = 1f / 30f;

    static ShardedWorldServerConfig ShardCfg() => new()
    {
        TickSeconds = Dt,
        CellSize = 60f,
        OverlapMargin = 24f,
        InterestRadius = 24f,
        MaxPlayers = 8,
        SpawnPosition = _ => Vector3.Zero,
    };

    static (ShardedWorldServer server, NetClient client) ConnectSharded(ShardedWorldServerConfig cfg, string account)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire(Encoding.UTF8.GetBytes(account)));
        for (int i = 0; i < 60; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.Equal(1, server.PlayerCount);
        return (server, client);
    }

    static uint Epoch(ShardedWorldServer server, int slot)
    {
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState st));
        return st.TeleportEpoch;
    }

    static void AssertStrictlyRising(IReadOnlyList<uint> epochs, string what)
    {
        for (int i = 1; i < epochs.Count; i++)
            Assert.True(epochs[i] > epochs[i - 1],
                $"{what} must stamp forward every time, and went {epochs[i - 1]} -> {epochs[i]} at step {i} " +
                $"(whole run: {string.Join(",", epochs)})");
    }

    // ---- The join seam: a placement between the join and the first tick, on both heads ----

    [Fact]
    public void A_teleport_stamped_between_the_join_and_the_first_tick_still_stamps_forward()
    {
        // The narrowest window either head has: OnJoin builds the entity and publishes the slot inside Poll, and this
        // calls SetPlayerState with no Tick in between, on EVERY frame of the session. The sharded head is the one
        // that matters, because its basis is a component on a cell entity rather than a value in a slot table, and it
        // publishes netIdBySlot only AFTER setting MovementState. That ordering is what this pins.
        var (st, ct) = LoopbackTransport.CreatePair();
        ShardedWorldServerConfig cfg = ShardCfg();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire(Encoding.UTF8.GetBytes("seam")));

        var stamped = new List<uint>();
        for (int i = 0; i < 60; i++)
        {
            client.Poll();
            server.Poll();                                   // the join lands in here
            foreach (int slot in server.JoinedSlots.ToList())
            {
                server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(3f, 0f, 3f) }, teleport: true);
                stamped.Add(Epoch(server, slot));
            }
            server.Tick(Dt);
        }

        Assert.NotEmpty(stamped);
        Assert.Equal(1u, stamped[0]);                        // the very first stamp of the session builds on 0
        AssertStrictlyRising(stamped, "a sharded teleport at the join seam");
    }

    [Fact]
    public void The_single_world_head_stamps_forward_at_the_join_seam_too()
    {
        // Same window on the single-World head, whose basis is stateBySlot. entityBySlot (the guard), stateBySlot
        // (the basis) and netIdBySlot are written together in OnJoin and removed together in OnLeave, so the guard
        // passing already proves the basis is there. This is that invariant as a test.
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = Dt, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire(Encoding.UTF8.GetBytes("seam")));

        var stamped = new List<uint>();
        for (int i = 0; i < 40; i++)
        {
            client.Poll();
            server.Poll();
            foreach (int slot in server.JoinedSlots.ToList())
            {
                server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(3f, 0f, 3f) }, teleport: true);
                Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState s));
                stamped.Add(s.TeleportEpoch);
            }
            server.Tick(Dt);
        }

        Assert.NotEmpty(stamped);
        Assert.Equal(1u, stamped[0]);
        AssertStrictlyRising(stamped, "a single-World teleport at the join seam");
    }

    // ---- Cell handoff: the epoch is a built-in component, so it must survive every crossing ----

    [Fact]
    public void Teleports_that_cross_cell_borders_keep_stamping_forward()
    {
        // Each teleport lands in a different cell, so the next Tick hands the entity off and the basis for the
        // teleport after it is read from a DIFFERENT cell's world. MovementState is a built-in component id, which
        // ReplicationRegistry.Register forces to ReplicationChannels.Default, so the Migrate capture always carries
        // the epoch across. The last two rows sit either side of one border, so the run ends on a crossing.
        ShardedWorldServerConfig cfg = ShardCfg();
        (ShardedWorldServer server, NetClient client) = ConnectSharded(cfg, "roam");
        int slot = server.JoinedSlots.First();
        var seen = new List<uint> { Epoch(server, slot) };

        foreach (Vector3 to in new[]
        {
            new Vector3(200f, 0f, 0f), new Vector3(-200f, 0f, 140f), new Vector3(0f, 0f, -320f),
            new Vector3(61f, 0f, 61f), new Vector3(59f, 0f, 59f),
        })
        {
            server.Teleport(PlayerRef.Slot(slot), to);
            for (int i = 0; i < 8; i++) { client.Poll(); server.Poll(); server.Tick(Dt); }
            seen.Add(Epoch(server, slot));
        }

        AssertStrictlyRising(seen, "a teleport across a cell handoff");
    }

    [Fact]
    public void A_placement_on_an_entity_frozen_mid_handoff_is_a_no_op_rather_than_a_stamp()
    {
        // The handoff freeze, forced open. ProcessHandoffs phase 1 sets Migrating and unregisters the entity from its
        // source cell. The shipped in-process link acks inside the same call, but the seam is documented for a
        // networked ICellLink that would hold this window across calls. TryGetOwned excludes both Ghost and Migrating,
        // so the frozen player resolves to NO owner: the write is skipped entirely instead of stamping a basis read
        // off a half-migrated entity. That is what makes this window harmless, so it is pinned rather than assumed.
        ShardedWorldServerConfig cfg = ShardCfg();
        (ShardedWorldServer server, NetClient client) = ConnectSharded(cfg, "frozen");
        int slot = server.JoinedSlots.First();
        server.Teleport(PlayerRef.Slot(slot), new Vector3(20f, 0f, 20f));
        for (int i = 0; i < 8; i++) { client.Poll(); server.Poll(); server.Tick(Dt); }

        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim home, out Entity player));
        uint before = Epoch(server, slot);
        Assert.True(before > 0u, "the teleport above should have moved the epoch off its spawn value");

        home.World.Set(player, new Migrating { Destination = new CellCoord(9, 9) });
        home.UnregisterOwned(netId);

        server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(80f, 0f, 80f) }, teleport: true);

        Assert.False(server.TryGetPlayerState(slot, out _),
            "a frozen entity is owned by no cell, so its state is unreadable and the placement is a no-op");
        Assert.True(home.World.TryGet(player, out MovementState ms));
        Assert.Equal(before, ms.TeleportEpoch);   // nothing was stamped over the frozen copy either
    }

    // ---- Cell persistence and eviction, the two paths #637 named as the places to look first ----

    [Fact]
    public void A_cell_restore_into_the_cell_a_player_stands_in_leaves_their_epoch_alone()
    {
        // A restore applies a blob into a LIVE cell world through a throwaway view that spawns one entity per NetId in
        // the blob. It can only touch a player if a player were in the blob, and CellSim.SnapshotOwned excludes the
        // NetIds the server passes it (the joined players). This drives that round trip against the player's own cell.
        ShardedWorldServerConfig cfg = ShardCfg();
        (ShardedWorldServer server, NetClient client) = ConnectSharded(cfg, "restore");
        int slot = server.JoinedSlots.First();
        server.SpawnEntity(4f, 4f);                          // an NPC in the player's cell, so the blob is not empty
        for (int i = 0; i < 6; i++) { client.Poll(); server.Poll(); server.Tick(Dt); }

        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim home, out _));
        ICellPersistenceHost persistence = server;
        byte[]? blob = persistence.SnapshotCell(home.Coord);
        Assert.NotNull(blob);

        uint before = Epoch(server, slot);
        IReadOnlyList<long> restored = persistence.RestoreCell(home.Coord, blob!);
        Assert.DoesNotContain(netId, restored);              // a cell blob never carries a joined player

        server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(7f, 0f, 7f) }, teleport: true);
        Assert.Equal(before + 1u, Epoch(server, slot));
    }

    [Fact]
    public void A_cell_holding_a_joined_player_is_unevictable_and_evicting_its_neighbours_changes_nothing()
    {
        // Unloading a player's cell would destroy the entity holding their epoch, so both halves of the eviction gate
        // refuse it: the server pins its bound players' home cells, and ShardHost.CanRemoveCell refuses the same cells
        // through the client bindings. Evicting everything else must leave the next teleport stamping normally.
        ShardedWorldServerConfig cfg = ShardCfg();
        (ShardedWorldServer server, NetClient client) = ConnectSharded(cfg, "evict");
        int slot = server.JoinedSlots.First();
        server.Teleport(PlayerRef.Slot(slot), new Vector3(200f, 0f, 0f));
        for (int i = 0; i < 12; i++) { client.Poll(); server.Poll(); server.Tick(Dt); }

        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim home, out _));
        Assert.False(server.CanEvictCell(home.Coord), "a cell owning a joined player must never be evictable");
        Assert.False(server.Host.CanRemoveCell(home.Coord), "and the host must refuse it independently");

        foreach (CellSim c in server.Host.Cells.ToList())
            if (c.Coord != home.Coord) server.EvictCell(c.Coord);

        uint before = Epoch(server, slot);
        server.Teleport(PlayerRef.Slot(slot), new Vector3(-400f, 0f, 260f));
        for (int i = 0; i < 12; i++) { client.Poll(); server.Poll(); server.Tick(Dt); }
        Assert.Equal(before + 1u, Epoch(server, slot));
    }

    // ---- The guard itself: a missing basis is announced, never silently stamped as 0 ----

    [Fact]
    public void A_forced_missing_basis_is_announced_rather_than_silently_stamped_from_zero()
    {
        // Every row above shows the miss is unreachable. This one FORCES it, by stripping MovementState off a live
        // player entity, and pins that reaching it is loud: TeleportEpochGuard reports before returning 0, so a future
        // path that produces this state announces itself instead of shipping a swallowed teleport to every client.
        //
        // It catches by MESSAGE rather than by type on purpose. The guard uses Debug.Assert, the house pattern for an
        // invariant (BepuPhysicsWorld, Scene3D.RenderOrigin), and the VSTest host translates a failed Debug.Fail into
        // its own DebugAssertException so the run does not die. That type belongs to the harness, not to anything the
        // engine ships or should reference, so the marker text is what this asserts on.
        //
        // That rescue is the TEST PLATFORM's, not the runtime's, and this row depends on it: VSTest installs a
        // DebugProvider that turns the failed assert into a throw. Run this assembly under Microsoft.Testing.Platform,
        // or execute the dll directly, and the same call reaches Environment.FailFast and takes the whole run down
        // rather than failing one test. The error log the guard emits FIRST is the half that does not depend on the
        // harness, but asserting on it here would mean calling the process-global Log.Configure, and Server.Tests has
        // no LoggingSerial collection to serialize that against (the collection lives in Foundation.Tests, and xUnit
        // collections do not cross assemblies), so the log line is deliberately left unasserted rather than bought
        // with unguarded process-global state.
        ShardedWorldServerConfig cfg = ShardCfg();
        (ShardedWorldServer server, NetClient client) = ConnectSharded(cfg, "forced");
        int slot = server.JoinedSlots.First();
        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity player));

        cell.World.Remove<MovementState>(player);

        Exception raised = Assert.ThrowsAny<Exception>(() =>
            server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(9f, 0f, 9f) }, teleport: true));
        Assert.Contains("no teleport-epoch basis", raised.Message);
        Assert.Contains("#637", raised.Message);
    }

    // ---- The two #642 paths, which both place a freshly reseeded entity ----

    [Fact]
    public async Task The_async_restore_stamps_forward_on_the_entity_the_join_built()
    {
        // #642 gave the restore a companion: the join is seeded from the resume hint, and the restore that follows is
        // applied as a teleport only when it actually moves the player. Either way the entity it places is the one
        // OnJoin just built, which carries a fresh MovementState, so the stamp has a basis. A first join with no hint
        // is the loud case: the entity is built at the spawn and the record moves it half a kilometre.
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:restored",
            PlayerRecord.From(new PlayerMoveState { Position = new Vector3(420f, 0.9f, -260f) }).Encode());

        var (st, ct) = LoopbackTransport.CreatePair();
        ShardedWorldServerConfig cfg = ShardCfg();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
        var client = new NetClient(ct, TestHandshake.Wire(Encoding.UTF8.GetBytes("restored")));

        var trail = new List<uint>();
        for (int i = 0; i < 200; i++)
        {
            client.Poll(); server.Poll(); server.Tick(Dt); persistence.Update(Dt);
            if (server.PlayerCount == 1) trail.Add(Epoch(server, server.JoinedSlots.First()));
        }

        int slot = server.JoinedSlots.First();
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState final));
        Assert.True(Vector3.Distance(final.Position, new Vector3(420f, 0.9f, -260f)) < 1f,
            $"the stored record should have been restored ({final.Position})");
        Assert.Equal(1u, final.TeleportEpoch);               // the restore's own teleport, built on the join's 0
        for (int i = 1; i < trail.Count; i++)
            Assert.True(trail[i] >= trail[i - 1], $"the epoch dipped during the restore: {string.Join(",", trail)}");
    }

    [Fact]
    public async Task The_quarantine_reset_stamps_forward_on_the_entity_the_join_built()
    {
        // The other #642 path. A record that fails validation is copied to the quarantine key and the player is RESET
        // to the configured spawn as a teleport, because the join may have seeded them at a hint the rejected record
        // is the only evidence for. Same basis as the restore above: the entity OnJoin built, epoch 0, stamped to 1.
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:bad",
            PlayerRecord.From(new PlayerMoveState { Position = new Vector3(9000f, 0.9f, 9000f) }).Encode());

        var (st, ct) = LoopbackTransport.CreatePair();
        ShardedWorldServerConfig cfg = ShardCfg();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig
        {
            SaveIntervalSeconds = 999f,
            Bounds = new RectBounds(-1000f, -1000f, 1000f, 1000f),   // the stored position is well outside it
        });
        string? quarantined = null;
        persistence.OnRecordQuarantined += (account, _) => quarantined = account;
        var client = new NetClient(ct, TestHandshake.Wire(Encoding.UTF8.GetBytes("bad")));

        var trail = new List<uint>();
        for (int i = 0; i < 200; i++)
        {
            client.Poll(); server.Poll(); server.Tick(Dt); persistence.Update(Dt);
            if (server.PlayerCount == 1) trail.Add(Epoch(server, server.JoinedSlots.First()));
        }

        Assert.Equal("bad", quarantined);
        Assert.Equal(1u, Epoch(server, server.JoinedSlots.First()));   // the reset teleport, built on the join's 0
        for (int i = 1; i < trail.Count; i++)
            Assert.True(trail[i] >= trail[i - 1], $"the epoch dipped during the reset: {string.Join(",", trail)}");
    }
}
