using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The GPU-free water body rule: which tiles are water, how a region-plane's water splits into 4-connected
/// bodies cut into disjoint rectangles, and where each body's surface sits. Every expectation is hand-computed from
/// the mask and the corner heights. The render planes built from these bodies are pinned by the render tests.</summary>
public sealed class TileWaterBodiesTests
{
    // The greybox catalogs' water material. The only Kind = Water entry they define.
    const ushort Water = 4;
    const ushort Grass = 1;

    static readonly TileWorldCatalogs Catalogs = TileWorldCatalogs.Greybox();
    static readonly RegionCoord Origin = new(0, 0);

    // One or more regions of grass on plane 0 along +x, which every world here paints water into.
    static TileWorldDocument GrassWorld(int regionsX = 1)
    {
        var regions = new RegionCoord[regionsX];
        for (int rx = 0; rx < regionsX; rx++) regions[rx] = new RegionCoord(rx, 0);
        return TileWorldTestData.FlatWorld(4, regions);
    }

    static void Paint(TileWorldDocument doc, int x, int z, int width, int height, ushort material = Water)
    {
        for (int tz = z; tz < z + height; tz++)
            for (int tx = x; tx < x + width; tx++)
                doc.SetUnderlay(tx, tz, 0, material);
    }

    static void RaiseCorners(TileWorldDocument doc, int x0, int z0, int x1, int z1, short cm)
    {
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                doc.SetCornerHeightCm(x, z, 0, cm);
    }

    static IReadOnlyList<TileWaterBody> Collect(TileWorldDocument doc, RegionCoord? region = null) =>
        TileWaterBodies.Collect(doc, Catalogs, region ?? Origin, 0);

    static bool[,] Mask(int width, int height, params (int X, int Z, int W, int H)[] blocks)
    {
        var mask = new bool[width, height];
        foreach ((int bx, int bz, int bw, int bh) in blocks)
            for (int z = bz; z < bz + bh; z++)
                for (int x = bx; x < bx + bw; x++)
                    mask[x, z] = true;
        return mask;
    }

    [Fact]
    public void TheSurfaceDropIsTwoCentimetres()
    {
        Assert.Equal(0.02f, TileWaterBodies.SurfaceDropMetres);
    }

    [Fact]
    public void AStraightRiverIsOneBodyOfOneRectangle()
    {
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 5, 3, 20);

