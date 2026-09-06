using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldServerTests
{
    private static float Flat(float x, float z) => 0f;

    private static readonly MoveCommand East = new(new Vector2(1f, 0f), run: true, cameraYaw: 0f);

    // Small cells so a player crosses a boundary in a handful of ticks.
    private static ShardedWorldServerConfig SmallCells(Func<int, Vector3>? spawn = null) => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = spawn,
    };

    private static int JoinClient(ShardedWorldServer server, NetClient client, ShardedWorldServerConfig cfg)
    {
        for (int i = 0; i < 200; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(cfg.TickSeconds);
            if (client.Slot >= 0 && server.TryGetPlayerNetId(client.Slot, out _)) return client.Slot;
        }
        throw new Xunit.Sdk.XunitException("client never joined");
    }

    private static float OwnedX(ShardedWorldServer server, long netId)
    {
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        return cell.World.Get<ReplicatedPosition>(e).Value.X;
    }

    private static bool Sees(ShardedWorldServer server, int slot, long netId, float interestRadius)
    {
        byte[] snap = server.Host.SnapshotForClient(slot, interestRadius);
        var view = new ClientReplicationView(server.Registry);
        view.Apply(new World(), snap);
        return view.TryGetEntity(netId, out _);
    }

    [Fact]
    public void Join_SpawnsPlayer_OwnedByItsCell()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells(_ => new Vector3(5f, 0f, 5f));   // cell (0,0)
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire());

        int slot = JoinClient(server, client, cfg);
        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        Assert.Equal(1, server.PlayerCount);
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out _));
        Assert.Equal(new CellCoord(0, 0), cell.Coord);
    }

    [Fact]
    public void Crossing_Boundary_OwnedByExactlyOneCell_NetIdStable_PositionContinuous()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells(_ => new Vector3(8f, 0f, 5f));   // cell (0,0), near east edge x=10
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire());
        int slot = JoinClient(server, client, cfg);
        Assert.True(server.TryGetPlayerNetId(slot, out long netId));

        float maxStep = MoveTuning.Default.RunSpeed * cfg.TickSeconds * 1.5f;
        bool crossed = false;
        float prevX = OwnedX(server, netId);
        for (int i = 0; i < 120; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, East), NetChannelReliability.ReliableOrdered);
            server.Poll();
            server.Tick(cfg.TickSeconds);
            client.Poll();

            Assert.Equal(1, server.Host.OwnerCount(netId));        // never 0 (loss) or 2 (dup)
            Assert.True(server.TryGetPlayerNetId(slot, out long stillNetId));
            Assert.Equal(netId, stillNetId);                        // NetId stable across handoff

            float x = OwnedX(server, netId);
            Assert.True(x - prevX <= maxStep + 1e-3f, $"position jumped {prevX}->{x} (handoff teleport)");
            prevX = x;
            if (server.Host.TryGetOwner(netId, out CellSim owner, out _) && owner.Coord.X >= 1) crossed = true;
        }
        Assert.True(crossed, "player never crossed into the neighbour cell");
    }

    [Fact]
    public void Ghosting_AdjacentPlayersSeeEachOther_FarPlayerDoesNot()
    {
        var hub = new InMemoryTransportHub();
        // slot0 @ x=8.5 (cell0), slot1 @ x=11.5 (cell1) - 3 m apart across x=10; slot2 @ x=55 (cell5) far.
        var cfg = SmallCells(slot => slot switch
        {
            0 => new Vector3(8.5f, 0f, 5f),
            1 => new Vector3(11.5f, 0f, 5f),
            _ => new Vector3(55f, 0f, 5f),
        });
        var server = new ShardedWorldServer(hub.Server, cfg, Flat, MoveTuning.Default);
        var c0 = new NetClient(hub.CreateClient(), TestHandshake.Wire());
        var c1 = new NetClient(hub.CreateClient(), TestHandshake.Wire());
        var c2 = new NetClient(hub.CreateClient(), TestHandshake.Wire());

        for (int i = 0; i < 50; i++) { c0.Poll(); c1.Poll(); c2.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.Equal(3, server.PlayerCount);
        Assert.True(server.TryGetPlayerNetId(0, out long n0));
        Assert.True(server.TryGetPlayerNetId(1, out long n1));
        Assert.True(server.TryGetPlayerNetId(2, out long n2));

        Assert.True(Sees(server, slot: 0, n1, cfg.InterestRadius));   // adjacent across border -> ghost in home AoI
        Assert.True(Sees(server, slot: 1, n0, cfg.InterestRadius));
        Assert.False(Sees(server, slot: 2, n0, cfg.InterestRadius));  // far player pulls no distant ghost
        Assert.False(Sees(server, slot: 2, n1, cfg.InterestRadius));
        Assert.True(Sees(server, slot: 2, n2, cfg.InterestRadius));   // ...but sees itself
    }

    [Fact]
    public void Determinism_SingleThreaded_Matches_ThreadPool()
    {
        List<(Vector3 pos, CellCoord cell)> Run(IJobScheduler sched)
        {
            var hub = new InMemoryTransportHub();
            var cfg = SmallCells(slot => new Vector3(7f + slot * 2f, 0f, 5f));
            var server = new ShardedWorldServer(hub.Server, cfg, Flat, MoveTuning.Default) { Scheduler = sched };
            var a = new NetClient(hub.CreateClient(), TestHandshake.Wire());
            var b = new NetClient(hub.CreateClient(), TestHandshake.Wire());
            for (int i = 0; i < 60; i++) { a.Poll(); b.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }

            var ar = new MoveCommand(new Vector2(1f, 0f), true, 0f);
            var br = new MoveCommand(new Vector2(0f, 1f), false, 0f);
            for (int i = 0; i < 120; i++)
            {
                a.Send(MoveProtocol.EncodeMove(i, ar), NetChannelReliability.ReliableOrdered);
                b.Send(MoveProtocol.EncodeMove(i, br), NetChannelReliability.ReliableOrdered);
                a.Poll(); b.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
            }
            var outp = new List<(Vector3, CellCoord)>();
            foreach (int slot in new[] { 0, 1 })
            {
                Assert.True(server.TryGetPlayerNetId(slot, out long id));
                Assert.True(server.Host.TryGetOwner(id, out CellSim cell, out Entity e));
                outp.Add((cell.World.Get<ReplicatedPosition>(e).Value, cell.Coord));
            }
            return outp;
        }

        Assert.Equal(Run(new SingleThreadedJobScheduler()), Run(new ThreadPoolJobScheduler()));
    }

    [Fact]
    public void RealWorldClient_WalksAcrossBoundary_NoSnap_NetIdStable()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells(_ => new Vector3(8f, 0f, 5f));   // cell (0,0), near east edge
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = cfg.TickSeconds });

        // Connect + first serves to seed the prediction basis.
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        Assert.True(client.LocalNetId > 0);
        long localNetId = client.LocalNetId;

        float maxStep = MoveTuning.Default.RunSpeed * cfg.TickSeconds * 2f;
        float prevX = LocalX(client);
        bool crossed = false;
        for (int i = 0; i < 120; i++)
        {
            client.SendInput(East);
            server.Poll();
            server.Tick(cfg.TickSeconds);
            client.Poll();
            client.AdvancePresentation(cfg.TickSeconds);          // drive the render smoothing (presentation per frame)

            Assert.Equal(localNetId, client.LocalNetId);          // stable identity across the migrate
            float x = LocalX(client);
            Assert.True(x - prevX <= maxStep + 1e-3f, $"client view snapped {prevX}->{x} at handoff");
            prevX = x;
            if (server.Host.TryGetOwner(localNetId, out CellSim owner, out _) && owner.Coord.X >= 1) crossed = true;
        }
        Assert.True(crossed, "player never crossed the boundary");
        Assert.True(LocalX(client) > 10f, "client's local avatar should be past the x=10 boundary");
    }

    [Fact]
    public void Reconnect_OnRecycledSlot_CanMove()
    {
        var hub = new InMemoryTransportHub();
        var cfg = SmallCells(_ => new Vector3(5f, 0f, 5f));   // both players spawn in cell (0,0)
        var server = new ShardedWorldServer(hub.Server, cfg, Flat, MoveTuning.Default);

        // Client A joins on slot 0 and plays enough ticks to push that slot's processed high-water mark up.
        INetTransport aTransport = hub.CreateClient();
        var a = new NetClient(aTransport, TestHandshake.Wire());
        int slotA = JoinClient(server, a, cfg);

        const int played = 40;
        for (int seq = 0; seq < played; seq++)
        {
            a.Send(MoveProtocol.EncodeMove(seq, East), NetChannelReliability.ReliableOrdered);
            a.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }

        // A disconnects; the server frees slot 0 for reuse.
        hub.DisconnectClient(aTransport);
        server.Poll(); server.Tick(cfg.TickSeconds);
        Assert.Equal(0, server.PlayerCount);

        // Client B joins on the recycled slot 0, sending east commands that legitimately restart at seq 0.
        var b = new NetClient(hub.CreateClient(), TestHandshake.Wire());
        int slotB = JoinClient(server, b, cfg);
        Assert.Equal(slotA, slotB);   // same slot, recycled
        Assert.True(server.TryGetPlayerNetId(slotB, out long bNet));

        float xSpawn = OwnedX(server, bNet);
        for (int seq = 0; seq < 20; seq++)
        {
            b.Send(MoveProtocol.EncodeMove(seq, East), NetChannelReliability.ReliableOrdered);
            b.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(OwnedX(server, bNet) > xSpawn + 0.1f,
            $"reconnect on recycled slot froze the player: spawn x {xSpawn} -> {OwnedX(server, bNet)}");
    }

    private static float LocalX(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position.X;
        throw new Xunit.Sdk.XunitException("no local entity in client snapshot");
    }
}
