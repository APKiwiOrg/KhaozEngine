using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>An archetype's authored collision boxes as physics colliders: one <see cref="TileColliderKind.Object"/>
/// box per authored box, in place of the box over the footprint, standing where the placement rule puts it, and in the
/// collider hash. Issue https://github.com/APKiwiOrg/KhaozEngine/issues/1782.</summary>
public class TileCollisionBoxColliderTests
{
    const float Tolerance = 1e-4f;

    static TileCollider[] Objects(TileWorldColliders colliders) =>
        colliders.Colliders.Where(c => c.Kind == TileColliderKind.Object).ToArray();

    static void AssertAxisAlignedBox(TileCollider collider, float centreX, float centreZ, float halfX, float halfZ,
        float bottom, float top)
    {
        BoxShape box = Assert.IsType<BoxShape>(collider.Shape);
        Assert.Equal(Quaternion.Identity, collider.Pose.Orientation);
        Assert.Equal(centreX, collider.Pose.Position.X, Tolerance);
        Assert.Equal(centreZ, collider.Pose.Position.Z, Tolerance);
        Assert.Equal(halfX, box.HalfExtents.X, Tolerance);
        Assert.Equal(halfZ, box.HalfExtents.Z, Tolerance);
        Assert.Equal(bottom, collider.Pose.Position.Y - box.HalfExtents.Y, Tolerance);
        Assert.Equal(top, collider.Pose.Position.Y + box.HalfExtents.Y, Tolerance);
    }

    // The boxed bench turned once on ground rising 10 cm a tile eastward covers tiles x 10 and z 10 to 11, so it stands
    // at its anchor (10.5, -11) on 1.05 m. A turn sends local x to world -z and local z to world -x, so the seat's
    // centre (0, 0.12) lands at x 10.38 and the back's (0, -0.24) at x 10.74, each with its halves swapped onto the
    // other axes. Both rest on the base, so each reaches down to the lowest corner under it, x 10 at 1.0 m. No
    // collision height is authored, and none is needed.
    [Fact]
    public void ABoxedSolidIsOneObjectBoxPerAuthoredBoxInPlaceOfItsFootprint()
    {
        TileWorldDocument doc = SlopedWorldWith("bench_boxed", 10, 10, rotation: 1);

        TileCollider[] boxes = Objects(TileWorldColliders.Build(doc, Catalogs()));

        Assert.Equal(2, boxes.Length);
        AssertAxisAlignedBox(boxes[0], 10.38f, -11f, 0.3f, 0.8f, bottom: 1.0f, top: 1.05f + SeatTop);
        AssertAxisAlignedBox(boxes[1], 10.74f, -11f, 0.06f, 0.8f, bottom: 1.0f, top: 1.05f + 0.94f);
    }

    // The diagonal's one box is a thin wall turned 45 degrees on its mesh origin, so it is that oriented box rather
    // than a box over the anchor tile. Its long axis runs south-east, the way a 45 degree clockwise turn from above
    // carries local east. The 2x1 stands at its anchor x 4 on 0.4 m and reaches down to the corner at x 3.
    [Fact]
    public void ADiagonalWithABoxIsThatTurnedBoxNotItsAnchorTile()
    {
        TileWorldDocument doc = SlopedWorldWith("diag_boxed", 3, 3);

        TileCollider wall = Assert.Single(Objects(TileWorldColliders.Build(doc, Catalogs())));

        BoxShape box = Assert.IsType<BoxShape>(wall.Shape);
        Assert.Equal(0.7f, box.HalfExtents.X, Tolerance);
        Assert.Equal(0.05f, box.HalfExtents.Z, Tolerance);
        Assert.Equal(4f, wall.Pose.Position.X, Tolerance);
        Assert.Equal(-3.5f, wall.Pose.Position.Z, Tolerance);
        Vector3 longAxis = Vector3.Transform(Vector3.UnitX, wall.Pose.Orientation);
        Assert.Equal(MathF.Sqrt(0.5f), longAxis.X, Tolerance);
        Assert.Equal(MathF.Sqrt(0.5f), longAxis.Z, Tolerance);
        Assert.Equal(0.3f, wall.Pose.Position.Y - box.HalfExtents.Y, Tolerance);
        Assert.Equal(0.4f + 2f, wall.Pose.Position.Y + box.HalfExtents.Y, Tolerance);
    }

