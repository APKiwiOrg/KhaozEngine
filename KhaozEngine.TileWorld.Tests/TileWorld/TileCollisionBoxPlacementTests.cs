using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Where an authored collision box stands once its object is placed: it turns with the drawn mesh under every
/// quarter turn and a free yaw, its own yaw turns it about its centre, and its vertical span follows the standing height
/// with a grounded box meeting a slope. Physics builds its object colliders from this placement and the tile editor
/// draws and reports it, so the mesh's own local-to-world matrix is the oracle here. Issue
/// https://github.com/APKiwiOrg/KhaozEngine/issues/1782.</summary>
public class TileCollisionBoxPlacementTests
{
    const float Tolerance = 1e-4f;

    // Off centre on both axes, so a turn that dropped the centre or swapped the wrong half would show. Every bound is a
    // binary fraction, so the quarter-turn arithmetic is exact.
    static TileCollisionBox OffCentre(float yawDegrees = 0f) => new()
    {
        MinX = -0.75f, MaxX = 0.25f, MinY = 0.25f, MaxY = 0.5f, MinZ = -0.375f, MaxZ = 0.125f, YawDegrees = yawDegrees,
    };

    static TileObjectArchetype Bench(float yawOffset = 0f, params TileCollisionBox[] boxes) => new()
    {
        Id = "bench", Name = "bench", MeshRef = "kit/bench.glb", SizeX = 2, SizeZ = 1,
        CollisionKind = TileCollisionKind.Solid, YawOffsetDegrees = yawOffset, CollisionBoxes = new(boxes),
    };

    static TileObject Placed(int rotation) => new() { Id = 7, ArchetypeId = "bench", X = 4, Z = 6, Rotation = rotation };

    static TilePlacedCollisionBox Place(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o, TileCollisionBox box)
    {
        Assert.True(TileCollisionBoxes.TryPlace(doc, TileLoadedCorners.Of(doc), archetype, o, box, out TilePlacedCollisionBox placed));
        return placed;
    }

    // The four corners of a box in the mesh's own frame, turned about the box centre by its own yaw in the turn sense
    // quarter turns use, then through the mesh's local-to-world matrix: where the drawn model puts those corners.
    static IEnumerable<Vector3> MeshCorners(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o, TileCollisionBox box)
    {
        Matrix4x4 localToWorld = TileObjectPlacement.LocalToWorld(doc, archetype, o);
        Matrix4x4 ownYaw = Matrix4x4.CreateRotationY(-box.YawDegrees * (MathF.PI / 180f));
        var centre = new Vector3((box.MinX + box.MaxX) / 2f, 0f, (box.MinZ + box.MaxZ) / 2f);
        float halfX = (box.MaxX - box.MinX) / 2f, halfZ = (box.MaxZ - box.MinZ) / 2f;
        foreach ((float sx, float sz) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            yield return Vector3.Transform(centre + Vector3.Transform(new Vector3(sx * halfX, 0f, sz * halfZ), ownYaw), localToWorld);
    }

    // A corner of the placed box lies at plus or minus its half extents in the box's own frame.
    static void AssertOnACorner(TilePlacedCollisionBox placed, Vector3 world)
    {
        Vector3 inBox = Vector3.Transform(new Vector3(world.X - placed.CentreX, 0f, world.Z - placed.CentreZ),
            Quaternion.Conjugate(placed.Orientation));
        Assert.Equal(placed.HalfX, MathF.Abs(inBox.X), Tolerance);
        Assert.Equal(placed.HalfZ, MathF.Abs(inBox.Z), Tolerance);
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(1, 0f)]
    [InlineData(2, 0f)]
    [InlineData(3, 0f)]
    [InlineData(0, 90f)]
    [InlineData(1, -90f)]
    public void A_box_on_a_quarter_turn_is_axis_aligned_where_the_mesh_turns_its_corners(int rotation, float boxYaw)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileCollisionBox box = OffCentre(boxYaw);
        TileObjectArchetype bench = Bench(0f, box);
        TileObject o = Placed(rotation);

        TilePlacedCollisionBox placed = Place(doc, bench, o, box);

        Assert.Equal(Quaternion.Identity, placed.Orientation);
        Assert.Equal(0f, placed.YawDegrees);
        // An odd quarter turn in all swaps the box's halves onto the other world axes.
        bool swapped = ((rotation * 90 + (int)boxYaw) / 90 & 1) != 0;
        Assert.Equal(swapped ? (0.25f, 0.5f) : (0.5f, 0.25f), (placed.HalfX, placed.HalfZ));
        foreach (Vector3 corner in MeshCorners(doc, bench, o, box)) AssertOnACorner(placed, corner);
    }

