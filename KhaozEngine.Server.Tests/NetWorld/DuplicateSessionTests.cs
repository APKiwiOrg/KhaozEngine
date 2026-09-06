using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// One account is ONE live session (#662). The join gate used to allocate a slot per accepted Hello and never ask
/// whether the authenticated subject already held one, so two clients presenting the same connect token became two
/// live sessions of one account. <see cref="WorldPersistence"/> keys one record per account, so those two sessions
/// shared a record, and once #654 made a load belong to a SESSION the later join superseded the earlier one: the
/// first session was never restored, sat on the default spawn, and its state overwrote the account record as soon as
/// the winner left.
///
/// <para>The rows drive the real loopback stack (an <see cref="InMemoryTransportHub"/>, a <see cref="WorldServer"/> or
/// <see cref="ShardedWorldServer"/>, and a <see cref="WorldPersistence"/> over a <see cref="GatedWorldStore"/> that
/// holds every load open across both joins), which is the reproduction from the issue. The last rows leave
/// persistence out and ask the CLIENT what it was told, because a kick a player cannot be shown a reason for is
/// half a fix.</para>
///
/// <para>The in-memory transport exercises the framed <c>Reject</c>, the client classification, and the terminal
/// event from the server's real kick path. The LiteNetLib binding separately covers transport-specific delivery of
/// a reason carried by the disconnect itself.</para>
/// </summary>
public class DuplicateSessionTests
{
    private static float Flat(float x, float z) => 0f;

    private const float Dt = 1f / 30f;
    private const string Alpha = "alpha";

    // Where alpha's stored record says it is, far enough from the configured spawn (the origin) that a record
    // overwritten with join-time state is unmistakable.
    private static readonly Vector3 Stored = new(333f, 0f, -333f);

    private static float PlanarDistance(Vector3 a, Vector3 b) =>
        Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    // One client, with whatever the session layer has told it so far.
    private sealed class Peer
    {
        public required NetClient Client { get; init; }
        public required INetTransport Transport { get; init; }
        public List<string> Rejects { get; } = new();
        public bool Joined { get; private set; }

        public void Poll()
        {
            Client.Poll();
            while (Client.TryDequeueEvent(out ClientSessionEvent ev))
            {
                if (ev.Kind == ClientSessionEventKind.Joined) Joined = true;
                else if (ev.Kind == ClientSessionEventKind.Rejected) Rejects.Add(ev.RejectReason);
            }
        }
    }

    private sealed class Rig
    {
        public required InMemoryTransportHub Hub { get; init; }
        public required GatedWorldStore Store { get; init; }
        public required InMemoryWorldStore Inner { get; init; }
        public required WorldPersistence Persistence { get; init; }
        public required IWorldPersistenceHost Host { get; init; }
        /// <summary>One server frame: poll, tick (which serves), then the persistence drain.</summary>
        public required Action Step { get; init; }

        public List<Peer> Peers { get; } = new();
        /// <summary>Every (accountId, slot) the layer reported dropping, in order.</summary>
        public List<(string Account, int Slot)> Dropped { get; } = new();
        /// <summary>Accounts whose durable blob was re-applied, one entry per apply.</summary>
        public List<string> Applied { get; } = new();

        /// <summary>Connects a client. A null account is a TOKENLESS connection: no subject, so nothing the gate can
        /// read as a duplicate.</summary>
        public Peer Connect(string? account)
        {
            INetTransport t = Hub.CreateClient();
            var peer = new Peer
            {
                Client = new NetClient(t, account is null ? TestHandshake.Wire() : TestHandshake.Wire(account)),
                Transport = t,
            };
            Peers.Add(peer);
            return peer;
        }

        public void Drop(Peer peer) => Hub.DisconnectClient(peer.Transport);

        public void Pump(Func<bool> until, int frames = 400)
        {
            for (int i = 0; i < frames && !until(); i++)
            {
                foreach (Peer p in Peers) p.Poll();
                Step();
            }
        }

        public PlayerMoveState State(int slot)
        {
            Assert.True(Host.TryGetPlayerState(slot, out PlayerMoveState s), $"slot {slot} has no live player");
            return s;
        }

        public string Account(int slot)
        {
            Assert.True(Host.TryGetAccountId(slot, out string a), $"slot {slot} has no account");
            return a;
        }

