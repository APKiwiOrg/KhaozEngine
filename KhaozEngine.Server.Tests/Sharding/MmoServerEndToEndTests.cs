using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.NetWorld;
using KhaozEngine.WorldStore;
using MmoServerSample;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Sharding;

public class MmoServerEndToEndTests
{
    private readonly ITestOutputHelper output;
    public MmoServerEndToEndTests(ITestOutputHelper output) => this.output = output;

    private static void PumpNet(MmoServer server, NetClient client, int rounds = 10)
    {
        for (int i = 0; i < rounds; i++) { server.Poll(); client.Poll(); }
    }

    private static void ApplyClientSnapshots(NetClient client, World world, ClientReplicationView view)
    {
        while (client.TryDequeueEvent(out ClientSessionEvent ev))
            if (ev.Kind == ClientSessionEventKind.Data)
            {
                view.ApplyDelta(world, ev.Data);   // the reference server serves per-client AoI deltas
                client.Send(MmoProtocol.EncodeAck(view.LastAppliedSeq), NetChannelReliability.ReliableOrdered);
            }
    }

    [Fact]
    public void EndToEnd_ClientCrossesBoundary_SingleOwnership_ContinuousView()
    {
        (LoopbackTransport serverTransport, LoopbackTransport clientTransport) = LoopbackTransport.CreatePair();
        var config = new MmoServerConfig
        {
            CellSize = 100f,
            TickSeconds = 0.1f,
            OverlapMargin = 30f,
            InterestRadius = 30f,
            SpawnX = 85f,
            SpawnY = 50f,
        };
        var server = new MmoServer(serverTransport, config);
        long npc = server.SpawnNpc(110f, 50f);                 // across the A/B boundary, owned by B=(1,0)

        var client = new NetClient(clientTransport);

        // Connect + join: the server spawns the player and binds the client.
        PumpNet(server, client);
        Assert.Equal(0, client.Slot);
        Assert.True(server.TryGetPlayerNetId(0, out long player));

        var clientWorld = new World();
        var view = new ClientReplicationView(MmoServer.CreateRegistry());

        // First serve, no input: player owned by A; the across-border NPC is visible as a ghost in A's interest.
        server.Poll();
        server.Tick(0f);
        client.Poll();
        ApplyClientSnapshots(client, clientWorld, view);
        Assert.True(server.Host.TryGetHomeCell(0, out CellSim homeBefore));
        Assert.Equal(new CellCoord(0, 0), homeBefore.Coord);
        Assert.True(view.TryGetEntity(player, out _));
        Assert.True(view.TryGetEntity(npc, out Entity npcBefore));

        // Walk east across the x=100 boundary, one command per tick.
        int seq = 0;
        for (int i = 0; i < 6; i++)
        {
            client.Send(MmoProtocol.EncodeMove(seq++, new MoveCommand(6f, 0f)), NetChannelReliability.ReliableOrdered);
            server.Poll();                       // ingest the command
            server.Tick(config.TickSeconds);     // move -> handoff -> ghost sync -> serve
            client.Poll();

            Assert.Equal(1, server.Host.OwnerCount(player));   // exactly one owner at every tick (no dup/loss)
            ApplyClientSnapshots(client, clientWorld, view);
            Assert.True(view.TryGetEntity(npc, out _));         // the NPC never drops out of the client's view
        }

        // The player re-bound to B; the player and NPC are continuous (the NPC is the same client entity).
        Assert.True(server.Host.TryGetOwner(player, out CellSim owner, out _));
        Assert.Equal(new CellCoord(1, 0), owner.Coord);
        Assert.True(server.Host.TryGetHomeCell(0, out CellSim homeAfter));
        Assert.Equal(new CellCoord(1, 0), homeAfter.Coord);
        Assert.True(view.TryGetEntity(player, out _));
        Assert.True(view.TryGetEntity(npc, out Entity npcAfter));
        Assert.Equal(npcBefore, npcAfter);
    }

