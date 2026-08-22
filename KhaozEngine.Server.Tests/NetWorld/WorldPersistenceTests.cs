using System;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldPersistenceTests
{
    private static float FlatGround(float x, float z) => 0f;

    private sealed class Harness : IDisposable
    {
        public readonly WorldServer Server;
        public readonly WorldPersistence Persistence;
        private readonly LoopbackTransport serverTransport;
        private readonly LoopbackTransport clientTransport;
        private readonly NetClient client;
        private readonly WorldServerConfig config;

        public Harness(IWorldStore store, byte[] token, WorldPersistenceConfig? pcfg = null)
        {
            (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
            config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
            Server = new WorldServer(serverTransport, config, FlatGround, MoveTuning.Default);
            Persistence = new WorldPersistence(Server, store, pcfg);
            client = new NetClient(clientTransport, TestHandshake.Wire(token));
        }

        // Pump until the predicate is true (or the frame budget runs out), running persistence.Update each frame.
        public void PumpUntil(Func<bool> done, int frames = 300)
        {
            for (int i = 0; i < frames && !done(); i++)
            {
                client.Poll();
                Server.Poll();
                Server.Tick(config.TickSeconds);
                Persistence.Update(config.TickSeconds);
            }
        }

        // Notifies the server end of a disconnect (so it observes a Left and save-on-leave fires).
        public void Disconnect() => clientTransport.Disconnect(new NetConnectionId(1));

        public void Dispose()
        {
            serverTransport.Dispose();
            clientTransport.Dispose();
        }
    }

    [Fact]
    public async Task LoadOnJoin_RestoresSavedPosition()
    {
        IWorldStore store = new InMemoryWorldStore();
        var saved = new PlayerMoveState { Position = new Vector3(33f, 0f, 44f) };
        await store.SaveAsync("player:hero", PlayerRecord.From(saved).Encode());

        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"));
        int slot = -1;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.PumpUntil(() => slot >= 0);
        await h.Persistence.FlushAsync();           // settle the async load
        h.Persistence.Update(0f);                   // apply the loaded state on the server thread

        Assert.True(h.Server.TryGetPlayerState(slot, out PlayerMoveState got));
        Assert.Equal(new Vector3(33f, 0f, 44f), got.Position);
    }

    [Fact]
    public async Task SaveOnLeave_PersistsFinalPosition()
    {
        IWorldStore store = new InMemoryWorldStore();
        using (var h = new Harness(store, Encoding.UTF8.GetBytes("hero")))
        {
            int slot = -1;
            h.Server.PlayerJoined += (s, _) => slot = s;
            h.PumpUntil(() => slot >= 0);
            h.Server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(5f, 0f, 9f) });
            h.Disconnect();
            h.PumpUntil(() => h.Server.PlayerCount == 0);   // server observes the leave -> save fires
            await h.Persistence.FlushAsync();
        }
        byte[]? data = await store.LoadAsync("player:hero");
        Assert.NotNull(data);
        Assert.Equal(new Vector3(5f, 0f, 9f), PlayerRecord.Decode(data!).ToState().Position);
    }

    [Fact]
    public async Task PeriodicSnapshot_SavesDirtyPlayers()
    {
        IWorldStore store = new InMemoryWorldStore();
        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"),
            new WorldPersistenceConfig { SaveIntervalSeconds = 0.0001f });   // fire almost immediately
        int slot = -1;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.PumpUntil(() => slot >= 0);
        h.Server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(1f, 0f, 2f) });
        h.Persistence.Update(1f);                   // dt past the interval -> dirty pass
        await h.Persistence.FlushAsync();

        byte[]? data = await store.LoadAsync("player:hero");
        Assert.NotNull(data);
        Assert.Equal(new Vector3(1f, 0f, 2f), PlayerRecord.Decode(data!).ToState().Position);
    }

    [Fact]
    public async Task PeriodicSnapshot_DuringInFlightLoad_DoesNotClobberStoredRecord()
    {
        // Ruinborne runs the async SqlServerWorldStore: reproduce the load-on-join window through the real WorldServer
        // with a gated store. A periodic dirty pass firing while the load is still in flight must not overwrite the
        // stored record (position + game blob) with the default-spawn state the player currently holds.
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("player:hero",
            PlayerRecord.From(new PlayerMoveState { Position = new Vector3(33f, 0f, 44f) }, Encoding.UTF8.GetBytes("loot")).Encode());
        var store = new GatedWorldStore(inner);

        using var h = new Harness(store, Encoding.UTF8.GetBytes("hero"),
            new WorldPersistenceConfig { SaveIntervalSeconds = 0.0001f });   // periodic pass fires almost every frame
        int slot = -1;
        h.Server.PlayerJoined += (s, _) => slot = s;
        h.PumpUntil(() => slot >= 0);                          // join fires; load-on-join is parked at the gate

        Assert.True(store.PendingLoads >= 1);                  // the load really is still in flight
        for (int i = 0; i < 5; i++) h.Persistence.Update(1f);  // force several periodic passes while it is in flight
        Assert.DoesNotContain("player:hero", store.SavedKeys); // the in-flight guard blocked every one

        store.ReleaseLoads();
        await h.Persistence.FlushAsync();
        h.Persistence.Update(0f);                              // apply the loaded state on the server thread

        Assert.True(h.Server.TryGetPlayerState(slot, out PlayerMoveState got));
        Assert.Equal(new Vector3(33f, 0f, 44f), got.Position); // restored, not clobbered
    }

    [Fact]
    public async Task SurvivesServerRestart_OnSqliteFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "ke-persist-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // First server: join, move, leave -> save.
            using (var store = new SqliteWorldStore($"Data Source={path}"))
            using (var h = new Harness(store, Encoding.UTF8.GetBytes("hero")))
            {
                int slot = -1;
                h.Server.PlayerJoined += (s, _) => slot = s;
                h.PumpUntil(() => slot >= 0);
                h.Server.SetPlayerState(slot, new PlayerMoveState { Position = new Vector3(100f, 0f, 200f) });
                h.Disconnect();
                h.PumpUntil(() => h.Server.PlayerCount == 0);
                await h.Persistence.FlushAsync();
            }

            // Brand-new server + store on the SAME file (a "restart"): the player is restored on join.
            using (var store = new SqliteWorldStore($"Data Source={path}"))
            using (var h = new Harness(store, Encoding.UTF8.GetBytes("hero")))
            {
                int slot = -1;
                h.Server.PlayerJoined += (s, _) => slot = s;
                h.PumpUntil(() => slot >= 0);
                await h.Persistence.FlushAsync();
                h.Persistence.Update(0f);

                Assert.True(h.Server.TryGetPlayerState(slot, out PlayerMoveState got));
                Assert.Equal(new Vector3(100f, 0f, 200f), got.Position);
            }
        }
        finally
        {
            // Strict, not best-effort: the stores above are disposed, so nothing holds the file open (#713).
            foreach (string p in new[] { path, path + "-wal", path + "-shm" }) File.Delete(p);
        }
    }
}
