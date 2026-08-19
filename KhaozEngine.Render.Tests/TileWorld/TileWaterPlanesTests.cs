using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The water collector: which tiles are a body, where its surface sits, and how a body is cut into
/// planes. Every expectation here is hand-computed from the mask, because the plane COUNT is the cost the water
/// pass pays and the decomposition is only worth having if it is pinned.</summary>
public class TileWaterPlanesTests
{
    // The greybox catalogs' water material. The only Kind = Water entry they define.
    const ushort Water = 4;
    const ushort Grass = TileRenderTestData.Grass;

    static readonly RegionCoord Origin = new(0, 0);

    // One or more regions of grass on plane 0, which every world here paints water into.
    static TileWorldDocument GrassWorld(int regionsX = 1)
    {
        var doc = new TileWorldDocument { Id = "tile-water-tests", DisplayName = "Tile water tests" };
        for (int rx = 0; rx < regionsX; rx++)
        {
            var region = new RegionCoord(rx, 0);
            doc.GetOrCreateRegion(region);
            for (int z = 0; z < TileRegion.Size; z++)
                for (int x = 0; x < TileRegion.Size; x++)
                    doc.SetUnderlay(region.OriginX + x, region.OriginZ + z, 0, Grass);
        }
        return doc;
    }

    static void Paint(TileWorldDocument doc, int x, int z, int width, int height)
    {
        for (int tz = z; tz < z + height; tz++)
            for (int tx = x; tx < x + width; tx++)
                doc.SetUnderlay(tx, tz, 0, Water);
    }

    static void Unpaint(TileWorldDocument doc, int x, int z, int width, int height)
    {
        for (int tz = z; tz < z + height; tz++)
            for (int tx = x; tx < x + width; tx++)
                doc.SetUnderlay(tx, tz, 0, Grass);
    }

    static void RaiseCorners(TileWorldDocument doc, int x0, int z0, int x1, int z1, short cm)
    {
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                doc.SetCornerHeightCm(x, z, 0, cm);
    }