        TileWaterBody body = Assert.Single(Collect(doc));
        Assert.Equal(new TileRect(10, 5, 3, 20), Assert.Single(body.Rects));
    }

    [Fact]
    public void ABendIsOneBodyOfTwoRectangles()
    {
        // An L: rows 5 to 7 are one 10-wide run, rows 8 to 14 a 3-wide run that cannot extend it.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 5, 3, 10);
        Paint(doc, 13, 5, 7, 3);

        TileWaterBody body = Assert.Single(Collect(doc));
        Assert.Equal(new[] { new TileRect(10, 5, 10, 3), new TileRect(10, 8, 3, 7) }, body.Rects);
    }

    [Fact]
    public void APondInsideALoopIsItsOwnBodyAtItsOwnHeight()
    {
        // A 10x10 ring two tiles thick with a 2x2 pond in the hole touching nothing. The ring's corners are at
        // 200 cm and the pond's own four corners at 100, which no ring tile shares.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 10, 10);
        Paint(doc, 12, 12, 6, 6, Grass);
        Paint(doc, 14, 14, 2, 2);
        RaiseCorners(doc, 10, 10, 20, 20, 200);
        RaiseCorners(doc, 14, 14, 16, 16, 100);

        IReadOnlyList<TileWaterBody> bodies = Collect(doc);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(
            new[] { new TileRect(10, 10, 10, 2), new TileRect(10, 12, 2, 6), new TileRect(18, 12, 2, 6), new TileRect(10, 18, 10, 2) },
            bodies[0].Rects);
        Assert.Equal(new TileRect(14, 14, 2, 2), Assert.Single(bodies[1].Rects));
        Assert.Equal(1.98d, bodies[0].SurfaceY, 4);
        Assert.Equal(0.98d, bodies[1].SurfaceY, 4);
    }

    [Fact]
    public void TheSurfaceSitsTwoCentimetresUnderTheBodysRim()
    {
        // A 3x3 pool whose corner block is at 150 cm with the four interior corners dug to 90. The rim is what
        // the surface follows, so a sunk bed must not move it.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 3, 3);
        RaiseCorners(doc, 10, 10, 13, 13, 150);
        RaiseCorners(doc, 11, 11, 12, 12, 90);

        Assert.Equal(1.48d, Assert.Single(Collect(doc)).SurfaceY, 4);
    }

    [Fact]
    public void ABodyCrossingARegionBorderIsClippedToEachRegion()
    {
        TileWorldDocument doc = GrassWorld(regionsX: 2);
        Paint(doc, 60, 10, 10, 3);

        Assert.Equal(new TileRect(60, 10, 4, 3), Assert.Single(Assert.Single(Collect(doc, new RegionCoord(0, 0))).Rects));
        Assert.Equal(new TileRect(64, 10, 6, 3), Assert.Single(Assert.Single(Collect(doc, new RegionCoord(1, 0))).Rects));
    }

    [Fact]
    public void ABodyCrossingARegionBorderKeepsTodaysPerRegionRim()
    {
        // One body spanning x 60 to 69 over the border at x 64. Region 0's own corners sit at 100 cm, the shared
        // border column x 64 at 150, and region 1's corners east of it at 300. Each region's clipped piece takes its
        // rim from its own rects with the far corners included, so region 0 reads the border column across the clip
        // (150, not 100) and never sees region 1's 300. One rim for the whole body would give both pieces 2.98.
        TileWorldDocument doc = GrassWorld(regionsX: 2);
        Paint(doc, 60, 10, 10, 3);
        RaiseCorners(doc, 60, 10, 63, 13, 100);
        RaiseCorners(doc, 64, 10, 64, 13, 150);
        RaiseCorners(doc, 65, 10, 70, 13, 300);

        Assert.Equal(1.48d, Assert.Single(Collect(doc, new RegionCoord(0, 0))).SurfaceY, 4);
        Assert.Equal(2.98d, Assert.Single(Collect(doc, new RegionCoord(1, 0))).SurfaceY, 4);
    }

    [Fact]
    public void ANoDrawWaterTileIsNotWater()
    {
        // A hole the ground skips has no bed under it, so it is not water even with a water underlay. The tile
        // beside it still is, and so is a water underlay on ordinary settings.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 2, 1);
        doc.SetSettings(10, 10, 0, TileSettings.NoDraw);

        Assert.False(TileWaterBodies.IsWater(doc, Catalogs, 10, 10, 0));
        Assert.True(TileWaterBodies.IsWater(doc, Catalogs, 11, 10, 0));
        Assert.False(TileWaterBodies.IsWater(doc, Catalogs, 12, 10, 0));
        Assert.Equal(new TileRect(11, 10, 1, 1), Assert.Single(Assert.Single(Collect(doc)).Rects));
    }

    [Fact]
    public void AWaterOverlayOnGrassIsNotWater()
    {
        // Only the underlay counts. A water-material overlay on a grass underlay is a decoration with no rim.
        TileWorldDocument doc = GrassWorld();
        doc.SetOverlay(10, 10, 0, Water);

        Assert.False(TileWaterBodies.IsWater(doc, Catalogs, 10, 10, 0));
        Assert.Empty(Collect(doc));
    }

    [Fact]
    public void ARegionWithNoWaterOrNoRegionCollectsNothing()
    {
        Assert.Empty(Collect(GrassWorld()));
        Assert.Empty(Collect(GrassWorld(), new RegionCoord(7, 7)));
    }

    [Fact]
    public void DiagonalNeighboursAreSeparateBodies()
    {
        // Four-connected, so a corner touch is two bodies rather than one.
        IReadOnlyList<IReadOnlyList<TileRect>> bodies = TileWaterBodies.Components(Mask(8, 8, (1, 1, 1, 1), (2, 2, 1, 1)));
        Assert.Equal(2, bodies.Count);
        Assert.Equal(new TileRect(1, 1, 1, 1), Assert.Single(bodies[0]));
        Assert.Equal(new TileRect(2, 2, 1, 1), Assert.Single(bodies[1]));
    }

    [Fact]
    public void EveryDecompositionIsDisjointAndCoversItsMask()
    {
        // Two hundred seeded masks. The seed is fixed so a failure is reproducible.
        var random = new Random(20260819);
        for (int trial = 0; trial < 200; trial++)
        {
            const int size = 14;
            var mask = new bool[size, size];
            var covered = new bool[size, size];
            for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                    mask[x, z] = random.Next(100) < 45;

            var all = new List<TileRect>();
            foreach (IReadOnlyList<TileRect> body in TileWaterBodies.Components(mask)) all.AddRange(body);
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                    Assert.False(all[i].Intersects(all[j]), $"trial {trial}: {all[i]} and {all[j]} overlap");

            foreach (TileRect rect in all)
                for (int z = rect.Z; z < rect.Z1; z++)
                    for (int x = rect.X; x < rect.X1; x++)
                        covered[x, z] = true;

            for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                    Assert.Equal(mask[x, z], covered[x, z]);
        }
    }

    [Fact]
    public void IdenticalRowRunsMergeAndDifferentOnesDoNot()
    {
        // The same span extends, any other span opens a new rectangle even when the rows are adjacent.
        Assert.Equal(new TileRect(2, 0, 3, 4), Assert.Single(TileWaterBodies.Rectangles(Mask(8, 8, (2, 0, 3, 4)))));

        IReadOnlyList<TileRect> widened = TileWaterBodies.Rectangles(Mask(8, 8, (2, 0, 3, 2), (2, 2, 4, 2)));
        Assert.Equal(new[] { new TileRect(2, 0, 3, 2), new TileRect(2, 2, 4, 2) }, widened);
    }
}
