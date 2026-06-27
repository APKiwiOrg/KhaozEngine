using System.Diagnostics;
using System.Threading;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
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
            if (ev.Kind == ClientSessionEventKind.Data) view.Apply(world, ev.Data);
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
        int npc = server.SpawnNpc(110f, 50f);                 // across the A/B boundary, owned by B=(1,0)

        var client = new NetClient(clientTransport);

        // Connect + join: the server spawns the player and binds the client.
        PumpNet(server, client);
        Assert.Equal(0, client.Slot);
        Assert.True(server.TryGetPlayerNetId(0, out int player));

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
                if (ev.Kind == ClientSessionEventKind.Data) view.Apply(clientWorld, ev.Data);

            if (client.Slot >= 0 && server.TryGetPlayerNetId(client.Slot, out int pid) && view.TryGetEntity(pid, out _))
                served = true;
            Thread.Sleep(10);
        }

        Assert.True(served, "client never received its player entity over a live socket");
    }
}
