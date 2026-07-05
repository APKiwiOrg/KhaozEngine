using System;
using System.Diagnostics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Simulation;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// Ownership-lookup measurement (gap 6 of the MMO arch review): the per-player / per-NPC-per-tick
/// <see cref="ShardHost.TryGetOwner"/> call. Before the netId -&gt; (cell, entity) index it was a linear
/// <see cref="World.ForEach{T}(RefAction{T})"/> across every cell - O(total entities) per lookup, so the documented
/// OnBeforeTick NPC-brain pattern (TryGetOwner per NPC per tick) was O(NPCs x entities) = quadratic in population.
/// This times TryGetOwner (the O(1) index path) against a naive linear owner-scan over the same host across a sweep
/// of entity counts: the index cost stays flat while the scan grows linearly, which is the whole point of the index.
/// </summary>
public static class OwnerLookupBenchmark
{
    /// <summary>One swept point: per-lookup cost of the index path vs the naive scan at a given entity count.</summary>
    public sealed class Point
    {
        public required int TotalEntities { get; init; }
        public required double IndexNs { get; init; } // ns per TryGetOwner (O(1) index)
        public required double ScanNs { get; init; }  // ns per naive linear owner-scan (pre-index O(N))
        public double Ratio => IndexNs > 0 ? ScanNs / IndexNs : 0;
    }

    private struct BenchNetPos : IComponent { public float X; public float Y; }

    /// <summary>
    /// Builds a <paramref name="gridWidth"/> x <paramref name="gridHeight"/> host with
    /// <paramref name="entitiesPerCell"/> owned entities per cell (eagerly indexed via <see cref="ShardHost.SpawnOwned"/>)
    /// and times a full sweep of <see cref="ShardHost.TryGetOwner"/> over every owned netId both ways - the O(1) index
    /// and a naive linear owner-scan - each after <paramref name="warmup"/> un-timed passes, averaged over
    /// <paramref name="timed"/> passes. Returns per-lookup nanoseconds for each.
    /// </summary>
    public static Point Measure(int gridWidth, int gridHeight, int entitiesPerCell, int warmup, int timed,
        ulong seed = 0xB0B0UL)
    {
        const float cellSize = 100f;
        var registry = new ReplicationRegistry();
        registry.Register<BenchNetPos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new BenchNetPos { X = br.ReadSingle(), Y = br.ReadSingle() });

        var host = new ShardHost(cellSize, 1f / 30f, registry, interestCellSize: cellSize);
        var rng = new DeterministicRng(seed);
        long[] netIds = new long[gridWidth * gridHeight * entitiesPerCell];
        int total = 0;
        long nextNetId = 1;
        for (int cy = 0; cy < gridHeight; cy++)
        for (int cx = 0; cx < gridWidth; cx++)
        {
            float baseX = cx * cellSize, baseY = cy * cellSize;
            for (int i = 0; i < entitiesPerCell; i++)
            {
                float x = baseX + (0.05f + 0.9f * rng.NextFloat()) * cellSize;
                float y = baseY + (0.05f + 0.9f * rng.NextFloat()) * cellSize;
                long netId = nextNetId++;
                Entity e = host.SpawnOwned(x, y, netId, out CellSim cell);
                cell.World.Set(e, new BenchNetPos { X = x, Y = y });
                netIds[total++] = netId;
            }
        }

        // Index lookups are O(1), so sweep every netId for a stable average.
        double indexNs = TimePerLookup(
            () => { for (int i = 0; i < netIds.Length; i++) host.TryGetOwner(netIds[i], out _, out _); },
            netIds.Length, warmup, timed);

        // The naive scan is O(total entities) PER lookup, so sweeping every netId would be O(N^2) and blow up at large
        // N. Sample a fixed, cell-spread subset (stride) so the scan measurement stays tractable while still averaging
        // the per-lookup cost - which is the number that grows with N, the whole point of the contrast.
        int stride = Math.Max(1, netIds.Length / 128);
        int scanCount = 0;
        for (int i = 0; i < netIds.Length; i += stride) scanCount++;
        double scanNs = TimePerLookup(
            () => { for (int i = 0; i < netIds.Length; i += stride) NaiveFindOwner(host, netIds[i]); },
            scanCount, warmup, timed);
        return new Point { TotalEntities = total, IndexNs = indexNs, ScanNs = scanNs };
    }

    // The pre-index behaviour: scan cells and, in each, World.ForEach for the owned netId - O(total entities) per lookup.
    private static void NaiveFindOwner(ShardHost host, long netId)
    {
        foreach (CellSim cell in host.Cells)
        {
            World w = cell.World;
            bool found = false;
            w.ForEach<NetId>((Entity e, ref NetId id) =>
            {
                if (id.Value == netId && !w.Has<Ghost>(e) && !w.Has<Migrating>(e)) found = true;
            });
            if (found) return;
        }
    }

    private static double TimePerLookup(Action pass, int lookupsPerPass, int warmup, int timed)
    {
        for (int i = 0; i < warmup; i++) pass();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < timed; i++) pass();
        sw.Stop();
        long lookups = (long)lookupsPerPass * Math.Max(1, timed);
        return lookups == 0 ? 0 : sw.Elapsed.TotalMilliseconds * 1_000_000.0 / lookups; // ns per lookup
    }
}
