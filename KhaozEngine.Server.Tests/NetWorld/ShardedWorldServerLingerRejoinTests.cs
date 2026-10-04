using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// A reconnect inside the disconnect linger on <see cref="ShardedWorldServer"/>: the same account reclaims its held
/// slot, the lingering body leaves through the ordinary path (one <c>PlayerLeaving</c>, one save, the despawn) and a
/// fresh body is seated on that slot from the saved record, where the old one stood. A tokenless guest cannot reclaim
/// anything, so its linger runs out.
/// </summary>
public class ShardedWorldServerLingerRejoinTests
{
    private const float Dt = 1f / 30f;

    // Records every player-record write into the rig's event log, so a save is ordered against the join it precedes.
    private sealed class LoggingStore : IWorldStore
    {
        private readonly InMemoryWorldStore inner = new();
        private readonly List<string> log;
        public LoggingStore(List<string> log) => this.log = log;
        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) => inner.LoadAsync(key, ct);
        public Task SaveAsync(string key, byte[] data, CancellationToken ct = default)
        {
            log.Add($"save:{key}");
            return inner.SaveAsync(key, data, ct);
        }
        public Task SaveManyAsync(IReadOnlyList<(string Key, byte[] Data)> items, CancellationToken ct = default)
        {
            foreach ((string key, byte[] _) in items) log.Add($"save:{key}");
            return inner.SaveManyAsync(items, ct);
        }
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
    }

    // The ShardedWorldPersistenceTests wiring on an in-memory hub: a WorldPersistence that never runs its periodic
    // pass on its own, and a linger hook answering LingerTicks for every drop. Leaves, joins and store writes share
    // one log.
    private sealed class Rig : IDisposable
    {
        public readonly InMemoryTransportHub Hub = new();
        public readonly ShardedWorldServer Server;
        public readonly WorldPersistence Persistence;
        public readonly LoggingStore Store;
        public readonly List<string> Log = new();
        public int LingerTicks = 300;
        public int Leaves;

        public Rig(int maxPlayers = 4, DuplicateSessionPolicy duplicates = DuplicateSessionPolicy.KickOlder)
        {
            var config = new ShardedWorldServerConfig
            {
                TickSeconds = Dt,
                MaxPlayers = maxPlayers,
                DuplicateSessions = duplicates,
                DisconnectLingerTicks = (_, _) => LingerTicks,
            };
            Server = new ShardedWorldServer(Hub.Server, config, (_, _) => 0f, MoveTuning.Default);
            Server.PlayerLeaving += (slot, account, _) => { Leaves++; Log.Add($"leaving:{slot}:{account}"); };
            Server.PlayerJoined += (slot, account) => Log.Add($"joined:{slot}:{account}");
            Store = new LoggingStore(Log);
            Persistence = new WorldPersistence(Server, Store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
        }

        public NetClient Connect(string? account, out INetTransport transport)
        {
            transport = Hub.CreateClient();
            var client = new NetClient(transport, account is null ? TestHandshake.Wire() : TestHandshake.Wire(account));
            client.Poll();
            return client;
        }

        public int Join(string? account, out INetTransport transport)
        {
            NetClient client = Connect(account, out transport);
            Server.Poll();
            client.Poll();
            Assert.True(client.Slot >= 0);
            Assert.True(Server.TryGetPlayerNetId(client.Slot, out _));
            return client.Slot;
        }

        // Drops the link and lets the server see it, so the body lingers from here.
        public void Drop(INetTransport transport, int slot)
        {
            Hub.DisconnectClient(transport);
            Server.Poll();
            Assert.True(Server.IsLingering(slot));
        }

        public void Step()
        {
            Server.Poll();
            Server.Tick(Dt);
            Persistence.Update(Dt);
        }

        public bool BodyAlive(long netId) => Server.Host.TryGetOwner(netId, out _, out _);

        public int SlotsFor(string account)
        {
            int count = 0;
            foreach (int slot in Server.JoinedSlots)
                if (Server.TryGetAccountId(slot, out string id) && id == account) count++;
            return count;
        }

        public int Count(string entry) => Log.FindAll(e => e == entry).Count;

        public void Dispose()
        {
            Server.Dispose();
            Hub.Dispose();
        }
    }

    // Moves the slot's body to (x, current y, current z) the way a game would during the linger.
    private static void MoveBodyTo(ShardedWorldServer server, int slot, float x)
    {
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState state));
        state.Position = new Vector3(x, state.Position.Y, state.Position.Z);
        server.SetPlayerState(slot, state);
    }

    [Fact]
    public void ARejoinReclaimsTheSlotAndEndsTheBodyFirst()
    {
        using var rig = new Rig();
        int slot = rig.Join("a", out INetTransport first);
        Assert.Equal(0, slot);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long oldNetId));
        rig.Drop(first, slot);
        rig.Step();
        rig.Step();
        rig.Log.Clear();

        NetClient again = rig.Connect("a", out _);
        rig.Server.Poll();
        again.Poll();

        Assert.Equal(0, again.Slot);
        Assert.Equal(new[] { "leaving:0:a", "save:player:a", "joined:0:a" }, rig.Log);
        Assert.False(rig.BodyAlive(oldNetId));
        Assert.False(rig.Server.IsLingering(slot));
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long newNetId));
        Assert.NotEqual(oldNetId, newNetId);
        Assert.True(rig.BodyAlive(newNetId));
        Assert.Equal(1, rig.SlotsFor("a"));
        Assert.Equal(1, rig.Server.PlayerCount);

        // The linger ended with the old body, so its expiry never removes the new one.
        for (int i = 0; i < 310; i++) rig.Step();
        Assert.Equal(1, rig.Leaves);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long still));
        Assert.Equal(newNetId, still);
    }

    [Fact]
    public void TheRejoinIsSeatedFromWhatTheLingerSaved()
    {
        using var rig = new Rig();
        INetTransport? live = null;
        var client = new WorldClient(
            () => { INetTransport t = rig.Hub.CreateClient(); live = t; return t; },
            (_, _) => 0f, MoveTuning.Default,
            new WorldClientConfig
            {
                TickSeconds = Dt,
                DisconnectTimeoutSeconds = 0.5f,
                Reconnect = new ReconnectBackoff { InitialSeconds = 0.05f, Multiplier = 1f, MaxSeconds = 0.05f },
            },
            Encoding.UTF8.GetBytes("a"));
        void Frame(float dt = Dt)
        {
            rig.Step();
            client.Poll(dt);
            client.AdvancePresentation(dt);
        }
        for (int i = 0; i < 40 && !client.Joined; i++) Frame();
        Assert.True(client.Joined);
        for (int i = 0; i < 20; i++) { client.SendInput(MoveCommand.Idle); Frame(); }
        int slot = Assert.Single(rig.Server.JoinedSlots);
        long oldNetId = client.LocalNetId;
        uint epochBefore = client.LocalTeleportEpoch;

        rig.Drop(live!, slot);
        MoveBodyTo(rig.Server, slot, 12f);

        bool back = false;
        for (int i = 0; i < 400 && !back; i++)
        {
            Frame(0.05f);
            back = client.Joined && client.LocalNetId > 0 && client.LocalNetId != oldNetId;
        }
        Assert.True(back, "the client never rejoined");
        for (int i = 0; i < 40; i++) { client.SendInput(MoveCommand.Idle); Frame(); }

        Assert.Equal(1, rig.Leaves);
        Assert.True(rig.Server.TryGetPlayerState(slot, out PlayerMoveState seated));
        Assert.Equal(12f, seated.Position.X, 1e-3f);
        Assert.Equal(epochBefore, client.LocalTeleportEpoch);
    }

    [Fact]
    public async Task LeaveAndRejoinInOnePollSavesOnceBeforeTheJoin()
    {
        using var rig = new Rig();
        int slot = rig.Join("a", out INetTransport first);
        for (int i = 0; i < 5; i++) rig.Step();
        rig.Log.Clear();

        // The drop and the returning Hello both reach the server in the same Poll.
        rig.Hub.DisconnectClient(first);
        NetClient again = rig.Connect("a", out _);
        rig.Server.Poll();
        again.Poll();

        Assert.Equal(slot, again.Slot);
        Assert.Equal(new[] { "leaving:0:a", "save:player:a", "joined:0:a" }, rig.Log);
        Assert.False(rig.Server.IsLingering(slot));

        for (int i = 0; i < 30; i++) rig.Step();
        await rig.Persistence.FlushAsync();
        Assert.Equal(1, rig.Count("leaving:0:a"));
        Assert.Equal(1, rig.Count("save:player:a"));
        Assert.Equal(1, rig.SlotsFor("a"));
    }

    [Fact]
    public void ARecycledSlotWhoseNewcomerDropsInOnePollLeavesTheOldBodyOnce()
    {
        using var rig = new Rig();
        int slot = rig.Join("a", out _);
        Assert.Equal(0, slot);
        for (int i = 0; i < 5; i++) rig.Step();
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long oldNetId));
        rig.Log.Clear();

        // KickOlder recycles slot 0 for the newcomer, which drops in the same Poll. The hold is asked from the host's
        // view, one drain behind, so it answers for the old body. Its kick still has to leave it exactly once.
        rig.Connect("a", out INetTransport t2);
        rig.Hub.DisconnectClient(t2);
        rig.Server.Poll();

        Assert.Equal(new[] { "leaving:0:a", "save:player:a", "joined:0:a", "leaving:0:a" }, rig.Log);
        Assert.False(rig.BodyAlive(oldNetId));
        Assert.False(rig.Server.IsLingering(slot));
        Assert.Equal(0, rig.Server.PlayerCount);
        Assert.Equal(0, rig.SlotsFor("a"));

        // Nothing holds the slot any more.
        Assert.Equal(0, rig.Join("b", out _));
    }

    [Fact]
    public void RefuseNewerDoesNotRefuseTheReturningAccount()
    {
        using var rig = new Rig(duplicates: DuplicateSessionPolicy.RefuseNewer);
        int slot = rig.Join("a", out INetTransport first);
        rig.Drop(first, slot);
        rig.Step();

        NetClient again = rig.Connect("a", out _);
        rig.Server.Poll();
        again.Poll();

        Assert.Equal(slot, again.Slot);
        Assert.Equal(1, rig.Leaves);
        Assert.Equal(1, rig.SlotsFor("a"));
        Assert.False(rig.Server.IsLingering(slot));
    }

    [Fact]
    public void AFullServerAdmitsTheReturningAccount()
    {
        using var rig = new Rig(maxPlayers: 1);
        int slot = rig.Join("a", out INetTransport first);
        rig.Drop(first, slot);
        rig.Step();

        NetClient stranger = rig.Connect("b", out _);
        rig.Server.Poll();
        stranger.Poll();
        Assert.True(stranger.Slot < 0, "the lingering body holds the only seat");

        NetClient again = rig.Connect("a", out _);
        rig.Server.Poll();
        again.Poll();

        Assert.Equal(slot, again.Slot);
        Assert.Equal(1, rig.Leaves);
        Assert.Equal(1, rig.SlotsFor("a"));
        Assert.Equal(1, rig.Server.PlayerCount);
    }

    [Fact]
    public void AGuestLingerRunsOut()
    {
        using var rig = new Rig { LingerTicks = 3 };
        int slot = rig.Join(null, out INetTransport first);
        Assert.Equal(0, slot);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long netId));
        rig.Hub.DisconnectClient(first);
        rig.Server.Poll();
        long k = rig.Server.ServerTick;
        Assert.True(rig.Server.IsLingering(slot));

        int other = rig.Join(null, out _);
        Assert.Equal(1, other);

        for (long t = k + 1; t <= k + 3; t++)
        {
            rig.Step();
            Assert.True(rig.BodyAlive(netId), $"tick {t}");
            Assert.Equal(0, rig.Leaves);
        }
        rig.Step();

        Assert.Equal(k + 4, rig.Server.ServerTick);
        Assert.False(rig.BodyAlive(netId));
        Assert.Equal(1, rig.Leaves);
        Assert.False(rig.Server.IsLingering(slot));
        Assert.True(rig.Server.TryGetPlayerNetId(other, out _));
    }

    [Fact]
    public async Task TheShutdownSaveIncludesALingeringBody()
    {
        using var rig = new Rig();
        int slot = rig.Join("a", out INetTransport first);
        rig.Step();
        rig.Drop(first, slot);
        MoveBodyTo(rig.Server, slot, 20f);

        rig.Persistence.SaveDirtyPass();
        await rig.Persistence.FlushAsync();

        byte[]? stored = await rig.Store.LoadAsync("player:a");
        Assert.NotNull(stored);
        Assert.Equal(20f, PlayerRecord.Decode(stored).ToState().Position.X, 1e-3f);
        Assert.True(rig.Server.IsLingering(slot));
    }
}