    static IReadOnlyList<WaterPlane> Collect(TileWorldDocument doc, RegionCoord? region = null) =>
        TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, region ?? Origin, 0);

    // A plane read back as the tile rect it covers, which is what the hand-computed expectations are written in.
    // Undoes ToPlane exactly: world x is tile x, world z is MINUS tile z, so the north edge is the low world z.
    static TileRect TilesOf(WaterPlane plane)
    {
        int x = (int)MathF.Round(plane.CenterX - plane.HalfExtentX);
        int z = (int)MathF.Round(-(plane.CenterZ + plane.HalfExtentZ));
        return new TileRect(x, z, (int)MathF.Round(plane.HalfExtentX * 2f), (int)MathF.Round(plane.HalfExtentZ * 2f));
    }

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
    public void StraightRiverIsOnePlane()
    {
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 5, 3, 20);

        WaterPlane plane = Assert.Single(Collect(doc));
        Assert.Equal(new TileRect(10, 5, 3, 20), TilesOf(plane));
    }

    [Fact]
    public void ABendIsTwoPlanes()
    {
        // An L: a 3-wide arm running north from z 5 to z 15, and a 7-wide arm running east over its first three
        // rows. Rows 5 to 7 are one 10-wide run, rows 8 to 14 are a 3-wide run that cannot extend it.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 5, 3, 10);
        Paint(doc, 13, 5, 7, 3);

        IReadOnlyList<WaterPlane> planes = Collect(doc);
        Assert.Equal(2, planes.Count);
        Assert.Equal(new TileRect(10, 5, 10, 3), TilesOf(planes[0]));
        Assert.Equal(new TileRect(10, 8, 3, 7), TilesOf(planes[1]));
    }

    [Fact]
    public void ARiverThatStepsSidewaysCostsOnePlanePerStep()
    {
        // Three wide, shifted one tile east every four rows, five steps of it. Each step's run has a different
        // x span from the one below, so none of them merge, and consecutive steps overlap by two columns so the
        // whole thing is still one 4-connected body.
        TileWorldDocument doc = GrassWorld();
        for (int step = 0; step < 5; step++) Paint(doc, 10 + step, 10 + 4 * step, 3, 4);

        IReadOnlyList<WaterPlane> planes = Collect(doc);
        Assert.Equal(5, planes.Count);
        for (int step = 0; step < 5; step++)
            Assert.Equal(new TileRect(10 + step, 10 + 4 * step, 3, 4), TilesOf(planes[step]));
        TileWaterPlanes.RequireDisjoint(planes.Select(TilesOf).ToList(), Origin, 0);
    }

    [Fact]
    public void APondInsideALoopIsItsOwnBodyAtItsOwnHeight()
    {
        // A 10x10 ring two tiles thick, with a 2x2 pond floating in the hole and touching nothing. The ring's
        // corners are at 200 cm and the pond's own four corners at 100, which no ring tile shares, so the two
        // surfaces can only agree if the collector merged bodies it should have kept apart.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 10, 10);
        Unpaint(doc, 12, 12, 6, 6);
        Paint(doc, 14, 14, 2, 2);
        RaiseCorners(doc, 10, 10, 20, 20, 200);
        RaiseCorners(doc, 14, 14, 16, 16, 100);

        IReadOnlyList<WaterPlane> planes = Collect(doc);
        Assert.Equal(5, planes.Count);

        // The ring, row by row: a 10-wide cap, the two 2-wide sides, then the far cap.
        Assert.Equal(new TileRect(10, 10, 10, 2), TilesOf(planes[0]));
        Assert.Equal(new TileRect(10, 12, 2, 6), TilesOf(planes[1]));
        Assert.Equal(new TileRect(18, 12, 2, 6), TilesOf(planes[2]));
        Assert.Equal(new TileRect(10, 18, 10, 2), TilesOf(planes[3]));
        Assert.Equal(new TileRect(14, 14, 2, 2), TilesOf(planes[4]));

        for (int i = 0; i < 4; i++) Assert.Equal(1.98d, planes[i].SurfaceY, 4);
        Assert.Equal(0.98d, planes[4].SurfaceY, 4);
        TileWaterPlanes.RequireDisjoint(planes.Select(TilesOf).ToList(), Origin, 0);
    }

    [Fact]
    public void ABodyCrossingARegionBorderIsClippedToEachRegion()
    {
        TileWorldDocument doc = GrassWorld(regionsX: 2);
        Paint(doc, 60, 10, 10, 3);

        Assert.Equal(new TileRect(60, 10, 4, 3), TilesOf(Assert.Single(Collect(doc, new RegionCoord(0, 0)))));
        Assert.Equal(new TileRect(64, 10, 6, 3), TilesOf(Assert.Single(Collect(doc, new RegionCoord(1, 0)))));
    }

    [Fact]
    public void TheSurfaceSitsTwoCentimetresUnderTheBodysRim()
    {
        // A 3x3 pool whose whole corner block is at 150 cm with the four interior corners dug down to 90. The
        // rim is what the surface follows, so a sunk bed must not move it.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 3, 3);
        RaiseCorners(doc, 10, 10, 13, 13, 150);
        RaiseCorners(doc, 11, 11, 12, 12, 90);

        WaterPlane plane = Assert.Single(Collect(doc));
        Assert.Equal(1.48d, plane.SurfaceY, 4);
    }

    [Fact]
    public void NorthIsMinusWorldZ()
    {
        Assert.Equal(11.5d, TileWaterPlanes.ToPlane(new TileRect(10, 10, 3, 3), 0f, 1f).CenterX, 4);
        Assert.Equal(-11.5d, TileWaterPlanes.ToPlane(new TileRect(10, 10, 3, 3), 0f, 1f).CenterZ, 4);
        Assert.Equal(1.5d, TileWaterPlanes.ToPlane(new TileRect(10, 10, 3, 3), 0f, 1f).HalfExtentX, 4);
        Assert.Equal(1.5d, TileWaterPlanes.ToPlane(new TileRect(10, 10, 3, 3), 0f, 1f).HalfExtentZ, 4);

        // The whole point of the sign: a body further NORTH sits at a more negative world z, so a north-up map
        // and a right-handed camera agree.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 5, 2, 2);
        Paint(doc, 10, 30, 2, 2);

        IReadOnlyList<WaterPlane> planes = Collect(doc);
        Assert.Equal(2, planes.Count);
        Assert.Equal(-6d, planes[0].CenterZ, 4);
        Assert.Equal(-31d, planes[1].CenterZ, 4);
        Assert.True(planes[1].CenterZ < planes[0].CenterZ);
    }

    [Fact]
    public void ARegionWithNoWaterCollectsNothing()
    {
        Assert.Empty(Collect(GrassWorld()));
    }

    [Fact]
    public void ARegionThatDoesNotExistCollectsNothing()
    {
        Assert.Empty(Collect(GrassWorld(), new RegionCoord(7, 7)));
    }

    [Fact]
    public void ANoDrawTileIsNotWater()
    {
        // A hole the ground mesher skips has no bed under it for the pass to darken against, so it gets no
        // surface either. The tile beside it still does.
        TileWorldDocument doc = GrassWorld();
        Paint(doc, 10, 10, 2, 1);
        doc.SetSettings(10, 10, 0, TileSettings.NoDraw);

        Assert.Equal(new TileRect(11, 10, 1, 1), TilesOf(Assert.Single(Collect(doc))));
    }

    [Fact]
    public void PastTheThresholdTheCallWarnsOnceNamingTheRegionAndPlane()
    {
        // Seventeen single-tile ponds two tiles apart, so none of them touches another and each costs its own
        // plane. Sixteen is the count the collector accepts in silence.
        TileWorldDocument doc = GrassWorld();
        for (int i = 0; i < 17; i++) Paint(doc, 2 * i, 10, 1, 1);

        IReadOnlyList<WaterPlane> planes = Collect(doc);
        Assert.Equal(17, planes.Count);

        string warning = Assert.IsType<string>(TileWaterPlanes.OverflowWarning(planes.Count, Origin, 0));
        Assert.Contains("(0, 0)", warning, StringComparison.Ordinal);
        Assert.Contains("plane 0", warning, StringComparison.Ordinal);
        Assert.Contains("17", warning, StringComparison.Ordinal);

        Assert.Null(TileWaterPlanes.OverflowWarning(TileWaterPlanes.PlaneCountWarnThreshold, Origin, 0));
        Assert.Null(TileWaterPlanes.OverflowWarning(0, Origin, 0));
    }

    [Fact]
    public void OverlappingRectanglesAreRejected()
    {
        var overlapping = new List<TileRect> { new(0, 0, 4, 4), new(3, 3, 4, 4) };
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => TileWaterPlanes.RequireDisjoint(overlapping, Origin, 0));
        Assert.Contains("overlap", error.Message, StringComparison.Ordinal);

        // Touching along an edge is not overlapping: the far edges of a TileRect are exclusive.
        TileWaterPlanes.RequireDisjoint(new List<TileRect> { new(0, 0, 4, 4), new(4, 0, 4, 4) }, Origin, 0);
    }

    [Fact]
    public void DiagonalNeighboursAreSeparateBodies()
    {
        // Four-connected, so a corner touch is two bodies rather than one.
        IReadOnlyList<IReadOnlyList<TileRect>> bodies = TileWaterPlanes.Components(Mask(8, 8, (1, 1, 1, 1), (2, 2, 1, 1)));
        Assert.Equal(2, bodies.Count);
        Assert.Equal(new TileRect(1, 1, 1, 1), Assert.Single(bodies[0]));
        Assert.Equal(new TileRect(2, 2, 1, 1), Assert.Single(bodies[1]));
    }

    [Fact]
    public void EveryDecompositionIsDisjointAndCoversItsMask()
    {
        // Two hundred seeded masks, which is the only cheap way to say the decomposition never overlaps for a
        // shape nobody thought to draw by hand. The seed is fixed so a failure is reproducible.
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
            foreach (IReadOnlyList<TileRect> body in TileWaterPlanes.Components(mask)) all.AddRange(body);
            TileWaterPlanes.RequireDisjoint(all, Origin, trial);

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
        // The merge rule itself, on a mask with no document behind it: same span extends, any other span opens a
        // new rectangle even when the rows are adjacent.
        Assert.Equal(new TileRect(2, 0, 3, 4), Assert.Single(TileWaterPlanes.Rectangles(Mask(8, 8, (2, 0, 3, 4)))));

        IReadOnlyList<TileRect> widened = TileWaterPlanes.Rectangles(Mask(8, 8, (2, 0, 3, 2), (2, 2, 4, 2)));
        Assert.Equal(2, widened.Count);
        Assert.Equal(new TileRect(2, 0, 3, 2), widened[0]);
        Assert.Equal(new TileRect(2, 2, 4, 2), widened[1]);
    }
}
