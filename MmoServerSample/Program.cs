using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.Simulation;

namespace MmoServerSample;

/// <summary>
/// Boots the reference dedicated MMO server on a real UDP socket (LiteNetLib) and runs the authoritative loop on a
/// <see cref="FixedTickHost"/>. A thin client can connect, send move commands, and walk across cell boundaries
/// (authority hands off seamlessly; the home cell serves its whole area-of-interest). The headless equivalent over
/// <c>LoopbackTransport</c> is exercised in the test suite.
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--persistence-demo")
        {
            RunPersistenceDemo().GetAwaiter().GetResult();
            return;
        }

        if (args.Length > 0 && args[0] == "--chat-demo")
        {
            RunChatDemo();
            return;
        }

        int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 47600;
        var config = new MmoServerConfig();

        using var transport = new LiteNetLibServerTransport(port);
        var server = new MmoServer(transport, config);

        // Seed a small static world so a connecting client has neighbours to see across cell borders. Each NPC
        // carries a distinct Creature kind (the consumer discriminator a client reads via TryGetComponent to pick
        // its model); players carry none, so a client tells NPCs apart from other players.
        server.SpawnNpc(config.CellSize * 0.5f, config.CellSize * 0.5f, kind: 1);
        server.SpawnNpc(config.CellSize * 1.5f, config.CellSize * 0.5f, kind: 2);
        server.SpawnNpc(config.CellSize * 1.0f, config.CellSize * 1.0f, kind: 3);

        // Cell parallelism: ShardHost defaults to a single-threaded scheduler (SingleThreadedJobScheduler), the
        // safest default for a library - byte-identical behaviour to before schedulers existed. A dedicated server
        // wants every core, though: cells are disjoint Worlds, so ThreadPoolJobScheduler fans ShardHost.Tick's
        // per-cell sim steps across the thread pool for near-linear-in-cores throughput (measured 3.2x tick
        // speedup at 256 cells on 12 cores). Only Tick is parallelized. The cross-cell passes (SyncGhosts,
        // ProcessHandoffs) stay single-threaded, so this is safe to flip on unconditionally.
        server.Host.Scheduler = new ThreadPoolJobScheduler();

        var clock = new FixedTickHost(config.TickSeconds);
        var sw = Stopwatch.StartNew();
        double last = 0;

        // Idle pacing: sleep only as long as there actually is before the next fixed tick, instead of a fixed
        // Thread.Sleep(5) that ignores the clock entirely. A fixed sleep both OVERSLEEPS (OS sleep granularity -
        // notably Windows' ~15.6 ms default timer resolution - routinely overshoots a short requested duration by
        // 2-3x) and loses track of the tick boundary, so the loop ends up bursting through several queued ticks via
        // FixedTickHost's maxTicksPerFrame catch-up instead of ticking smoothly. SafetyMarginSeconds mirrors that
        // worst-case Windows overshoot so the loop wakes up a little before the tick is due rather than after it.
        // When the remaining time is too small to bother sleeping for, ComputeIdleWaitSeconds returns 0 and the
        // loop yields the final sliver instead, since sub-millisecond OS sleeps are unreliable.
        const float SafetyMarginSeconds = 0.0156f;   // ~ Windows default timer resolution
        const float MinimumSleepSeconds = 0.001f;    // below this, yield the sliver instead of asking the OS to sleep

        Console.WriteLine($"MMO server listening on UDP {port} (tick {1f / config.TickSeconds:0} Hz). Ctrl+C to stop.");
        while (true)
        {
            server.Poll();
            double now = sw.Elapsed.TotalSeconds;
            float elapsed = (float)(now - last);
            last = now;
            clock.Advance(elapsed, _ => server.Tick(config.TickSeconds));

            float waitSeconds = FixedTickHost.ComputeIdleWaitSeconds(clock.SecondsUntilNextTick, SafetyMarginSeconds, MinimumSleepSeconds);
            if (waitSeconds > 0f) Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));
            else Thread.Yield();
        }
    }

    // Demonstrates the generic game-message seam over loopback: a client sends a chat line framed with the engine's
    // game-message codec, and the server surfaces it on ChatReceived, demuxed from the movement stream.
    private static void RunChatDemo()
    {
        var (serverTransport, clientTransport) = KhaozEngine.Netcode.LoopbackTransport.CreatePair();
        var server = new MmoServer(serverTransport, new MmoServerConfig());
        var client = new KhaozEngine.Netcode.NetClient(clientTransport);
        server.ChatReceived += (slot, text) => Console.WriteLine($"[chat-demo] server received from slot {slot}: \"{text}\"");

        for (int i = 0; i < 10; i++) { server.Poll(); client.Poll(); }   // connect + join

        client.Send(MmoProtocol.EncodeChat("hi from the client"),
            KhaozEngine.Netcode.NetChannelReliability.ReliableOrdered);
        for (int i = 0; i < 10; i++) { server.Poll(); client.Poll(); }   // deliver + demux

        Console.WriteLine($"[chat-demo] server.LastChat = \"{server.LastChat}\"");
    }

    private static async System.Threading.Tasks.Task RunPersistenceDemo()
    {
        var store = new KhaozEngine.WorldStore.InMemoryWorldStore();

        // Run 1: fresh server on the shared store. Spawn a resource node, persist, shut down.
        var (a, _) = KhaozEngine.Netcode.LoopbackTransport.CreatePair();
        var run1 = new MmoServer(a, new MmoServerConfig(), store);
        await run1.PreloadAsync();
        long id = run1.SpawnResourceNode(150f, 150f, 42);
        await run1.FlushAsync();
        Console.WriteLine($"[persistence-demo] run1: spawned ResourceNode netId={id} amount=42 at (150,150), persisted.");

        // Run 2: a fresh server on the SAME store simulates a restart. Preload restores the node.
        var (b, _) = KhaozEngine.Netcode.LoopbackTransport.CreatePair();
        var run2 = new MmoServer(b, new MmoServerConfig(), store);
        await run2.PreloadAsync();
        var coord = run2.Host.CoordFor(150f, 150f);
        int count = 0;
        int amount = -1;
        if (run2.Host.TryGetCell(coord, out KhaozEngine.Sharding.CellSim cell))
            cell.World.ForEach<ResourceNode>((KhaozEngine.Ecs.Entity e, ref ResourceNode n) => { count++; amount = n.Amount; });
        Console.WriteLine($"[persistence-demo] run2 (after restart): cell {coord} restored {count} node(s), amount={amount}, nextNetId resumed to {run2.NextNetId}.");
    }
}