        /// <summary>The only slot with a live player, asserting there is exactly one.</summary>
        public int OnlySlot()
        {
            Assert.Single(Host.JoinedSlots);
            foreach (int slot in Host.JoinedSlots) return slot;
            throw new InvalidOperationException("unreachable");
        }

        /// <summary>Releases every parked load and settles it, applying (or dropping) on the server thread.</summary>
        public async Task SettleLoadsAsync()
        {
            Store.ReleaseLoads();
            await Persistence.FlushAsync();
            Persistence.Update(0f);
        }

        /// <summary>What alpha's record says now, read straight from the wrapped store.</summary>
        public async Task<Vector3> StoredPositionAsync()
        {
            byte[]? data = await Inner.LoadAsync("player:" + Alpha);
            Assert.NotNull(data);
            return PlayerRecord.Decode(data!).ToState().Position;
        }
    }

    private static async Task<Rig> SingleAsync(DuplicateSessionPolicy policy = DuplicateSessionPolicy.KickOlder)
    {
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("player:" + Alpha, PlayerRecord.From(new PlayerMoveState { Position = Stored },
            Encoding.UTF8.GetBytes("alpha-blob")).Encode());
        var store = new GatedWorldStore(inner);
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig
        {
            TickSeconds = Dt,
            InterestRadius = 500f,
            MaxPlayers = 8,
            SpawnPosition = _ => Vector3.Zero,
            DuplicateSessions = policy,
        };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        return Build(hub, store, inner, server, () => { server.Poll(); server.Tick(Dt); });
    }

