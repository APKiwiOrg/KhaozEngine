using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>How a tile world is described as colliders: one ground mesh per region wound for a one-sided front,
/// boxes for blocked tiles, walls, solids, diagonals and walk surfaces, none for roofs, in one canonical order with a
/// hash that only moves when the colliders do.</summary>
public class TileWorldCollidersTests
{
    const float Tolerance = 1e-4f;

    static TileCollider[] Of(TileWorldColliders colliders, TileColliderKind kind) =>
        colliders.Colliders.Where(c => c.Kind == kind).ToArray();

    static void AssertBox(TileCollider collider, Vector3 centre, Vector3 halfExtents)
    {
        BoxShape box = Assert.IsType<BoxShape>(collider.Shape);
        Assert.Equal(centre.X, collider.Pose.Position.X, Tolerance);
        Assert.Equal(centre.Y, collider.Pose.Position.Y, Tolerance);
        Assert.Equal(centre.Z, collider.Pose.Position.Z, Tolerance);
        Assert.Equal(halfExtents.X, box.HalfExtents.X, Tolerance);
        Assert.Equal(halfExtents.Y, box.HalfExtents.Y, Tolerance);
        Assert.Equal(halfExtents.Z, box.HalfExtents.Z, Tolerance);
        Assert.Equal(Quaternion.Identity, collider.Pose.Orientation);
    }

    // A box from its world bottom and top rather than its centre and half height, which is how the rules state it.
    static void AssertBox(TileCollider collider, float centreX, float centreZ, float halfX, float halfZ,
                          float bottom, float top) =>
        AssertBox(collider, new Vector3(centreX, (bottom + top) / 2f, centreZ),
                  new Vector3(halfX, (top - bottom) / 2f, halfZ));

    [Fact]
    public void AFlatWorldIsOneGroundMeshPerRegion()
    {
        TileWorldDocument doc = FlatWorld(new RegionCoord(1, 0), new RegionCoord(0, 0), new RegionCoord(0, -1));

        TileWorldColliders colliders = TileWorldColliders.Build(doc, Catalogs());

        Assert.Equal(3, colliders.Colliders.Count);
        Assert.All(colliders.Colliders, c => Assert.Equal(TileColliderKind.Ground, c.Kind));
        // Sorted by region z then x, not in the order the regions were created.
        Assert.Equal(new Vector3(0, 0, 64), colliders.Colliders[0].Pose.Position);
        Assert.Equal(new Vector3(0, 0, 0), colliders.Colliders[1].Pose.Position);
        Assert.Equal(new Vector3(64, 0, 0), colliders.Colliders[2].Pose.Position);
        const int triangles = TileRegion.TileCount * 2;
        foreach (TileCollider ground in colliders.Colliders)
        {
            TriangleMeshShape mesh = Assert.IsType<TriangleMeshShape>(ground.Shape);
            Assert.Equal(triangles * 3, mesh.Vertices.Length);
            Assert.Equal(triangles * 3, mesh.Indices.Length);
            Assert.All(mesh.Vertices, v => Assert.Equal(0f, v.Y));
            Assert.Equal(Quaternion.Identity, ground.Pose.Orientation);
        }
    }

    [Fact]
    public void ARegionPosePlusALocalVertexIsTheLatticePosition()
    {
        var region = new RegionCoord(1, -1);
        TileWorldDocument doc = RoughWorld(region);

        TileCollider ground = Assert.Single(TileWorldColliders.Build(doc, Catalogs()).Colliders);
        Vector3[] vertices = Assert.IsType<TriangleMeshShape>(ground.Shape).Vertices;

        // The mesh keeps Build's three fresh positions per triangle in A, B, C order, so walking the tiles the way
        // Build walks them meets each vertex in turn.
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        int v = 0;
        for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
            for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
            {
                Assert.True(TileGroundTriangles.TryDescribe(doc, x, z, 0, out TileGroundCell cell, triangles));
                for (int t = 0; t < cell.TriangleCount; t++)
                    foreach (TileLatticePoint point in new[] { triangles[t].A, triangles[t].B, triangles[t].C })
                    {
                        Vector3 expected = TileGroundTriangles.LatticePosition(doc, x, z, 0, point, 0, 0);
                        Vector3 actual = ground.Pose.Position + vertices[v++];
                        Assert.Equal(expected.X, actual.X, Tolerance);
                        Assert.Equal(expected.Y, actual.Y, Tolerance);
                        Assert.Equal(expected.Z, actual.Z, Tolerance);
                    }
            }
        Assert.Equal(vertices.Length, v);
    }

