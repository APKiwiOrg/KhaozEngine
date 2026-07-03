using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Sharded delta serving: a boundary crossing is transparent to the client's replication view (a nearby entity that
/// stays in AoI is never despawned+respawned as the local player changes owning cell), and the delta path is
/// deterministic (single-threaded cell ticks == threadpool).
/// </summary>
public class ShardedWorldServerDeltaTests
{
    private static float Flat(float x, float z) => 0f;
    private static readonly MoveCommand East = new(new Vector2(1f, 0f), run: true, cameraYaw: 0f);

    private static ShardedWorldServerConfig SmallCells(Func<int, Vector3>? spawn = null) => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = spawn,
    };

    [Fact]
    public void NearbyEntity_StaysAlive_WithStableIdentity_AsLocalPlayerCrossesBoundary()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells(_ => new Vector3(8f, 0f, 5f));   // cell (0,0), near the east edge x=10
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        // A static NPC sitting ON the boundary (x=10): ghosted into cell 0 and owned by cell 1, so it stays inside the
        // crossing player's AoI while the player's owning cell flips from 0 to 1.
        int npcNetId = server.SpawnEntity(10f, 5f);

        var client = new RawDeltaClient(ct, server.Registry);
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }
        Assert.True(client.Joined && client.LocalNetId > 0);
        int playerNetId = client.LocalNetId;

        Entity? npcEntity = null;
        bool crossedWhileNpcVisible = false;
        bool hasCrossed = false;
        for (int i = 0; i < 90; i++)
        {
            client.SendMove(East);
            server.Poll();
            server.Tick(cfg.TickSeconds);
            client.Poll();

            bool nowInCell1 = server.Host.TryGetOwner(playerNetId, out CellSim owner, out _) && owner.Coord.X >= 1;
            bool npcVisible = client.View.TryGetEntity(npcNetId, out Entity npcE) && client.World.IsAlive(npcE);
            if (npcVisible)
            {
                // Stable client identity for the NPC: if it were despawned+respawned at the handoff, GetOrSpawn would
                // hand back a NEW entity here and this equality would fail.
                if (npcEntity is Entity prev) Assert.Equal(prev, npcE);
                npcEntity = npcE;
            }
            if (nowInCell1 && npcVisible && !hasCrossed) crossedWhileNpcVisible = true;
            hasCrossed |= nowInCell1;
        }

        Assert.True(hasCrossed, "player never crossed the boundary");
        Assert.True(crossedWhileNpcVisible, "the nearby entity should stay visible through the boundary crossing");
    }

    [Fact]
    public void Delta_serving_is_deterministic_single_threaded_vs_threadpool()
    {
        List<(int netId, Vector3 pos)> Run(IJobScheduler sched)
        {
            var hub = new InMemoryHub();
            var cfg = SmallCells(slot => new Vector3(7f + slot * 2f, 0f, 5f));
            var server = new ShardedWorldServer(hub.Server, cfg, Flat, MoveTuning.Default) { Scheduler = sched };
            var a = new RawDeltaClient(hub.CreateClient(), server.Registry);
            var b = new RawDeltaClient(hub.CreateClient(), server.Registry);
            for (int i = 0; i < 60; i++) { a.Poll(); b.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }

            var ar = new MoveCommand(new Vector2(1f, 0f), true, 0f);
            var br = new MoveCommand(new Vector2(0f, 1f), false, 0f);
            for (int i = 0; i < 120; i++)
            {
                a.SendMove(ar);
                b.SendMove(br);
                a.Poll(); b.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
            }

            // Read A's replicated view (the delta-applied client state) as the deterministic fingerprint.
            var outp = new List<(int, Vector3)>();
            foreach (KeyValuePair<int, Entity> kv in a.View.Entities)
                if (a.World.TryGet(kv.Value, out ReplicatedPosition rp)) outp.Add((kv.Key, rp.Value));
            outp.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return outp;
        }

        Assert.Equal(Run(new SingleThreadedJobScheduler()), Run(new ThreadPoolJobScheduler()));
    }
}
