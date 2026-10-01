using System;
using System.Collections.Generic;
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
        // From the lowest corner under the tile to the highest corner plus the plane height, 3 m.
        AssertBox(blocked[0], 2.5f, -2.5f, 0.5f, 0.5f, bottom: 0.2f, top: 3.3f);
        AssertBox(blocked[1], 6.5f, -4.5f, 0.5f, 0.5f, bottom: 0.6f, top: 3.7f);

        TileCollider[] lower = Of(
            TileWorldColliders.Build(doc, Catalogs(), new TileColliderOptions { BlockedHeight = 1.5f }),
            TileColliderKind.Blocked);
        AssertBox(lower[0], 2.5f, -2.5f, 0.5f, 0.5f, bottom: 0.2f, top: 1.8f);
    }

    [Fact]
    public void ASteepBlockedTileStandsOverItsHighestCorner()
    {
        TileWorldDocument doc = CliffWorld();
        float blockedHeight = doc.PlaneHeight;

        TileCollider cliff = Assert.Single(Of(TileWorldColliders.Build(doc, Catalogs()), TileColliderKind.Blocked),
            c => MathF.Abs(c.Pose.Position.Z - TileWorldSpace.WorldZ(4.5f, 1f)) < Tolerance);

        // The tile falls from the plateau to 0 inside itself, more than the blocked height, so a box measured from
        // the lowest corner alone would top out below the plateau a body walks off.
        float highest = PlateauCm * 0.01f;
        Assert.True(highest > blockedHeight);
        BoxShape box = Assert.IsType<BoxShape>(cliff.Shape);
        float bottom = cliff.Pose.Position.Y - box.HalfExtents.Y, top = cliff.Pose.Position.Y + box.HalfExtents.Y;
        Assert.Equal(0f, bottom, Tolerance);
        Assert.True(top >= highest + blockedHeight - Tolerance, $"box top {top} under {highest + blockedHeight}");
        AssertBox(cliff, CliffX + 0.5f, -4.5f, 0.5f, 0.5f, bottom: 0f, top: highest + blockedHeight);
    }

    [Fact]
    public void AWallObjectGetsOneEdgeBoxAndAWallCornerTwo()
    {
        TileWorldDocument doc = WallsWorld();

        TileCollider[] walls = Of(TileWorldColliders.Build(doc, Catalogs()), TileColliderKind.Wall);

        // Four walls with one edge each on row 5, ahead of four corners with two each on row 10, though the corners
        // were placed first. Every top is the anchor height plus 2.5 m. Every bottom is the lowest corner on the edge
        // or the anchor height, whichever is lower.
        Assert.Equal(4 + 4 * 2, walls.Length);
        AssertBox(walls[0], 5f, -5.5f, 0.05f, 0.5f, bottom: 0.5f, top: 0.55f + 2.5f);
        // The corner at (8, 10) turned once is the north and east edges, in that order. The east edge's corners sit
        // above the anchor, so the anchor is its bottom.
        AssertBox(walls[6], 8.5f, -11f, 0.5f, 0.05f, bottom: 0.8f, top: 0.85f + 2.5f);
        AssertBox(walls[7], 9f, -10.5f, 0.05f, 0.5f, bottom: 0.85f, top: 0.85f + 2.5f);
    }

    [Fact]
    public void EveryWallEdgeIsTheEdgeTheBakerMarks()
    {
        TileWorldDocument doc = WallsWorld();
        TileWorldCatalogs catalogs = Catalogs();

        TileCollider[] walls = Of(TileWorldColliders.Build(doc, catalogs), TileColliderKind.Wall);
        TileCollisionMap map = TileCollisionBaker.Bake(doc, catalogs);

        // An edge as one key from either side: a line of constant tile x or z, and the row or column it spans.
        var baked = new SortedSet<(bool AlongZ, int Line, int Span)>();
        for (int z = 0; z < 20; z++)
            for (int x = 0; x < 20; x++)
            {
                TileCollisionFlags f = map.Get(x, z, 0);
                if ((f & TileCollisionFlags.WallW) != 0) baked.Add((true, x, z));
                if ((f & TileCollisionFlags.WallE) != 0) baked.Add((true, x + 1, z));
                if ((f & TileCollisionFlags.WallS) != 0) baked.Add((false, z, x));
                if ((f & TileCollisionFlags.WallN) != 0) baked.Add((false, z + 1, x));
            }
        var built = new List<(bool AlongZ, int Line, int Span)>();
        foreach (TileCollider wall in walls)
        {
            Vector3 half = Assert.IsType<BoxShape>(wall.Shape).HalfExtents;
            float tileX = TileWorldSpace.TileX(wall.Pose.Position.X, 1f), tileZ = TileWorldSpace.TileZ(wall.Pose.Position.Z, 1f);
            bool alongZ = half.X < half.Z;
            Assert.Equal(0.05f, alongZ ? half.X : half.Z, Tolerance);
            Assert.Equal(0.5f, alongZ ? half.Z : half.X, Tolerance);
            built.Add(alongZ
                ? (true, (int)MathF.Round(tileX), (int)MathF.Floor(tileZ))
                : (false, (int)MathF.Round(tileZ), (int)MathF.Floor(tileX)));
        }

        Assert.Equal(12, baked.Count);
        Assert.Equal(baked, built.OrderBy(e => e));
    }

    [Fact]
    public void NoObjectBoxInvertsOnSteepGround()
    {
        TileWorldDocument doc = SteepWorld();
        TileWorldCatalogs catalogs = Catalogs();

        TileWorldColliders colliders = TileWorldColliders.Build(doc, catalogs);

        // The wall's edge is at -30 m and its anchor, the tile centre, at -33 m. The 3x1 diagonal's anchor tile reaches
        // down to -36 m and its anchor, the footprint centre, stands at -39 m. Each box runs from the anchor to the
        // anchor plus its collision height.
        TileCollider wall = Assert.Single(Of(colliders, TileColliderKind.Wall));
        TileCollider diagonal = Assert.Single(Of(colliders, TileColliderKind.Object));
        AssertBox(wall, 5f, -5.5f, 0.05f, 0.5f, bottom: -33f, top: -33f + 2.5f);
        AssertBox(diagonal, 5.5f, -10.5f, 0.5f, 0.5f, bottom: -39f, top: -39f + 2f);
        foreach (TileObject o in doc.AllObjects())
        {
            TileObjectArchetype archetype = catalogs.Archetype(o.ArchetypeId)!;
            TileCollider box = o.ArchetypeId == "wall" ? wall : diagonal;
            Assert.True(Assert.IsType<BoxShape>(box.Shape).HalfExtents.Y > 0f, $"{o.ArchetypeId} is inverted");
            Assert.Equal(TileObjectPlacement.AnchorPosition(doc, archetype, o).Y + archetype.CollisionHeight!.Value,
                         box.Pose.Position.Y + ((BoxShape)box.Shape).HalfExtents.Y, Tolerance);
        }
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

        // And the box ends where the rect does: a point just past each edge is off the surface and off the box.
        foreach (Vector2 local in new[] { new Vector2(1.01f, 0f), new Vector2(-2.01f, 0f), new Vector2(-0.5f, 0.51f), new Vector2(-0.5f, -0.51f) })
        {
            Vector3 world = Vector3.Transform(new Vector3(local.X, 0f, local.Y), localToWorld);
            Assert.False(TileWalkSurfaces.TryHeightAt(doc, catalogs, world.X, world.Z, 0, out _));

            Vector3 inBox = Vector3.Transform(
                new Vector3(world.X, deck.Pose.Position.Y, world.Z) - deck.Pose.Position,
                Quaternion.Conjugate(deck.Pose.Orientation));
            Assert.True(MathF.Abs(inBox.X) > box.HalfExtents.X || MathF.Abs(inBox.Z) > box.HalfExtents.Z,
                        $"{local} is inside the box");
        }
    }

    [Theory]
    [InlineData(nameof(TileColliderOptions.WallThickness), 0f)]
    [InlineData(nameof(TileColliderOptions.WallThickness), float.NaN)]
    [InlineData(nameof(TileColliderOptions.WalkSurfaceThickness), -0.1f)]
    [InlineData(nameof(TileColliderOptions.WalkSurfaceThickness), float.PositiveInfinity)]
    [InlineData(nameof(TileColliderOptions.BlockedHeight), 0f)]
    [InlineData(nameof(TileColliderOptions.BlockedHeight), float.NaN)]
    public void AnOptionThatIsNotAFinitePositiveNumberIsRefusedByName(string option, float value)
    {
        TileColliderOptions options = option switch
        {
            nameof(TileColliderOptions.WallThickness) => new TileColliderOptions { WallThickness = value },
            nameof(TileColliderOptions.WalkSurfaceThickness) => new TileColliderOptions { WalkSurfaceThickness = value },
            _ => new TileColliderOptions { BlockedHeight = value },
        };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => TileWorldColliders.Build(FlatWorld(), Catalogs(), options));

        Assert.Equal("options", error.ParamName);
        Assert.Contains(option, error.Message, StringComparison.Ordinal);
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

    // The kind order, the shape bytes and the pose encoding are a contract: a deployed head compares this hash. The
    // literal is EveryKindWorld's digest, every collider on a quarter turn so it is exact on every architecture. A
    // deliberate change to the colliders or their encoding updates this literal and says so in the CHANGELOG.
    [Fact]
    public void TheHashOfEveryKindWorldIsPinned()
    {
        Assert.Equal("6aa23030d83786938861832e5f220a95614c896da011eb21c14ef31a5e651851",
            Convert.ToHexStringLower(TileWorldColliders.Build(EveryKindWorld(), Catalogs()).Hash));
    }

    [Fact]
    public void TheHashIgnoresTheOrderRegionsWereCreatedIn()
    {
        RegionCoord[] regions = { new(0, 0), new(1, 0), new(0, -1), new(-1, 1) };
        TileWorldDocument forward = MultiRegionWorld(regions);
        TileWorldDocument backward = MultiRegionWorld(regions.Reverse().ToArray());
        Assert.NotEqual(forward.Regions.Keys, backward.Regions.Keys);

        TileWorldColliders a = TileWorldColliders.Build(forward, Catalogs());
        TileWorldColliders b = TileWorldColliders.Build(backward, Catalogs());

        Assert.Equal(4, Of(a, TileColliderKind.Ground).Length);
        Assert.Equal(a.Colliders.Count, b.Colliders.Count);
        Assert.Equal(a.Hash, b.Hash);
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
