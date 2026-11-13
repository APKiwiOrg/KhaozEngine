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

    // A box far past the loaded world searches only the loaded corners for its ground, so it places at once instead of
    // overflowing the corner range or walking an unbounded loop.
    [Fact]
    public void A_box_far_larger_than_the_world_meets_the_lowest_loaded_corner()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(50 + x * 10));
        var box = new TileCollisionBox { MinX = -1e30f, MaxX = 1e30f, MaxY = 1f, MinZ = -1e30f, MaxZ = 1e30f };
        TileObjectArchetype bench = Bench(0f, box);

        TilePlacedCollisionBox placed = Place(doc, bench, Placed(0), box);

        Assert.Equal(doc.CornerHeight(0, 0, 0), placed.Bottom, Tolerance);
    }

    // A loaded box can still be edited in memory, and one that cannot be a volume places nothing rather than a shape
    // no physics backend can hold. The last case is valid where authored and only collapses once added to a standing
    // height too large for its thickness to survive in a float.
    [Theory]
    [InlineData("null")]
    [InlineData("inverted")]
    [InlineData("nan")]
    [InlineData("infinite yaw")]
    [InlineData("absorbed")]
    public void A_box_that_cannot_be_a_volume_places_nothing(string defect)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileCollisionBox? box = OffCentre();
        switch (defect)
        {
            case "null": box = null; break;
            case "inverted": box.MinX = 1f; break;
            case "nan": box.MaxY = float.NaN; break;
            case "infinite yaw": box.YawDegrees = float.PositiveInfinity; break;
            default:
                for (int z = 0; z < TileRegion.Size; z++)
                    for (int x = 0; x < TileRegion.Size; x++)
                        doc.SetCornerHeightCm(x, z, 0, 30000);
                box.MinY = 0f;
                box.MaxY = 1e-6f;
                break;
        }
        TileObjectArchetype bench = Bench(0f, OffCentre());
        Assert.True(TileCollisionBoxes.TryPlace(doc, TileLoadedCorners.Of(doc), bench, Placed(0), OffCentre(), out _));

        Assert.False(TileCollisionBoxes.TryPlace(doc, TileLoadedCorners.Of(doc), bench, Placed(0), box, out _));
    }
}