    [Theory]
    [InlineData(30f, 0f)]
    [InlineData(0f, 45f)]
    [InlineData(30f, 15f)]
    public void A_box_off_a_quarter_turn_is_oriented_where_the_mesh_turns_its_corners(float yawOffset, float boxYaw)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileCollisionBox box = OffCentre(boxYaw);
        TileObjectArchetype bench = Bench(yawOffset, box);
        TileObject o = Placed(1);

        TilePlacedCollisionBox placed = Place(doc, bench, o, box);

        Assert.NotEqual(Quaternion.Identity, placed.Orientation);
        Assert.Equal(90f + yawOffset + boxYaw, placed.YawDegrees);
        Assert.Equal((0.5f, 0.25f), (placed.HalfX, placed.HalfZ));
        foreach (Vector3 corner in MeshCorners(doc, bench, o, box)) AssertOnACorner(placed, corner);
    }

    // On ground rising 10 cm a tile eastward the bench stands at 1.1 m, the height at its anchor x 11. A box resting
    // on (or sunk below) the base reaches down to the lowest corner under its OWN plan, not the footprint's, so the
    // overhang west to x 9.5 reaches the corner at x 9. A box raised off the base keeps its authored bottom. Every top
    // is the standing height plus maxY.
    [Theory]
    [InlineData(-1.5f, -0.75f, 0f, 0.9f)]
    [InlineData(0.25f, 0.75f, 0f, 1.1f)]
    [InlineData(-0.75f, 0.75f, 0.5f, 1.6f)]
    [InlineData(-0.75f, 0.75f, -0.25f, 0.85f)]
    public void A_box_spans_from_the_standing_height_and_a_grounded_one_meets_the_slope(float minX, float maxX,
        float minY, float bottom)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(x * 10));
        var box = new TileCollisionBox { MinX = minX, MaxX = maxX, MinY = minY, MaxY = 1f, MinZ = -0.25f, MaxZ = 0.25f };
        TileObjectArchetype bench = Bench(0f, box);
        var o = new TileObject { Id = 7, ArchetypeId = "bench", X = 10, Z = 10 };
        float standing = TileObjectPlacement.AnchorPosition(doc, bench, o).Y;
        Assert.Equal(1.1f, standing, Tolerance);

        TilePlacedCollisionBox placed = Place(doc, bench, o, box);

        Assert.Equal(bottom, placed.Bottom, Tolerance);
        Assert.Equal(standing + 1f, placed.Top, Tolerance);
    }

    // A grounded box reads only the loaded regions under it, never the empty space between them. Regions (0, 0) and
    // (10, 10) bound a corner range of 705 by 705, about half a million corners, of which the two regions own 4096
    // each and edge-read 65 more along each of their east and north seams. So the corners read are bounded by the
    // loaded terrain, and the far region's one low corner still wins over the base height every gap corner reads.
    // From the review of https://github.com/APKiwiOrg/KhaozEngine/issues/1782.
    [Fact]
    public void A_box_over_sparse_regions_reads_only_the_loaded_terrain()
    {
        var far = new RegionCoord(10, 10);
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), far);
        doc.SetCornerHeightCm(far.OriginX + 5, far.OriginZ + 7, 0, -150);
        var box = new TileCollisionBox { MinX = -1e30f, MaxX = 1e30f, MaxY = 1f, MinZ = -1e30f, MaxZ = 1e30f };
        int farEdgeX = far.OriginX + TileRegion.Size, farEdgeZ = far.OriginZ + TileRegion.Size;

        short lowest = TileCollisionBoxes.LowestCornerCm(doc, 0, 0, farEdgeX, 0, farEdgeZ, out long read);
        TilePlacedCollisionBox placed = Place(doc, Bench(0f, box), Placed(0), box);

        Assert.Equal((short)-150, lowest);
        long loadedTerrain = 2L * (TileRegion.Size * TileRegion.Size + 2 * (TileRegion.Size + 1));
        Assert.True(read <= loadedTerrain, $"read {read} corners, the loaded terrain is {loadedTerrain}");
        Assert.Equal(-1.5f, placed.Bottom, Tolerance);
    }

    // A ground search can span more corners than an int counts. Regions (-1, 0) and (33554431, 0), the last region
    // column of the corner domain, bound a range from x -64 to int.MaxValue, so its far edge is past int.MaxValue and
    // it is 2^31 + 64 corners wide. Every corner the two regions own or edge-read stands at 1 m and every corner between
    // them reads the base height, 0. So a grounded box over the range reaches down to 0 rather than resting on the
    // loaded terrain's 1 m above the missing ground, and the search still reads only the loaded terrain. From the
    // re-check of https://github.com/APKiwiOrg/KhaozEngine/issues/1782.
    [Fact]
    public void A_box_over_a_range_wider_than_int_still_counts_the_corners_between_its_regions()
    {
        RegionCoord near = new(-1, 0), last = new(33554431, 0);
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, near, last);
        SetOwnedCorners(doc, near, 100);
        SetOwnedCorners(doc, last, 100);
        var box = new TileCollisionBox { MinX = -1e30f, MaxX = 1e30f, MaxY = 1f, MinZ = -0.25f, MaxZ = 0.25f };
        var o = new TileObject { Id = 7, ArchetypeId = "bench", X = -2, Z = 5 };

        TilePlacedCollisionBox placed = Place(doc, Bench(0f, box), o, box);
        Assert.Equal(0f, placed.Bottom, Tolerance);

        TileCollisionBoxes.LowestCornerCm(doc, 0, near.OriginX, int.MaxValue, 5, 6, out long read);
        long loadedTerrain = 2L * (TileRegion.Size * TileRegion.Size + 2 * (TileRegion.Size + 1));
        Assert.True(read <= loadedTerrain, $"read {read} corners, the loaded terrain is {loadedTerrain}");
    }

    // Reading loaded terrain alone answers what the document's own corner read answers over every corner of the range:
    // an owned corner, a missing region's seam corner that edge-reads its loaded neighbour, and a missing region's inner
    // corner, which reads the base height. Each row's minimum comes from a different kind of corner (see SeamWorld and
    // DomainEdgeWorld).
    [Theory]
    [InlineData("seams", 10, 20, 10, 20)]
    [InlineData("seams", 64, 64, 10, 12)]
    [InlineData("seams", 64, 66, 10, 12)]
    [InlineData("seams", 60, 130, 5, 5)]
    [InlineData("seams", 64, 64, 64, 64)]
    [InlineData("seams", 10, 10, 128, 128)]
    [InlineData("seams", 140, 141, 70, 71)]
    [InlineData("domain edge", 2147483584, int.MaxValue, 5, 6)]
    [InlineData("domain edge", 64, 64, 2147483600, int.MaxValue)]
    [InlineData("domain edge", int.MinValue, int.MinValue, 5, 6)]
    [InlineData("domain edge", 10, 11, int.MinValue, int.MinValue)]
    [InlineData("domain edge", int.MinValue, -2147483578, 70, 70)]
    public void The_lowest_corner_reads_a_missing_corner_as_the_document_does(string world, int x0, int x1, int z0, int z1)
    {
        TileWorldDocument doc = world == "seams" ? SeamWorld() : DomainEdgeWorld();
        short expected = short.MaxValue;
        for (long z = z0; z <= z1; z++)
            for (long x = x0; x <= x1; x++)
                expected = Math.Min(expected, doc.CornerHeightCm((int)x, (int)z, 0));

        Assert.Equal(expected, TileCollisionBoxes.LowestCornerCm(doc, 0, x0, x1, z0, z1, out _));
    }

    // Regions (0, 0), (2, 0) and (0, 1) are loaded and (1, 0), (1, 1) and (2, 1) are missing. Every seam a loaded region
    // edge-reads into carries a height of its own: (0, 0) stands at 200 cm with its east column at 120, (0, 1) at 250
    // with its east column at 130 and its north row at 140, and (2, 0) at 300 with one corner, (130, 5), at -90. So
    // the rows above find, in order: owned corners alone (200), the seam east of (0, 0) alone (120), that seam with the
    // missing region's inner corners (the base, 0), the far region's low corner across the gap (-90), the corner north
    // east of (0, 0), which edge-reads (0, 1) to its west before anything south (130), the seam north of (0, 1) (140),
    // and inner corners of a missing region alone (the base, 0).
    static TileWorldDocument SeamWorld()
    {
        var south = new RegionCoord(0, 0);
        var east = new RegionCoord(2, 0);
        var north = new RegionCoord(0, 1);
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, south, east, north);
        SetOwnedCorners(doc, south, 200);
        SetOwnedCorners(doc, east, 300);
        SetOwnedCorners(doc, north, 250);
        for (int z = 0; z < TileRegion.Size; z++) doc.SetCornerHeightCm(TileRegion.Size - 1, z, 0, 120);
        for (int z = TileRegion.Size; z < 2 * TileRegion.Size; z++) doc.SetCornerHeightCm(TileRegion.Size - 1, z, 0, 130);
        for (int x = 0; x < TileRegion.Size; x++) doc.SetCornerHeightCm(x, 2 * TileRegion.Size - 1, 0, 140);
        doc.SetCornerHeightCm(130, 5, 0, -90);
        return doc;
    }

    // Regions (33554431, 0) and (0, 33554431) are the last region column and row of the int corner domain, so their far
    // edges are past int.MaxValue. The document's corner read takes a missing corner's west and south neighbours with
    // wrapping int arithmetic, so it reads those regions' east and north edges from the domain's first column and row.
    // (33554431, 0) stands at 100 cm with its east column, x int.MaxValue, at -70. (0, 33554431) stands at 200 with its
    // east column at -80 and the rest of its north row, z int.MaxValue, at -60. (-33554432, 1), the first region column,
    // stands at 100, and (33554432, 1), a region the document admits but whose origin wraps onto the same corners, is
    // never read for them. So the rows above find, in order: owned corners up to int.MaxValue (-70), the seam east of
    // (0, 33554431) in rows up to int.MaxValue (-80), the seams the document reads across the domain's edge, east of
    // (33554431, 0) on the first column (-70) and north of (0, 33554431) on the first row (-60), and past the first
    // column's own corners and seam the base height (0), which counting the wrapped region's corners as owned would hide.
    static TileWorldDocument DomainEdgeWorld()
    {
        var lastColumn = new RegionCoord(33554431, 0);
        var lastRow = new RegionCoord(0, 33554431);
        var firstColumn = new RegionCoord(-33554432, 1);
        var wrapped = new RegionCoord(33554432, 1);
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, lastColumn, lastRow, firstColumn, wrapped);
        SetOwnedCorners(doc, lastColumn, 100);
        SetOwnedCorners(doc, lastRow, 200);
        SetOwnedCorners(doc, firstColumn, 100);
        for (int dz = 0; dz < TileRegion.Size; dz++)
        {
            doc.SetCornerHeightCm(int.MaxValue, lastColumn.OriginZ + dz, 0, -70);
            doc.SetCornerHeightCm(TileRegion.Size - 1, lastRow.OriginZ + dz, 0, -80);
        }
        for (int x = 0; x < TileRegion.Size - 1; x++) doc.SetCornerHeightCm(x, int.MaxValue, 0, -60);
        return doc;
    }

    // Every corner a region owns, counted from its origin so the last region of the domain does not wrap.
    static void SetOwnedCorners(TileWorldDocument doc, RegionCoord region, short cm)
    {
        for (int dz = 0; dz < TileRegion.Size; dz++)
            for (int dx = 0; dx < TileRegion.Size; dx++)
                doc.SetCornerHeightCm(region.OriginX + dx, region.OriginZ + dz, 0, cm);
    }

    // Finite, ordered bounds whose float sum overflows still have a midpoint and a half extent a float holds, so the box
    // places there rather than at an infinite centre that a collider and the movement extent would refuse. From the
    // review of https://github.com/APKiwiOrg/KhaozEngine/issues/1782.
    [Fact]
    public void A_box_whose_bounds_sum_past_the_float_range_places_at_its_midpoint()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        var box = new TileCollisionBox { MinX = 2e38f, MaxX = 3e38f, MinY = 2e38f, MaxY = 3e38f, MinZ = -0.25f, MaxZ = 0.25f };
        Assert.True(float.IsPositiveInfinity(box.MinY + box.MaxY));

        TilePlacedCollisionBox placed = Place(doc, Bench(0f, box), Placed(0), box);

        AssertRelative(2.5e38f, placed.Centre.X);
        AssertRelative(5e37f, placed.HalfExtents.X);
        AssertRelative(2.5e38f, placed.Centre.Y);
        AssertRelative(5e37f, placed.HalfExtents.Y);
        AssertRelative(2e38f, placed.Bottom);
        AssertRelative(3e38f, placed.Top);

        static void AssertRelative(float expected, float actual) =>
            Assert.True(MathF.Abs(actual / expected - 1f) < 1e-6f, $"{actual} is not {expected}");
    }

    // A loaded box can still be edited in memory, and one that cannot be a volume places nothing rather than a shape
    // no physics backend can hold. The last three are valid where authored. One turns through a yaw offset and a box
    // yaw whose sum leaves the float range, so it has no orientation. One is too thin to survive being added to its
    // standing height: its top and bottom differ in double but round to the same float, the height the editor reports.
    // The last fits a float in every value, but turned 45 degrees its corners reach past the float range, so the bounds
    // physics declares for it are not finite. Its control is the same box unturned. From the re-check of
    // https://github.com/APKiwiOrg/KhaozEngine/issues/1782.
    [Theory]
    [InlineData("null")]
    [InlineData("inverted")]
    [InlineData("nan")]
    [InlineData("infinite yaw")]
    [InlineData("overflowing turn")]
    [InlineData("absorbed")]
    [InlineData("turned past the float range")]
    public void A_box_that_cannot_be_a_volume_places_nothing(string defect)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileCollisionBox control = OffCentre();
        TileCollisionBox? box = OffCentre();
        float yawOffset = 0f;
        switch (defect)
        {
            case "null": box = null; break;
            case "inverted": box.MinX = 1f; break;
            case "nan": box.MaxY = float.NaN; break;
            case "infinite yaw": box.YawDegrees = float.PositiveInfinity; break;
            case "overflowing turn":
                yawOffset = 3e38f;
                box.YawDegrees = 3e38f;
                break;
            case "absorbed":
                SetOwnedCorners(doc, new RegionCoord(0, 0), 30000);
                box.MinY = 0f;
                box.MaxY = 1e-6f;
                break;
            default:
                control = new TileCollisionBox { MinX = -3e38f, MaxX = 3e38f, MinY = 1f, MaxY = 2f, MinZ = -3e38f, MaxZ = 3e38f };
                box = new TileCollisionBox
                {
                    MinX = -3e38f, MaxX = 3e38f, MinY = 1f, MaxY = 2f, MinZ = -3e38f, MaxZ = 3e38f, YawDegrees = 45f,
                };
                break;
        }
        TileObjectArchetype bench = Bench(yawOffset, OffCentre());
        Assert.True(TileCollisionBoxes.TryPlace(doc, TileLoadedCorners.Of(doc), bench, Placed(0), control, out _));

        Assert.False(TileCollisionBoxes.TryPlace(doc, TileLoadedCorners.Of(doc), bench, Placed(0), box, out _));
    }
}
