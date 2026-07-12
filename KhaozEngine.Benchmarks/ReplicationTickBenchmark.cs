using System;
using System.Collections.Generic;
using System.Diagnostics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// Builds a populated <see cref="World"/> + <see cref="ReplicationRegistry"/> + <see cref="AoiDeltaReplicator"/> +
/// <see cref="InterestGrid"/> from a <see cref="ReplicationBenchmarkConfig"/>, and times the real replication hot
/// path: <c>BeginTick</c>, one shared interest-grid rebuild (Clear + reinsert of every freshly-moved position),
/// then PER CLIENT one <c>InterestGrid.Query</c> + <c>AoiDeltaReplicator.WriteFor</c>. The once-per-tick rebuild
/// mirrors production after the interest-sharing change: <c>ShardedWorldServer.Tick</c> passes one serve epoch, so
/// <c>ShardHost.HomeInterest</c> rebuilds each home cell's grid once per tick (shared across clients), and
/// <c>WriteFor</c> scans + captures the world once per tick and projects each client's delta from that shared
/// capture. So the timed loop measures the shared path: one grid rebuild and one world capture per tick, then each
/// client's <c>WriteFor</c> projecting its delta from that shared capture (still a walk of the whole capture
/// filtered by the client's interest set, not a fresh per-client world scan). Each client acknowledges the PREVIOUS
/// tick's snapshot at the start of the current one (a simulated 1-tick RTT), so once past the always-full first
/// tick the timed loop measures the steady-state delta-from-last-ack path. <see cref="AoiDeltaReplicator"/> and
/// <see cref="InterestGrid"/> are never modified here - only measured.
/// </summary>
public static class ReplicationTickBenchmark
{
    /// <summary>
    /// Builds a fully-populated <see cref="ReplicationPopulation"/> for <paramref name="config"/>:
    /// <see cref="ReplicationBenchmarkConfig.EntityCount"/> entities seeded across a
    /// <see cref="ReplicationBenchmarkConfig.FieldSize"/> square field, each with a <see cref="NetId"/> and
    /// <see cref="ReplicationBenchmarkConfig.ComponentsPerEntity"/> replicated components registered in a fresh
    /// <see cref="ReplicationRegistry"/>, plus <see cref="ReplicationBenchmarkConfig.ClientCount"/> simulated
    /// clients each with a fixed AoI center (padded so its circle never clips the field edge) and radius. No
    /// ticks are run. Deterministic: the same <see cref="ReplicationBenchmarkConfig.Seed"/> produces the same
    /// population and client placement every time.
    /// </summary>
    public static ReplicationPopulation Build(ReplicationBenchmarkConfig config)
    {
        var world = new World();
        var registry = new ReplicationRegistry();
        RegisterComponents(registry, config.ComponentsPerEntity);

        var rng = new DeterministicRng(config.Seed);
        var entities = new Entity[config.EntityCount];
        var netIds = new long[config.EntityCount];
        var velX = new float[config.EntityCount];
        var velY = new float[config.EntityCount];

        // Population order is fixed (i = 0..EntityCount-1) and the RNG is consumed in a fixed order per entity
        // (position, then optional fillers, then velocity), so the same seed reproduces the same world bit-for-bit.
        for (int i = 0; i < config.EntityCount; i++)
        {
            float x = rng.NextFloat() * config.FieldSize;
            float y = rng.NextFloat() * config.FieldSize;
            Entity e = world.Spawn();
            long netId = i + 1; // node-0 single-process ids, matching NetIdAllocator's first-id convention
            world.Set(e, new NetId(netId));
            world.Set(e, new ReplPosition { X = x, Y = y });
            if (config.ComponentsPerEntity >= 2) world.Set(e, new ReplFillerA { Value = rng.NextFloat() });
            if (config.ComponentsPerEntity >= 3) world.Set(e, new ReplFillerB { Value = rng.NextFloat() });
            if (config.ComponentsPerEntity >= 4) world.Set(e, new ReplFillerC { Value = rng.NextFloat() });

            entities[i] = e;
            netIds[i] = netId;
            velX[i] = (rng.NextFloat() - 0.5f) * 2f;
            velY[i] = (rng.NextFloat() - 0.5f) * 2f;
        }

        // Client centers are padded away from every edge by the AoI radius, so each client's full circle stays
        // inside the field - every client sees the same ~10% area fraction (see ReplicationBenchmarkConfig.AoiRadius),
        // never a partial circle clipped short by a nearby edge.
        float radius = config.AoiRadius;
        float pad = MathF.Min(radius, config.FieldSize / 2f);
        float span = config.FieldSize - 2f * pad;
        var clients = new ClientAoi[config.ClientCount];
        for (int c = 0; c < config.ClientCount; c++)
        {
            float cx = pad + rng.NextFloat() * span;
            float cy = pad + rng.NextFloat() * span;
            clients[c] = new ClientAoi(c, cx, cy, radius);
        }

        var grid = new InterestGrid(MathF.Max(radius, 1f));
        RefAction<NetId, ReplPosition> insertIntoGrid = (Entity _, ref NetId id, ref ReplPosition p) => grid.Insert(id.Value, p.X, p.Y);
        var replicator = new AoiDeltaReplicator(registry);

        return new ReplicationPopulation
        {
            World = world,
            Registry = registry,
            Replicator = replicator,
            Grid = grid,
            Entities = entities,
            NetIds = netIds,
            VelX = velX,
            VelY = velY,
            Clients = clients,
            InsertIntoGrid = insertIntoGrid,
        };
    }