    [Fact]
    public void GroundTrianglesAreWoundForAOneSidedTopFace()
    {
        TileWorldDocument doc = RoughWorld();

        TileCollider ground = Assert.Single(TileWorldColliders.Build(doc, Catalogs()).Colliders);
        TriangleMeshShape mesh = Assert.IsType<TriangleMeshShape>(ground.Shape);
        TileGroundMesh unswapped = TileGroundTriangles.Build(doc, new RegionCoord(0, 0), 0);

        Assert.Equal(unswapped.Indices.Length, mesh.Indices.Length);
        for (int t = 0; t < mesh.Indices.Length; t += 3)
        {
            // Bepu's front normal in registered order is cross(C-A, B-A), and it must point up.
            Vector3 a = mesh.Vertices[mesh.Indices[t]];
            Vector3 b = mesh.Vertices[mesh.Indices[t + 1]];
            Vector3 c = mesh.Vertices[mesh.Indices[t + 2]];
            Assert.True(Vector3.Cross(c - a, b - a).Y > 0f, $"triangle {t / 3} faces down for Bepu");

            // The right-handed normal of the tile ground before the swap already points up, which is why it swaps.
            Vector3 ua = unswapped.Positions[unswapped.Indices[t]];
            Vector3 ub = unswapped.Positions[unswapped.Indices[t + 1]];
            Vector3 uc = unswapped.Positions[unswapped.Indices[t + 2]];
            Assert.True(Vector3.Cross(ub - ua, uc - ua).Y > 0f, $"tile triangle {t / 3} is not counter-clockwise");
        }
    }

    [Fact]
    public void AVoidOrBlockedTileGetsABlockedBox()
    {
        TileWorldDocument doc = VoidAndBlockedWorld();

        TileCollider[] blocked = Of(TileWorldColliders.Build(doc, Catalogs()), TileColliderKind.Blocked);

        Assert.Equal(2, blocked.Length);
        // From the lowest corner under the tile up the plane height, 3 m.
        AssertBox(blocked[0], 2.5f, -2.5f, 0.5f, 0.5f, bottom: 0.2f, top: 3.2f);
        AssertBox(blocked[1], 6.5f, -4.5f, 0.5f, 0.5f, bottom: 0.6f, top: 3.6f);

        TileCollider[] lower = Of(
            TileWorldColliders.Build(doc, Catalogs(), new TileColliderOptions { BlockedHeight = 1.5f }),
            TileColliderKind.Blocked);
        AssertBox(lower[0], 2.5f, -2.5f, 0.5f, 0.5f, bottom: 0.2f, top: 1.7f);
    }

    [Fact]
    public void AWallObjectGetsOneEdgeBoxAndAWallCornerTwo()
    {
        TileWorldDocument doc = WallsWorld();

        TileCollider[] walls = Of(TileWorldColliders.Build(doc, Catalogs()), TileColliderKind.Wall);

        Assert.Equal(3, walls.Length);
        // Tile x orders the wall at (5, 5) ahead of the corner at (8, 5), placed first. Every top is the anchor
        // height plus 2.5 m, every bottom the lowest corner on the edge.
        AssertBox(walls[0], 5f, -5.5f, 0.05f, 0.5f, bottom: 0.5f, top: 0.55f + 2.5f);
        // Rotation 1 is the north and east edges, in that order.
        AssertBox(walls[1], 8.5f, -6f, 0.5f, 0.05f, bottom: 0.8f, top: 0.85f + 2.5f);
        AssertBox(walls[2], 9f, -5.5f, 0.05f, 0.5f, bottom: 0.9f, top: 0.85f + 2.5f);
    }

    [Fact]
    public void ARotatedSolidObjectCoversItsRotatedFootprint()
    {
        // A 1x2 bench turned once covers tiles (10, 10) and (11, 10).
        TileWorldDocument doc = SlopedWorldWith("bench", 10, 10, rotation: 1);

        TileCollider bench = Assert.Single(Of(TileWorldColliders.Build(doc, Catalogs()), TileColliderKind.Object));

        // Lowest corner at x 10, anchor at the footprint centre x 11, 0.8 m above it.
        AssertBox(bench, 11f, -10.5f, 1f, 0.5f, bottom: 1.0f, top: 1.1f + 0.8f);
    }

    [Fact]
    public void ADiagonalObjectCoversOnlyItsAnchorTile()
    {
        // A 2x1 diagonal wall anchored at (3, 3) blocks tile (3, 3) alone.
        TileWorldDocument doc = SlopedWorldWith("diag_wall", 3, 3);

        TileCollider diagonal = Assert.Single(Of(TileWorldColliders.Build(doc, Catalogs()), TileColliderKind.Object));

        // Its top still stands on the anchor, the centre of the full footprint at x 4.
        AssertBox(diagonal, 3.5f, -3.5f, 0.5f, 0.5f, bottom: 0.3f, top: 0.4f + 2f);
    }

    [Fact]
    public void ARoofGetsNoCollider()
    {
        TileWorldDocument doc = SlopedWorldWith("roof", 12, 12);

        TileCollider only = Assert.Single(TileWorldColliders.Build(doc, Catalogs()).Colliders);

        Assert.Equal(TileColliderKind.Ground, only.Kind);
    }

