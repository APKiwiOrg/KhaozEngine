using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The tile binding over the shared persistence core, driven through a minimal host rather than a real server: a
/// tile survives a restart, a record that will not decode is quarantined instead of applied and its player reset to
/// the head's configured spawn (or left standing where the head built them, when the head configures none), and a
/// tokenless connection is never written under its seat. The core's own behaviour is pinned by the NetWorld suite,
/// so what these cover is that the TILE side of the seam is wired up right.
/// </summary>
public class TileWorldPersistenceTests
{
    // The smallest thing the core can drive: raise join and leave synchronously, record every placement. No
    // transport, no simulator, no server. SetPositionHintProvider is left to its default interface implementation,
    // which is the shape of a head that spawns joins its own way. Spawn is the head's configured spawn, and a null
    // one answers false exactly as the default TryGetConfiguredSpawn does, so one fake drives both quarantine
    // branches.
    sealed class FakeHost : IPersistenceHost<TileMoveState>
    {
        public readonly Dictionary<int, string> Accounts = new();
        public readonly Dictionary<int, TileMoveState> States = new();
        public readonly List<(int slot, TileMoveState state, bool teleport)> Placed = new();
        public event Action<int, string>? PlayerJoined;
        public event Action<int, string, TileMoveState>? PlayerLeaving;
        public IReadOnlyCollection<int> JoinedSlots => Accounts.Keys;
        public bool TryGetAccountId(int slot, out string accountId) => Accounts.TryGetValue(slot, out accountId!);
        public bool TryGetPlayerState(int slot, out TileMoveState state) => States.TryGetValue(slot, out state);
        public void SetPlayerState(int slot, in TileMoveState state, bool teleport = false)
        {
            States[slot] = state;
            Placed.Add((slot, state, teleport));
        }
        public TileMoveState? Spawn;
        public bool TryGetConfiguredSpawn(int slot, out TileMoveState spawn)
        {
            if (Spawn is { } configured && Accounts.ContainsKey(slot)) { spawn = configured; return true; }
            spawn = default;
            return false;
        }
        public void Join(int slot, string account, TileMoveState at)
        {
            Accounts[slot] = account;
            States[slot] = at;
            PlayerJoined?.Invoke(slot, account);
        }
        public void Leave(int slot)
        {
            PlayerLeaving?.Invoke(slot, Accounts[slot], States[slot]);
            Accounts.Remove(slot);
            States.Remove(slot);
        }
    }

    static string TempDb() => Path.Combine(Path.GetTempPath(), "ke-tilenet-" + Guid.NewGuid().ToString("N") + ".db");

    // One region, (0, 0), and four planes: the world every test here validates a loaded record against.
    static readonly TileWorldDocument World = TileMoveSimulatorTests.FlatWorld();
    static readonly TileCollisionMap Map = TileMoveSimulatorTests.Bake(World);