    // Boxes at the float range build whenever a float holds every corner of them, and the rest are left out rather than
    // failing the build: the movement extent the build declares refuses bounds that are not finite. A finite box whose
    // bounds sum past the float range builds at its midpoint. A box 6e38 m wide builds unturned, but turned 45 degrees
    // its corners reach about 4.2e38 m, past the float range, so the same box is left out. From the review and re-check
    // of https://github.com/APKiwiOrg/KhaozEngine/issues/1782.
    [Fact]
    public void ABoxAtTheFloatRangeBuildsWhenAFloatHoldsItsCornersAndIsLeftOutWhenNot()
    {
        TileWorldCatalogs catalogs = TileWorldCatalogs.Merge(Catalogs(), TileWorldCatalogs.LoadJson("""
            { "archetypes": [
                { "id": "high_box", "name": "high_box", "meshRef": "test/high_box.glb", "collisionKind": "Solid",
                  "collisionBoxes": [ { "minX": -0.5, "maxX": 0.5, "minZ": -0.5, "maxZ": 0.5, "minY": 2e38, "maxY": 3e38 } ] },
                { "id": "wide_box", "name": "wide_box", "meshRef": "test/wide_box.glb", "collisionKind": "Solid",
                  "collisionBoxes": [ { "minX": -3e38, "maxX": 3e38, "minZ": -3e38, "maxZ": 3e38, "minY": 1, "maxY": 2 } ] },
                { "id": "turned_wide_box", "name": "turned_wide_box", "meshRef": "test/wide_box.glb", "collisionKind": "Solid",
                  "collisionBoxes": [ { "minX": -3e38, "maxX": 3e38, "minZ": -3e38, "maxZ": 3e38, "minY": 1, "maxY": 2,
                                        "yawDegrees": 45 } ] } ] }
            """, "float-range-boxes"));
        TileWorldDocument doc = FlatWorld();
        doc.AddObject("high_box", 5, 5, 0, 0);
        doc.AddObject("wide_box", 8, 5, 0, 0);
        doc.AddObject("turned_wide_box", 11, 5, 0, 0);

        TileCollider[] boxes = Objects(TileWorldColliders.Build(doc, catalogs));

        Assert.Equal(2, boxes.Length);
        TileCollider high = Assert.Single(boxes, b => b.Pose.Position.Y > 1e37f);
        TileCollider wide = Assert.Single(boxes, b => b.Shape is BoxShape { HalfExtents.X: > 1e37f });
        AssertRelative(2.5e38f, high.Pose.Position.Y);
        AssertRelative(5e37f, Assert.IsType<BoxShape>(high.Shape).HalfExtents.Y);
        AssertRelative(3e38f, Assert.IsType<BoxShape>(wide.Shape).HalfExtents.X);
        AssertRelative(3e38f, Assert.IsType<BoxShape>(wide.Shape).HalfExtents.Z);

        static void AssertRelative(float expected, float actual) =>
            Assert.True(MathF.Abs(actual / expected - 1f) < 1e-6f, $"{actual} is not {expected}");
    }

    // Issue #1785: the first row overflows the float width, the second only the turned corners. Both must leave out
    // just that surface. Unturned, even bounds whose float sum or difference overflows still fit after double sums.
    [Theory]
    [InlineData(-3e38f, 3e38f, 45f, false)]
    [InlineData(0f, 3e38f, 45f, false)]
    [InlineData(-3e38f, 3e38f, 0f, true)]
    [InlineData(2e38f, 3e38f, 0f, true)]
    public void AWalkSurfaceAtTheFloatRangeBuildsOnlyWhenAFloatHoldsItsPlacedBounds(
        float min, float max, float yaw, bool builds)
    {
        TileWorldCatalogs catalogs = TileWorldCatalogs.Merge(Catalogs(), TileWorldCatalogs.LoadJson(
            FormattableString.Invariant($$"""
            { "archetypes": [ { "id": "float_deck", "name": "float_deck", "meshRef": "test/deck.glb",
                "yawOffsetDegrees": {{yaw}}, "walkSurfaces": [
                    { "height": 1.2, "minX": {{min}}, "maxX": {{max}}, "minZ": {{min}}, "maxZ": {{max}} },
                    { "height": 2.5 } ] } ] }
            """), "float-range-surfaces"));
        TileWorldDocument doc = FlatWorld();
        doc.AddObject("float_deck", 5, 5, 0, 0);
        doc.AddObject("bench_boxed", 8, 5, 0, 0);

        TileWorldColliders actual = TileWorldColliders.Build(doc, catalogs, "float-range-surfaces");

        TileCollider[] surfaces = actual.Colliders.Where(c => c.Kind == TileColliderKind.WalkSurface).ToArray();
        Assert.Equal(builds ? 2 : 1, surfaces.Length);
        TileCollider control = Assert.Single(surfaces, c => c.Shape is BoxShape { HalfExtents.X: 0.5f, HalfExtents.Z: 0.5f });
        Assert.Equal(2.5f, control.Pose.Position.Y + Assert.IsType<BoxShape>(control.Shape).HalfExtents.Y, Tolerance);
        Assert.Equal(2, Objects(actual).Length);
        if (builds) return;

        // Leaving out a surface is identical to removing it from the catalog, including captured bounds and owners.
        catalogs.Archetype("float_deck")!.WalkSurfaces!.RemoveAt(0);
        TileWorldColliders expected = TileWorldColliders.Build(doc, catalogs, "float-range-surfaces");
        Assert.Equal(expected.Hash, actual.Hash);
        Assert.Equal(expected.MovementExtent, actual.MovementExtent);
        using var actualPhysics = new BepuPhysicsWorld(Vector3.Zero);
        using var expectedPhysics = new BepuPhysicsWorld(Vector3.Zero);
        using var actualInstalled = actual.AddTo(actualPhysics);
        using var expectedInstalled = expected.AddTo(expectedPhysics);
        Assert.Equal(expectedInstalled.CreateMovementRegistration("float-range-surfaces").SemanticDigest,
            actualInstalled.CreateMovementRegistration("float-range-surfaces").SemanticDigest);
    }

    // Consumers fold the collider hash into their navigation bake inputs, so a box edit has to move it and putting the
    // box back has to restore it.
    [Fact]
    public void TheHashMovesWhenACollisionBoxChanges()
    {
        TileWorldDocument doc = BoxedBenchWorld();
        TileWorldCatalogs catalogs = Catalogs();
        TileCollisionBox seat = catalogs.Archetype("bench_boxed")!.CollisionBoxes![0];
        byte[] before = TileWorldColliders.Build(doc, catalogs).Hash;

        seat.MaxY = 0.5f;
        byte[] taller = TileWorldColliders.Build(doc, catalogs).Hash;
        seat.MaxY = SeatTop;

        Assert.NotEqual(before, taller);
        Assert.Equal(before, TileWorldColliders.Build(doc, catalogs).Hash);
    }
}