    [Fact]
    public void EndToEnd_ClientReadsCreatureKind_PlayerHasNone_AndOldClientTolerates()
    {
        (LoopbackTransport serverTransport, LoopbackTransport clientTransport) = LoopbackTransport.CreatePair();
        var config = new MmoServerConfig
        {
            CellSize = 100f, TickSeconds = 0.1f, OverlapMargin = 30f, InterestRadius = 30f, SpawnX = 50f, SpawnY = 50f,
        };
        var server = new MmoServer(serverTransport, config);
        long npc = server.SpawnNpc(60f, 50f, kind: 5);          // an NPC in the player's area of interest

        var client = new NetClient(clientTransport);
        PumpNet(server, client);
        Assert.True(server.TryGetPlayerNetId(0, out long player));

        byte[] snapshot = ServeOneSnapshot(server, client);

        // A client whose registry KNOWS Creature reads the NPC's kind; the player carries none, so the client tells
        // the NPC apart from a player by the component's presence.
        var world = new World();
        var view = new ClientReplicationView(MmoServer.CreateRegistry());
        view.ApplyDelta(world, snapshot);   // the first serve is a baseline -1 delta = a full snapshot
        Assert.True(view.TryGetEntity(npc, out Entity npcEntity));
        Assert.True(world.TryGet(npcEntity, out Creature creature));
        Assert.Equal(5, creature.Kind);
        Assert.True(view.TryGetEntity(player, out Entity playerEntity));
        Assert.False(world.TryGet(playerEntity, out Creature _));

        // An OLDER client whose registry never registered Creature (only Position) must SKIP the unknown extension
        // component and still apply the snapshot: no throw, still sees the NPC.
        var oldRegistry = new ReplicationRegistry();
        oldRegistry.Register<Position>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Position { X = br.ReadSingle(), Y = br.ReadSingle() });
        var oldView = new ClientReplicationView(oldRegistry);
        bool ok = oldView.TryApplyDelta(new World(), snapshot, out string? error);
        Assert.True(ok);
        Assert.Null(error);
        Assert.True(oldView.TryGetEntity(npc, out _));
    }

    [Fact]
    public void EndToEnd_ClientChatGameMessage_ReachesServer()
    {
        // Demonstrates the generic game-message seam end to end: a client frames a chat line with the engine's
        // game-message codec (MmoProtocol.EncodeChat -> MoveProtocol.EncodeGameMessage) and the reference server
        // demuxes it from the movement stream, surfacing it on ChatReceived - the same shape WorldServer.OnGameMessage
        // gives a turn-key consumer for free.
        (LoopbackTransport serverTransport, LoopbackTransport clientTransport) = LoopbackTransport.CreatePair();
        var server = new MmoServer(serverTransport, new MmoServerConfig { SpawnX = 50f, SpawnY = 50f });
        var client = new NetClient(clientTransport);
        PumpNet(server, client);
        Assert.True(server.TryGetPlayerNetId(0, out _));

        (int slot, string text)? got = null;
        server.ChatReceived += (slot, text) => got = (slot, text);

        client.Send(MmoProtocol.EncodeChat("hello world"), NetChannelReliability.ReliableOrdered);
        PumpNet(server, client);

        Assert.True(got.HasValue, "server never surfaced the chat game message");
        Assert.Equal(0, got!.Value.slot);
        Assert.Equal("hello world", got.Value.text);
        Assert.Equal("hello world", server.LastChat);
    }

    [Fact]
    public async Task PlayerPersistence_RoundTripsBySubject()
    {
        // Demonstrates the full capture/validate/apply loop keyed by the verified session subject: join as
        // "alice", move, mutate the private health blob directly, leave (save-on-leave via PlayerLeaving), flush,
        // then reconnect with the SAME subject on a brand-new server sharing the same store - the account-keyed
        // record round-trips both the position (sample XY <-> engine XZ) and the game's PrivateStats health blob.
        var store = new InMemoryWorldStore();
        byte[] token = Encoding.UTF8.GetBytes("alice");
        var config = new MmoServerConfig { TickSeconds = 0.1f, SpawnX = 50f, SpawnY = 50f };

        float movedX, movedY;
        {
            (LoopbackTransport serverTransport, LoopbackTransport clientTransport) = LoopbackTransport.CreatePair();
            var server = new MmoServer(serverTransport, config, store);
            var client = new NetClient(clientTransport, token);

            PumpNet(server, client);
            Assert.Equal(0, client.Slot);
            Assert.True(server.TryGetPlayerNetId(0, out long netId));

            client.Send(MmoProtocol.EncodeMove(0, new MoveCommand(6f, 4f)), NetChannelReliability.ReliableOrdered);
            server.Poll();
            server.Tick(config.TickSeconds);
            client.Poll();

            Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
            Assert.True(cell.World.TryGet(e, out Position moved));
            movedX = moved.X;
            movedY = moved.Y;
            cell.World.Set(e, new PrivateStats { Health = 42 });

            clientTransport.Disconnect(new NetConnectionId(1));   // server observes Left -> PlayerLeaving -> save-on-leave
            server.Poll();
            server.Tick(config.TickSeconds);
            await server.FlushAsync();
        }

        {
            (LoopbackTransport serverTransport, LoopbackTransport clientTransport) = LoopbackTransport.CreatePair();
            var server = new MmoServer(serverTransport, config, store);
            var client = new NetClient(clientTransport, token);

            PumpNet(server, client);
            Assert.Equal(0, client.Slot);
            await server.FlushAsync();       // settle the async load
            server.Tick(0f);                 // apply the loaded state on the server thread

            Assert.True(server.TryGetPlayerNetId(0, out long netId));
            Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
            Assert.True(cell.World.TryGet(e, out Position restored));
            Assert.Equal(movedX, restored.X, 2);
            Assert.Equal(movedY, restored.Y, 2);
            Assert.True(cell.World.TryGet(e, out PrivateStats stats));
            Assert.Equal(42, stats.Health);
        }
    }

    [Fact]
    public async Task PlayerPersistence_BadHealthBlob_QuarantinedFreshSpawn()
    {
        // A record whose health blob fails ValidatePrivateStats (out of the game's 1..100 range) is quarantined
        // WHOLE: the player is reset to the configured spawn rather than placed at the saved position, and the raw
        // record survives under quarantine:player:{accountId} for offline inspection. This is a first-ever join on
        // a fresh process, so there is no resume hint and the reset lands where the join already built it - the
        // rejoin case, where the reset is the only thing that moves the player, is
        // WorldPersistenceReconnectTeleportTests.
        var store = new InMemoryWorldStore();
        byte[] badBlob = BitConverter.GetBytes(9999);
        await store.SaveAsync("player:alice",
            PlayerRecord.From(new PlayerMoveState { Position = new Vector3(70f, 0f, 80f) }, badBlob).Encode());

        (LoopbackTransport serverTransport, LoopbackTransport clientTransport) = LoopbackTransport.CreatePair();
        var config = new MmoServerConfig { TickSeconds = 0.1f, SpawnX = 50f, SpawnY = 50f };
        var server = new MmoServer(serverTransport, config, store);
        var client = new NetClient(clientTransport, Encoding.UTF8.GetBytes("alice"));

        PumpNet(server, client);
        Assert.Equal(0, client.Slot);
        server.Tick(config.TickSeconds);   // drains the apply queue -> validates -> quarantines
        await server.FlushAsync();

        Assert.True(server.TryGetPlayerNetId(0, out long netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out Position spawned));
        Assert.Equal(config.SpawnX, spawned.X);
        Assert.Equal(config.SpawnY, spawned.Y);

        Assert.NotNull(await store.LoadAsync("quarantine:player:alice"));
    }

    [Fact]
    public void PlayerRejoin_IsBuiltWhereItLeft_NotOnTheSpawn()
    {
        // The sample implements the resume-spawn seam (SetResumePositionProvider + TryGetConfiguredSpawn) rather
        // than taking the default no-op, so a rejoining account's entity is BUILT where it left. Both are DEFAULT
        // interface methods, which means a head that omits them compiles and silently keeps the double teleport
        // (#642) - and this is the file a game copies for its own head, so it demonstrates the contract instead.
        var store = new InMemoryWorldStore();
        var hub = new InMemoryHub();
        var config = new MmoServerConfig { TickSeconds = 0.1f, SpawnX = 50f, SpawnY = 50f };
        var server = new MmoServer(hub.Server, config, store);
        byte[] token = Encoding.UTF8.GetBytes("alice");

        // Where each join built its entity, read from PlayerJoined - before any restore could have run.
        var builtAt = new List<Position>();
        server.PlayerJoined += (slot, _) =>
        {
            if (server.TryGetPlayerNetId(slot, out long id)
                && server.Host.TryGetOwner(id, out CellSim c, out Entity en)
                && c.World.TryGet(en, out Position p)) builtAt.Add(p);
        };

        INetTransport first = hub.CreateClient();
        var client = new NetClient(first, token);
        PumpNet(server, client);
        Assert.Equal(0, client.Slot);

        client.Send(MmoProtocol.EncodeMove(0, new MoveCommand(6f, 4f)), NetChannelReliability.ReliableOrdered);
        server.Poll();
        server.Tick(config.TickSeconds);
        Assert.True(server.TryGetPlayerNetId(0, out long netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        Assert.True(cell.World.TryGet(e, out Position moved));
        Assert.True(moved.X > config.SpawnX, "the move has to leave the spawn for this row to prove anything");

        hub.DisconnectClient(first);
        server.Poll();                       // Left -> PlayerLeaving -> save-on-leave, and the hint is recorded
        server.Tick(config.TickSeconds);

        var rejoined = new NetClient(hub.CreateClient(), token);
        PumpNet(server, rejoined);
        Assert.Equal(0, rejoined.Slot);      // the freed slot is recycled, and the ACCOUNT is what carries the hint

        Assert.Equal(2, builtAt.Count);
        Assert.Equal(config.SpawnX, builtAt[0].X);   // a first-ever join is untouched: the configured spawn
        Assert.Equal(config.SpawnY, builtAt[0].Y);
        Assert.Equal(moved.X, builtAt[1].X, 2);      // the rejoin is built where it left, not on the spawn
        Assert.Equal(moved.Y, builtAt[1].Y, 2);
    }

    // Serves one authoritative frame and returns the raw replication snapshot the client received.
    private static byte[] ServeOneSnapshot(MmoServer server, NetClient client)
    {
        server.Poll();
        server.Tick(0f);
        client.Poll();
        byte[]? last = null;
        while (client.TryDequeueEvent(out ClientSessionEvent ev))
            if (ev.Kind == ClientSessionEventKind.Data) last = ev.Data;
        Assert.NotNull(last);
        return last!;
    }

    [Trait("Category", "LiveSocket")]
    [Fact]
    public void LiveSocket_ClientConnects_AndIsServedItsPlayer()
    {
        // OS-assigned ephemeral port, never a fixed one - a hardcoded port collides with any other process
        // (a stale server, a parallel test run) that happens to hold it.
        using LiteNetLibServerTransport? serverTransport = LiveSocketSupport.TryBindServer(out int port);
        if (serverTransport is null) { output.WriteLine(LiveSocketSupport.NoFreePortReason); return; }

        var config = new MmoServerConfig { SpawnX = 50f, SpawnY = 50f };
        var server = new MmoServer(serverTransport, config);
        server.SpawnNpc(60f, 50f);

        using var clientTransport = new LiteNetLibClientTransport("127.0.0.1", port);
        var client = new NetClient(clientTransport);
        var clientWorld = new World();
        var view = new ClientReplicationView(MmoServer.CreateRegistry());

        var sw = Stopwatch.StartNew();
        bool served = false;
        while (sw.ElapsedMilliseconds < 3000 && !served)
        {
            server.Poll();
            server.Tick(config.TickSeconds);
            client.Poll();
            while (client.TryDequeueEvent(out ClientSessionEvent ev))
                if (ev.Kind == ClientSessionEventKind.Data)
                {
                    view.ApplyDelta(clientWorld, ev.Data);
                    client.Send(MmoProtocol.EncodeAck(view.LastAppliedSeq), NetChannelReliability.ReliableOrdered);
                }

            if (client.Slot >= 0 && server.TryGetPlayerNetId(client.Slot, out long pid) && view.TryGetEntity(pid, out _))
                served = true;
            Thread.Sleep(10);
        }

        Assert.True(served, "client never received its player entity over a live socket");
    }
}