    [Fact]
    public async Task A_players_tile_round_trips_through_a_temp_sqlite_store()
    {
        string db = TempDb();
        try
        {
            using var store = new SqliteWorldStore($"Data Source={db}");
            var host = new FakeHost();
            var p = new TileWorldPersistence(host, store, Map);
            host.Join(1, "acct-1", TileMoveState.At(new TileCoord(3, 4, 0), TileDirection.N));
            // Let the load-on-join land before the leave. Until it does the account is guarded and the leave-save is
            // skipped on purpose, so a test that walks straight from join to leave saves nothing at all.
            await p.FlushAsync();
            host.States[1] = TileMoveState.At(new TileCoord(30, 40, 1), TileDirection.SE);
            host.Leave(1);
            await p.FlushAsync();

            var host2 = new FakeHost();
            var p2 = new TileWorldPersistence(host2, store, Map);
            host2.Join(1, "acct-1", TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N));
            // FlushAsync drains the apply queue as well as awaiting the read, so the restore has landed when it
            // returns. A poll loop here would be a wall-clock budget against a real sqlite file on a shared runner.
            await p2.FlushAsync();
            Assert.Single(host2.Placed);
            Assert.Equal(new TileCoord(30, 40, 1), host2.Placed[0].state.Tile);
            Assert.Equal(TileDirection.SE, host2.Placed[0].state.Facing);
        }
        // Strict on purpose. The store is disposed by the time this runs, so on Windows this throws if the
        // sqlite handle is still open, which is the regression #713 was.
        finally { File.Delete(db); }
    }

    [Fact]
    public async Task A_corrupt_record_is_quarantined_and_the_player_is_placed_at_the_spawn()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:acct-2", Encoding.UTF8.GetBytes("not json at all"));
        var host = new FakeHost { Spawn = TileMoveState.At(new TileCoord(7, 8, 0), TileDirection.S) };
        string? quarantinedKey = null;
        var p = new TileWorldPersistence(host, store, Map);
        p.OnRecordQuarantined += (key, _) => quarantinedKey = key;
        host.Join(2, "acct-2", TileMoveState.At(new TileCoord(1, 1, 0), TileDirection.N));
        for (int i = 0; i < 50 && quarantinedKey is null; i++) { p.Update(0.25f); await Task.Delay(10); }
        Assert.Equal("acct-2", quarantinedKey);
        // Reset to the configured spawn, and as a genuine teleport. Declining to place would leave a rejoiner
        // standing on the resume hint the rejected record seeded, which nothing here ever validated (#642).
        (int slot, TileMoveState state, bool teleport) = Assert.Single(host.Placed);
        Assert.Equal(2, slot);
        Assert.Equal(new TileCoord(7, 8, 0), state.Tile);
        Assert.Equal(TileDirection.S, state.Facing);
        Assert.True(teleport);
        await p.FlushAsync();                          // the quarantine copy is a tracked write, awaited here
        Assert.NotNull(await store.LoadAsync("quarantine:player:acct-2"));
    }

    [Fact]
    public async Task A_quarantine_places_nobody_when_the_host_has_no_configured_spawn()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:acct-4", Encoding.UTF8.GetBytes("not json at all"));
        var host = new FakeHost();                     // no Spawn, so the host answers false like the default no-op
        string? quarantinedKey = null;
        var p = new TileWorldPersistence(host, store, Map);
        p.OnRecordQuarantined += (key, _) => quarantinedKey = key;
        host.Join(4, "acct-4", TileMoveState.At(new TileCoord(1, 1, 0), TileDirection.N));
        for (int i = 0; i < 50 && quarantinedKey is null; i++) { p.Update(0.25f); await Task.Delay(10); }
        Assert.Equal("acct-4", quarantinedKey);
        // A head that seeds no join has nothing to undo, so the player keeps whatever spawn it was built at.
        Assert.Empty(host.Placed);
    }

    [Fact]
    public async Task A_guest_connection_is_never_written()
    {
        var store = new InMemoryWorldStore();
        var host = new FakeHost();
        var p = new TileWorldPersistence(host, store, Map);
        host.Join(3, "guest:3", TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.N));
        host.Leave(3);
        await p.FlushAsync();
        Assert.Null(await store.LoadAsync("player:guest:3"));
        Assert.False(await store.ExistsAsync("player:3"));
        // Not just those two keys: nothing was written under ANY key, so a future PersistGuests default flipping to
        // true (which files a minted player:guest:{guid}) cannot leave this test green.
        var keys = new List<string>();
        await foreach (WorldStoreEntry entry in store.EnumerateAsync()) keys.Add(entry.Key);
        Assert.Empty(keys);
    }

    // The tile binding puts the PLANE on the Vector3's Y, so a restore that moves a player one whole floor measures
    // a distance of exactly 1. Against the core's own default of 1f that passed as no move at all, the record was
    // applied without a teleport, and the client glided between floors instead of cutting. A lattice binding wants a
    // sub-1 threshold, because the only distance that means "did not move" on a lattice is zero.
    [Theory]
    [InlineData(5, 5, 1, true)]        // one plane up: a move, and a loud one
    [InlineData(6, 5, 0, true)]        // one tile east: the same distance, and the same answer
    [InlineData(5, 5, 0, false)]       // the tile the join already seeded: still quiet, which is what #642 needs
    public async Task A_restore_that_moves_a_player_one_plane_reports_a_teleport(int x, int z, int plane, bool loud)
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:acct-plane",
            TilePlayerRecord.From(TileMoveState.At(new TileCoord(x, z, plane), TileDirection.S)).Encode());

        var host = new FakeHost();
        var p = new TileWorldPersistence(host, store, Map);
        // Seeded on (5, 5, 0), which is what a rejoin hint does: the stored record is one plane, one tile, or
        // nothing away from where the player already stands.
        host.Join(6, "acct-plane", TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.N));
        await p.FlushAsync();

        (int slot, TileMoveState state, bool teleport) = Assert.Single(host.Placed);
        Assert.Equal(6, slot);
        Assert.Equal(new TileCoord(x, z, plane), state.Tile);
        Assert.Equal(loud, teleport);
    }

    // A record can outlive the world it was written against: an authored-world edit drops or moves a region, or
    // lowers the plane count, and a player who logged out there is now stored somewhere the running build has no
    // ground for. TileWorldServer.SetPlayerState refuses both, by design and with a throw, because it is a door for
    // admin tooling. The persistence core calls that door with no try, from inside Update(dt), so before this the
    // one thing the whole quarantine mechanism exists to prevent, a bad record taking the head down, was exactly
    // what a stale one did. Driven through a REAL server rather than the fake host above, because the fake cannot
    // refuse anything and is why no test saw this.
    [Theory]
    [InlineData(10, 10, 9, "plane")]        // a plane the world does not have
    [InlineData(200, 200, 0, "region")]     // a tile in a region this build never loaded
    public async Task A_record_the_running_world_has_no_ground_for_is_quarantined_and_the_head_keeps_ticking(
        int tileX, int tileZ, int plane, string because)
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("player:acct-stale",
            TilePlayerRecord.From(TileMoveState.At(new TileCoord(tileX, tileZ, plane), TileDirection.S)).Encode());

        var hub = new InMemoryTransportHub();
        using var server = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(10, 10, 0)), Map,
            new TileDocumentTargets(World, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        var p = new TileWorldPersistence(server, store, Map);
        string? key = null, reason = null;
        p.OnRecordQuarantined += (k, r) => { key = k; reason = r; };

        server.SpawnPlayer(slot: 0, accountId: "acct-stale", displayName: "Ari");
        await p.FlushAsync();

        Assert.Equal("acct-stale", key);
        Assert.Contains(because, reason);
        // Placed at the head's configured spawn, which is the core's contract for a rejected record, and the bad
        // record is copied aside intact for offline repair rather than overwritten.
        Assert.True(server.TryGetPlayerState(0, out TileMoveState placed));
        Assert.Equal(new TileCoord(10, 10, 0), placed.Tile);
        Assert.NotNull(await store.LoadAsync("quarantine:player:acct-stale"));

        // The whole point: the head is still running. Both of these threw out of Update before the binding
        // measured a record against the world.
        p.Update(0.25f);
        server.Tick(0.25f);
        Assert.Equal(1, server.TickCount);
    }
}