    /// <summary>
    /// Builds the population, runs <see cref="ReplicationBenchmarkConfig.WarmupTicks"/> un-timed ticks (JIT/cache
    /// warm, and to move every client past its always-full first snapshot), then times
    /// <see cref="ReplicationBenchmarkConfig.TimedTicks"/> ticks: wall-clock (<see cref="Stopwatch"/>), allocation
    /// (<see cref="GC.GetAllocatedBytesForCurrentThread"/> before/after), GC collection-count deltas, and total
    /// wire bytes written (every client's <c>WriteFor</c> return array length, summed across the whole loop).
    /// </summary>
    public static ReplicationBenchmarkResult Run(ReplicationBenchmarkConfig config)
    {
        ReplicationPopulation pop = Build(config);
        int prevSeq = 0;

        for (int i = 0; i < config.WarmupTicks; i++)
            prevSeq = RunOneTick(pop, config, prevSeq, out _);

        int gen0Before = GC.CollectionCount(0), gen1Before = GC.CollectionCount(1), gen2Before = GC.CollectionCount(2);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();

        long wireBytesTotal = 0;
        int ticksRun = 0;
        for (int i = 0; i < config.TimedTicks; i++)
        {
            prevSeq = RunOneTick(pop, config, prevSeq, out long tickWireBytes);
            wireBytesTotal += tickWireBytes;
            ticksRun++;
        }

        sw.Stop();
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        int gen0After = GC.CollectionCount(0), gen1After = GC.CollectionCount(1), gen2After = GC.CollectionCount(2);

        return new ReplicationBenchmarkResult
        {
            Config = config,
            TicksMeasured = ticksRun,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            AllocatedBytes = allocAfter - allocBefore,
            Gen0Collections = gen0After - gen0Before,
            Gen1Collections = gen1After - gen1Before,
            Gen2Collections = gen2After - gen2Before,
            WireBytesTotal = wireBytesTotal,
        };
    }

    /// <summary>
    /// One server tick of the replication hot path: ack the previous tick's snapshot (simulated 1-tick RTT), move
    /// every entity by its seeded velocity, open a new snapshot seq, rebuild
    /// <see cref="ReplicationPopulation.Grid"/> from the fresh positions ONCE (Clear + full reinsert), then PER CLIENT
    /// run one <c>InterestGrid.Query</c> + <c>AoiDeltaReplicator.WriteFor</c>. The once-per-tick rebuild mirrors
    /// production's cadence after the interest-sharing change (<c>ShardedWorldServer.Tick</c> passes one serve epoch,
    /// so <c>ShardHost.HomeInterest</c> rebuilds each home cell's grid once per tick, shared by every client), and
    /// <c>WriteFor</c> now scans + captures the world once per tick and projects each client's delta from that shared
    /// capture. So this loop measures the shared path: one grid rebuild and one world capture per tick, then each
    /// client's projection walking that shared capture, filtered by its own interest set. Returns the new seq (the
    /// caller passes it back in as <paramref name="prevSeq"/> on the NEXT call) and this tick's total wire bytes via
    /// <paramref name="wireBytes"/>.
    /// </summary>
    private static int RunOneTick(ReplicationPopulation pop, ReplicationBenchmarkConfig config, int prevSeq, out long wireBytes)
    {
        if (prevSeq > 0)
            foreach (ClientAoi c in pop.Clients)
                pop.Replicator.Acknowledge(c.Slot, prevSeq);

        for (int i = 0; i < pop.Entities.Length; i++)
        {
            ref ReplPosition pos = ref pop.World.Get<ReplPosition>(pop.Entities[i]);
            pos.X += pop.VelX[i] * config.MoveStep;
            pos.Y += pop.VelY[i] * config.MoveStep;
        }

        int seq = pop.Replicator.BeginTick();

        // Rebuild the interest grid ONCE per tick, shared by every client (production rebuilds each home cell's grid
        // once per serve pass via the serve epoch, not once per client).
        pop.Grid.Clear();
        pop.World.ForEach(pop.InsertIntoGrid);

        wireBytes = 0;
        foreach (ClientAoi c in pop.Clients)
        {
            HashSet<long> interestSet = pop.Grid.Query(c.Cx, c.Cy, c.Radius);
            // WriteFor shares one per-tick world capture across all clients, projecting each client's delta from it.
            byte[] body = pop.Replicator.WriteFor(c.Slot, pop.World, interestSet);
            wireBytes += body.Length;
        }
        return seq;
    }

    private static void RegisterComponents(ReplicationRegistry registry, int componentsPerEntity)
    {
        registry.Register<ReplPosition>(1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new ReplPosition { X = br.ReadSingle(), Y = br.ReadSingle() });

        if (componentsPerEntity >= 2)
            registry.Register<ReplFillerA>(2,
                write: (f, bw) => bw.Write(f.Value),
                read: br => new ReplFillerA { Value = br.ReadSingle() });

        if (componentsPerEntity >= 3)
            registry.Register<ReplFillerB>(3,
                write: (f, bw) => bw.Write(f.Value),
                read: br => new ReplFillerB { Value = br.ReadSingle() });

        if (componentsPerEntity >= 4)
            registry.Register<ReplFillerC>(4,
                write: (f, bw) => bw.Write(f.Value),
                read: br => new ReplFillerC { Value = br.ReadSingle() });
    }
}
