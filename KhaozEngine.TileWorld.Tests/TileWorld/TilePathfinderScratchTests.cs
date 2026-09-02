using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Pins the reusable-scratch entry point of <see cref="TilePathfinder"/> against the allocating one:
/// same walk, same reached flag, same end, and the window arrays stop being allocated once a scratch is reused.
/// <para>No <c>DisableParallelization</c> collection: the allocation assertion reads
/// <c>GC.GetAllocatedBytesForCurrentThread</c>, which is thread local, so a class running in parallel on
/// another thread cannot move the number this test measures.</para></summary>
public class TilePathfinderScratchTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    // A wall line at x = 10 with a gap, a sealed box around (31, 31), and open ground everywhere else, so the
    // battery below covers the reached walk, the walk around an obstacle and the nearest-reachable fallback.
    static TileCollisionMap ObstacleMap()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int z = 0; z < 20; z++) doc.AddObject("wall", 10, z, 0, 2);
        for (int i = 29; i <= 33; i++) { doc.AddObject("tree", i, 29, 0, 0); doc.AddObject("tree", i, 33, 0, 0); }
        for (int i = 30; i <= 32; i++) { doc.AddObject("tree", 29, i, 0, 0); doc.AddObject("tree", 33, i, 0, 0); }
        return TileCollisionBaker.Bake(doc, Cat);
    }

    static readonly (int Sx, int Sz, int Gx, int Gz, int Size, int Radius)[] Battery =
    {
        (5, 5, 8, 8, 1, 64),
        (5, 5, 5, 5, 1, 64),
        (8, 5, 12, 5, 1, 64),
        (2, 2, 31, 31, 1, 64),
        (40, 40, 3, 3, 1, 64),
        (20, 20, 24, 26, 2, 12),
        (1, 1, 60, 60, 1, 8),
    };

    static void AssertSame(TilePath expected, TilePath actual, string label)
    {
        Assert.Equal(expected.Reached, actual.Reached);
        Assert.Equal(expected.End, actual.End);
        Assert.Equal(expected.Tiles.Count, actual.Tiles.Count);
        for (int i = 0; i < expected.Tiles.Count; i++)
            Assert.Equal(expected.Tiles[i], actual.Tiles[i]);
        Assert.NotNull(label);
    }

    static TilePath Run(TileCollisionMap map, (int Sx, int Sz, int Gx, int Gz, int Size, int Radius) q, TilePathfinderScratch? scratch) =>
        TilePathfinder.FindPath(map, 0, new TileCoord(q.Sx, q.Sz, 0), new TileCoord(q.Gx, q.Gz, 0), q.Size, q.Radius, scratch);

    [Fact]
    public void A_reused_scratch_walks_exactly_what_fresh_arrays_walk()
    {
        TileCollisionMap map = ObstacleMap();
        var expected = new List<TilePath>();
        foreach (var q in Battery) expected.Add(Run(map, q, null));

        // One scratch through the whole battery forwards, then backwards. The reverse leg is the one that would
        // catch state carried from a previous call, since every query then runs after a different predecessor.
        var scratch = new TilePathfinderScratch(64);
        for (int i = 0; i < Battery.Length; i++) AssertSame(expected[i], Run(map, Battery[i], scratch), "forward");
        for (int i = Battery.Length - 1; i >= 0; i--) AssertSame(expected[i], Run(map, Battery[i], scratch), "reverse");

        // A scratch sized for a small window grows for a bigger one and still matches.
        var small = new TilePathfinderScratch(4);
        for (int i = 0; i < Battery.Length; i++) AssertSame(expected[i], Run(map, Battery[i], small), "grown");
    }

    [Fact]
    public void A_reused_scratch_stops_allocating_the_window_arrays()
    {
        TileCollisionMap map = ObstacleMap();
        var q = (Sx: 5, Sz: 5, Gx: 40, Gz: 40, Size: 1, Radius: 64);
        var scratch = new TilePathfinderScratch(64);
        // Warm both entry points so the measurement below carries no JIT or first-call allocation.
        Run(map, q, null);
        Run(map, q, scratch);

        const int Calls = 16;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Calls; i++) Run(map, q, null);
        long fresh = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Calls; i++) Run(map, q, scratch);
        long pooled = GC.GetAllocatedBytesForCurrentThread() - before;

        // The window arrays are about 83 KB a call at radius 64, so the fresh leg is over a megabyte and the
        // scratch leg is the result list and nothing else. A tenth is a loose bound on a gap of about 100x.
        Assert.True(fresh > 16 * 64 * 1024, $"fresh leg allocated {fresh} bytes, expected the window arrays");
        Assert.True(pooled * 10 < fresh, $"scratch leg allocated {pooled} bytes against the fresh leg's {fresh}");
    }
}