    private static async Task<Rig> ShardedAsync(DuplicateSessionPolicy policy = DuplicateSessionPolicy.KickOlder)
    {
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("player:" + Alpha, PlayerRecord.From(new PlayerMoveState { Position = Stored },
            Encoding.UTF8.GetBytes("alpha-blob")).Encode());
        var store = new GatedWorldStore(inner);
        var hub = new InMemoryTransportHub();
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = Dt,
            CellSize = 60f,
            OverlapMargin = 24f,
            InterestRadius = 24f,
            MaxPlayers = 8,
            SpawnPosition = _ => Vector3.Zero,
            DuplicateSessions = policy,
        };
        var server = new ShardedWorldServer(hub.Server, config, Flat, MoveTuning.Default);
        return Build(hub, store, inner, server, () => { server.Poll(); server.Tick(Dt); });
    }

    private static Rig Build(InMemoryTransportHub hub, GatedWorldStore store, InMemoryWorldStore inner,
        IWorldPersistenceHost host, Action tick)
    {
        Rig rig = null!;
        var persistence = new WorldPersistence(host, store, new WorldPersistenceConfig
        {
            SaveIntervalSeconds = 999f,   // no periodic pass runs on its own: the rows that want one force it
            ApplyGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> blob) => rig.Applied.Add(ctx.AccountId),
        });
        rig = new Rig
        {
            Hub = hub,
            Store = store,
            Inner = inner,
            Persistence = persistence,
            Host = host,
            Step = () => { tick(); persistence.Update(Dt); },
        };
        persistence.OnLoadApplyDropped += (acct, slot) => rig.Dropped.Add((acct, slot));
        return rig;
    }

    // ---- (1) the issue's reproduction: two clients, one token ----

    // Both heads run this. The first session's load is parked at the gate for the whole handover, which is the exact
    // window the issue's table is written against and the one a genuinely remote store opens.
    private static async Task TheNewerSessionTakesTheSeatAlone(Rig rig)
    {
        Peer first = rig.Connect(Alpha);
        rig.Pump(() => first.Joined && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, first.Client.Slot);
        Assert.Equal(1, rig.Store.PendingLoads);                  // the first session's load really is in flight

        Peer second = rig.Connect(Alpha);                         // the same account, a second client
        rig.Pump(() => second.Joined);

        // The whole point: ONE live session for the account, and it is the newcomer's.
        int slot = rig.OnlySlot();
        Assert.Equal(Alpha, rig.Account(slot));
        Assert.Equal(slot, second.Client.Slot);
        Assert.Equal(new[] { SessionRejectReason.SignedInElsewhere }, first.Rejects);   // and the first was told why
        Assert.Empty(second.Rejects);

        await rig.SettleLoadsAsync();

        // The displaced session's load is dropped as the superseded read it is (#654), and the live session's applies.
        Assert.Equal(new[] { Alpha }, rig.Applied);
        Assert.Equal(new[] { (Alpha, 0) }, rig.Dropped);
        Assert.True(PlanarDistance(rig.State(slot).Position, Stored) < 1f,
            $"the surviving session was never restored ({rig.State(slot).Position})");

        // The last cell of the issue's table. The winner leaves, then the periodic pass runs: with the loser still
        // live on an unguarded seat holding the same account key, that pass wrote its default-spawn state over the
        // record. There is no loser now.
        rig.Drop(second);
        rig.Pump(() => rig.Host.JoinedSlots.Count == 0);
        rig.Persistence.SaveDirtyPass();
        await rig.Persistence.FlushAsync();

        Vector3 stored = await rig.StoredPositionAsync();
        Assert.True(PlanarDistance(stored, Stored) < 1f, $"alpha's record was overwritten with join-time state ({stored})");
    }

    [Fact]
    public async Task A_second_client_on_one_token_takes_the_seat_and_the_first_is_signed_out()
    {
        Rig rig = await SingleAsync();
        await TheNewerSessionTakesTheSeatAlone(rig);
    }

    // ---- (2) the sharded head, which goes through the same join gate ----

    [Fact]
    public async Task A_second_client_on_one_token_takes_the_seat_on_a_sharded_server()
    {
        Rig rig = await ShardedAsync();
        await TheNewerSessionTakesTheSeatAlone(rig);
    }

    // ---- (3) RefuseNewer: the seat belongs to whoever holds it ----

    [Fact]
    public async Task RefuseNewer_keeps_the_live_session_and_turns_the_second_client_away()
    {
        Rig rig = await SingleAsync(DuplicateSessionPolicy.RefuseNewer);

        Peer first = rig.Connect(Alpha);
        rig.Pump(() => first.Joined && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, first.Client.Slot);

        Peer second = rig.Connect(Alpha);
        rig.Pump(() => second.Rejects.Count > 0);

        Assert.Equal(new[] { SessionRejectReason.AlreadySignedIn }, second.Rejects);
        Assert.Equal(-1, second.Client.Slot);                     // never welcomed
        Assert.Empty(first.Rejects);                              // and the live session was not touched
        Assert.Equal(0, rig.OnlySlot());
        Assert.Equal(Alpha, rig.Account(0));

        await rig.SettleLoadsAsync();

        Assert.Equal(new[] { Alpha }, rig.Applied);
        Assert.Empty(rig.Dropped);
        Assert.True(PlanarDistance(rig.State(0).Position, Stored) < 1f,
            $"the live session's own restore should still land ({rig.State(0).Position})");
    }

    // ---- (4) tokenless guests are not one account ----

    [Fact]
    public async Task Two_tokenless_guests_are_both_admitted()
    {
        // Neither connection carries a subject, so there is nothing for the gate to compare. Deduping them would
        // read "anonymous" as an identity and let the first guest on the server lock everyone else out.
        Rig rig = await SingleAsync();

        Peer one = rig.Connect(null);
        rig.Pump(() => one.Joined);
        Peer two = rig.Connect(null);
        rig.Pump(() => two.Joined);

        Assert.Equal(2, rig.Host.JoinedSlots.Count);
        Assert.Equal(0, one.Client.Slot);
        Assert.Equal(1, two.Client.Slot);
        Assert.Empty(one.Rejects);
        Assert.Empty(two.Rejects);
        Assert.Equal(ResumePositionCache.GuestAccountPrefix + "0", rig.Account(0));
        Assert.Equal(ResumePositionCache.GuestAccountPrefix + "1", rig.Account(1));
    }

    // ---- (5) what the displaced player is actually shown ----

    [Fact]
    public void A_displaced_client_surfaces_the_signed_in_elsewhere_reason_and_does_not_reconnect()
    {
        // A reason code nothing carries to the player is not a reason. This is the reconnect-capable client, so it
        // also pins the terminal half: an auto-reconnect here would displace the session that just displaced this
        // one, and the two clients would trade the seat forever.
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 4, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);

        using var client = new WorldClient(() => hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig(), Encoding.UTF8.GetBytes(Alpha));
        for (int i = 0; i < 20 && client.ConnectionState != WorldConnectionState.Connected; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(Dt);
        }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        // The same account signs in again from somewhere else.
        var second = new NetClient(hub.CreateClient(), TestHandshake.Wire(Alpha));
        for (int i = 0; i < 20 && second.Slot < 0; i++)
        {
            second.Poll();
            server.Poll();
            server.Tick(Dt);
        }
        Assert.True(second.Slot >= 0, "the second client never got a slot");

        for (int i = 0; i < 20 && client.DisconnectReason == DisconnectReason.None; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(Dt);
        }

        Assert.Equal(DisconnectReason.SignedInElsewhere, client.DisconnectReason);
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);   // terminal, not Reconnecting
    }

    // ---- (6) the refusal is the one duplicate-session answer that IS retried ----

    [Fact]
    public void An_already_signed_in_refusal_keeps_reconnecting_until_the_seat_is_free()
    {
        // Terminal is wrong here, and expensively so. Under RefuseNewer the thing usually holding the seat is this
        // player's OWN half-dead connection, which the server keeps until its transport timeout expires (LiteNetLib
        // leaves DisconnectTimeout at 5 s), and the default backoff spends its first three attempts inside that
        // window. So a one-second blip used to dump the player at a manual sign-in screen. Retrying a refusal
        // displaces nobody, so it cannot start the ping-pong the KICK has to stay terminal to avoid.
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig
        {
            TickSeconds = Dt,
            MaxPlayers = 4,
            SpawnPosition = _ => Vector3.Zero,
            DuplicateSessions = DuplicateSessionPolicy.RefuseNewer,
        };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);

        // The account's existing session: under RefuseNewer it keeps the seat until its peer goes.
        INetTransport held = hub.CreateClient();
        var incumbent = new NetClient(held, TestHandshake.Wire(Alpha));
        for (int i = 0; i < 20 && incumbent.Slot < 0; i++)
        {
            incumbent.Poll();
            server.Poll();
            server.Tick(Dt);
        }
        Assert.True(incumbent.Slot >= 0, "the incumbent never got a slot");

        using var client = new WorldClient(() => hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig(), Encoding.UTF8.GetBytes(Alpha));
        for (int i = 0; i < 20 && client.DisconnectReason == DisconnectReason.None; i++)
        {
            client.Poll(Dt);
            incumbent.Poll();
            server.Poll();
            server.Tick(Dt);
        }

        Assert.Equal(DisconnectReason.AlreadySignedIn, client.DisconnectReason);
        Assert.Equal(WorldConnectionState.Reconnecting, client.ConnectionState);   // waiting, not given up

        // The old link dies and the server frees the seat. The backoff outlasts that wait, so the player gets in
        // with no manual step at all.
        hub.DisconnectClient(held);
        for (int i = 0; i < 400 && client.ConnectionState != WorldConnectionState.Connected; i++)
        {
            client.Poll(Dt);
            incumbent.Poll();
            server.Poll();
            server.Tick(Dt);
        }

        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }

    // ---- (7) a displaced raw client stops naming a seat it no longer holds ----

    [Fact]
    public async Task A_displaced_raw_client_gives_up_the_slot_it_reports()
    {
        // NetClient.Slot is what a bare-session consumer addresses itself by. The duplicate-session gate is the
        // engine's first Reject AFTER a Welcome, so a client that kept reporting its slot went on naming a seat the
        // session that displaced it is now sitting in.
        Rig rig = await SingleAsync();

        Peer first = rig.Connect(Alpha);
        rig.Pump(() => first.Joined && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, first.Client.Slot);

        Peer second = rig.Connect(Alpha);
        rig.Pump(() => second.Joined);

        Assert.Equal(new[] { SessionRejectReason.SignedInElsewhere }, first.Rejects);
        Assert.Equal(-1, first.Client.Slot);
        Assert.Equal(rig.OnlySlot(), second.Client.Slot);      // the seat the displaced client used to name

        // A plain transport drop gives it up too, which is the same guarantee by the other route.
        rig.Drop(second);
        rig.Pump(() => rig.Host.JoinedSlots.Count == 0);
        Assert.Equal(-1, second.Client.Slot);
    }
}