    [Fact]
    public void AWalkSurfaceIsAThinBoxAtItsHeight()
    {
        TileWorldDocument doc = SlopedWorldWith("deck", 20, 20);

        TileCollider deck = Assert.Single(
            Of(TileWorldColliders.Build(doc, Catalogs()), TileColliderKind.WalkSurface));

        // Anchored at (21.5, -20.5) on ground 2.15 m up, so the top is 3.35 m. The local rect runs x -2 to 1, so its
        // centre is half a metre west of the anchor. The box hangs 0.1 m under the top.
        AssertBox(deck, 21f, -20.5f, 1.5f, 0.5f, bottom: 3.35f - 0.1f, top: 3.35f);
    }

    [Theory]
    [InlineData("deck", 0)]
    [InlineData("deck", 1)]
    [InlineData("deck", 2)]
    [InlineData("deck", 3)]
    [InlineData("deck_skewed", 0)]
    [InlineData("deck_skewed", 1)]
    public void AWalkSurfaceBoxAgreesWithTheWalkSurfaceQuery(string archetypeId, int rotation)
    {
        TileWorldDocument doc = SlopedWorldWith(archetypeId, 20, 20, rotation);
        TileWorldCatalogs catalogs = Catalogs();
        TileObjectArchetype archetype = catalogs.Archetype(archetypeId)!;
        TileObject placed = Assert.Single(doc.AllObjects());
        Matrix4x4 localToWorld = TileObjectPlacement.LocalToWorld(doc, archetype, placed);

        TileCollider deck = Assert.Single(
            Of(TileWorldColliders.Build(doc, catalogs), TileColliderKind.WalkSurface));
        BoxShape box = Assert.IsType<BoxShape>(deck.Shape);
        bool quarterTurn = archetype.YawOffsetDegrees == 0f;
        Assert.Equal(quarterTurn, deck.Pose.Orientation == Quaternion.Identity);

        // The rect centre and a point just inside two opposite corners, in the mesh's own metres.
        foreach (Vector2 local in new[] { new Vector2(-0.5f, 0f), new Vector2(0.99f, 0.49f), new Vector2(-1.99f, -0.49f) })
        {
            Vector3 world = Vector3.Transform(new Vector3(local.X, 0f, local.Y), localToWorld);
            Assert.True(TileWalkSurfaces.TryHeightAt(doc, catalogs, world.X, world.Z, 0, out float expected));

            Vector3 inBox = Vector3.Transform(
                new Vector3(world.X, deck.Pose.Position.Y, world.Z) - deck.Pose.Position,
                Quaternion.Conjugate(deck.Pose.Orientation));
            Assert.True(MathF.Abs(inBox.X) <= box.HalfExtents.X + Tolerance, $"{local} is outside the box on x");
            Assert.True(MathF.Abs(inBox.Z) <= box.HalfExtents.Z + Tolerance, $"{local} is outside the box on z");
            Assert.Equal(expected, deck.Pose.Position.Y + box.HalfExtents.Y, Tolerance);
        }
    }

    [Fact]
    public void AMissingCollisionHeightThrowsNamingTheArchetype()
    {
        TileWorldDocument doc = SlopedWorldWith("unmeasured", 1, 1);

        TileWorldException error = Assert.Throws<TileWorldException>(() => TileWorldColliders.Build(doc, Catalogs()));

        Assert.Contains("'unmeasured'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHashIsStableAcrossRebuildsAndReloads()
    {
        TileWorldDocument doc = EveryKindWorld();
        byte[] first = TileWorldColliders.Build(doc, Catalogs()).Hash;

        Assert.Equal(32, first.Length);
        Assert.Equal(first, TileWorldColliders.Build(doc, Catalogs()).Hash);

        string directory = Path.Combine(Path.GetTempPath(), "ke-tileworld-physics-tests", Guid.NewGuid().ToString("N"));
        try
        {
            TileWorldFile.Save(doc, directory, force: true);
            TileWorldDocument reloaded = TileWorldFile.Load(directory);
            Assert.Equal(first, TileWorldColliders.Build(reloaded, Catalogs()).Hash);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheHashMovesWhenAHeightChanges()
    {
        TileWorldDocument doc = EveryKindWorld();
        TileWorldCatalogs catalogs = Catalogs();
        byte[] before = TileWorldColliders.Build(doc, catalogs).Hash;

        catalogs.Archetype("bench")!.CollisionHeight = 0.9f;
        byte[] taller = TileWorldColliders.Build(doc, catalogs).Hash;
        Assert.NotEqual(before, taller);

        doc.SetCornerHeightCm(40, 40, 0, 1);
        Assert.NotEqual(taller, TileWorldColliders.Build(doc, catalogs).Hash);
    }
}
