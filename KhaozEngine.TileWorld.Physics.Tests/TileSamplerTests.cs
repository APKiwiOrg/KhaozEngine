using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsTestData;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>The floor and water samplers a tile world hands the movement step: a floor that is exactly the drawn
/// ground mesh, defined everywhere, and a water medium that only wets feet under a river's surface.</summary>
public class TileSamplerTests
{
    // The agreement the ground sampler owes the registered ground mesh.
    const float Millimetre = 1e-3f;
    const float Tolerance = 1e-5f;

    static TileWorldColliders Build(TileWorldDocument doc) => TileWorldColliders.Build(doc, Catalogs());

    // The registered ground meshes in world metres, bucketed by the tile each triangle's centroid falls in, so a
    // point is checked against its own tile's triangles straight from the colliders rather than through the rule
    // the sampler uses.
    static Dictionary<(int X, int Z), List<(Vector3 A, Vector3 B, Vector3 C)>> GroundTrianglesByTile(
        TileWorldColliders colliders, float tileSize)
    {
        var byTile = new Dictionary<(int X, int Z), List<(Vector3 A, Vector3 B, Vector3 C)>>();
        foreach (TileCollider ground in colliders.Colliders.Where(c => c.Kind == TileColliderKind.Ground))
        {
            TriangleMeshShape mesh = Assert.IsType<TriangleMeshShape>(ground.Shape);
            Vector3 origin = ground.Pose.Position;
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                Vector3 a = mesh.Vertices[mesh.Indices[i]] + origin;
                Vector3 b = mesh.Vertices[mesh.Indices[i + 1]] + origin;
                Vector3 c = mesh.Vertices[mesh.Indices[i + 2]] + origin;
                Vector3 centroid = (a + b + c) / 3f;
                var tile = ((int)MathF.Floor(TileWorldSpace.TileX(centroid.X, tileSize)),
                            (int)MathF.Floor(TileWorldSpace.TileZ(centroid.Z, tileSize)));
                if (!byTile.TryGetValue(tile, out var list)) byTile[tile] = list = new();
                list.Add((a, b, c));
            }
        }
        return byTile;
    }

    // Barycentric weights of (x, z) on a triangle's plan.
    static Vector3 Weights(float x, float z, Vector3 a, Vector3 b, Vector3 c)
    {
        float d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
        float wb = ((x - a.X) * (c.Z - a.Z) - (c.X - a.X) * (z - a.Z)) / d;
        float wc = ((b.X - a.X) * (z - a.Z) - (x - a.X) * (b.Z - a.Z)) / d;
        return new Vector3(1f - wb - wc, wb, wc);
    }

    static void AssertUnitUp(Vector3 normal)
    {
        Assert.True(float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z));
        Assert.Equal(1f, normal.Length(), Tolerance);
        Assert.True(normal.Y > 0f, $"normal {normal} does not point up");
    }

    [Fact]
    public void HeightMatchesTheSharedTrianglesOnAGrid()
    {
        // Every cut at every rotation on a bumpy slope, in two regions on either side of the origin.
        var regions = new[] { new RegionCoord(0, 0), new RegionCoord(-1, -1) };
        TileWorldDocument doc = RoughWorld(regions);
        TileWorldColliders colliders = Build(doc);
        var byTile = GroundTrianglesByTile(colliders, doc.TileSize);

        int checkedPoints = 0;
        foreach (RegionCoord region in regions)
            // A quarter-tile grid lands on every tile edge, diagonal and mid-edge point, the offset grid inside them.
            foreach (float offset in new[] { 0f, 0.1f })
                for (float lz = offset; lz <= 6f; lz += 0.25f)
                    for (float lx = offset; lx <= 18f; lx += 0.25f)
                    {
                        float worldX = TileWorldSpace.WorldX(region.OriginX + lx, doc.TileSize);
                        float worldZ = TileWorldSpace.WorldZ(region.OriginZ + lz, doc.TileSize);
                        var tile = ((int)MathF.Floor(region.OriginX + lx), (int)MathF.Floor(region.OriginZ + lz));

                        // The triangle that holds the point best, which on a shared edge is either of its two.
                        (Vector3 A, Vector3 B, Vector3 C) best = default;
                        Vector3 weights = default;
                        float bestMin = float.NegativeInfinity;
                        foreach (var triangle in byTile[tile])
                        {
                            Vector3 w = Weights(worldX, worldZ, triangle.A, triangle.B, triangle.C);
                            float min = MathF.Min(w.X, MathF.Min(w.Y, w.Z));
                            if (min > bestMin) (bestMin, best, weights) = (min, triangle, w);
                        }
                        Assert.True(bestMin > -1e-4f, $"no ground triangle holds ({worldX}, {worldZ})");

                        float expected = weights.X * best.A.Y + weights.Y * best.B.Y + weights.Z * best.C.Y;
                        Assert.Equal(expected, colliders.Ground.HeightAt(worldX, worldZ), Millimetre);

                        // Strictly inside a triangle, the normal is that triangle's.
                        if (bestMin > 1e-3f)
                        {
                            Vector3 expectedNormal = Vector3.Normalize(Vector3.Cross(best.B - best.A, best.C - best.A));
                            if (expectedNormal.Y < 0f) expectedNormal = -expectedNormal;
                            Vector3 normal = colliders.Ground.NormalAt(worldX, worldZ);
                            Assert.Equal(expectedNormal.X, normal.X, Tolerance);
                            Assert.Equal(expectedNormal.Y, normal.Y, Tolerance);
                            Assert.Equal(expectedNormal.Z, normal.Z, Tolerance);
                        }
                        checkedPoints++;
                    }
        Assert.True(checkedPoints > 4000);
    }

    [Fact]
    public void HeightIsNotTheBilinearHeightWhereTheyDiffer()
    {
        // One corner raised a metre: tile (5, 5) splits along its level NW to SE diagonal, so its centre is on the
        // ground at 0, where the bilinear lattice height is a quarter of the raised corner.
        TileWorldDocument doc = FlatWorld();
        doc.SetCornerHeightCm(5, 5, 0, 100);
        TileWorldColliders colliders = Build(doc);
        float worldX = TileWorldSpace.WorldX(5.5f, doc.TileSize), worldZ = TileWorldSpace.WorldZ(5.5f, doc.TileSize);

        Assert.Equal(0f, colliders.Ground.HeightAt(worldX, worldZ), Tolerance);
        Assert.Equal(0.25f, doc.HeightAt(worldX, worldZ, 0), Tolerance);

        // A quarter of the way in from the raised corner, on the triangle that holds it.
        worldX = TileWorldSpace.WorldX(5.25f, doc.TileSize);
        worldZ = TileWorldSpace.WorldZ(5.25f, doc.TileSize);
        Assert.Equal(0.5f, colliders.Ground.HeightAt(worldX, worldZ), Tolerance);
        Assert.Equal(0.5625f, doc.HeightAt(worldX, worldZ, 0), Tolerance);
    }

    [Fact]
    public void NormalPointsUpOnFlatGround()
    {
        TileWorldColliders colliders = Build(FlatWorld());

        foreach ((float x, float z) in new[] { (0.5f, -0.5f), (10.25f, -33.75f), (63.9f, -63.9f), (32f, -32f) })
            Assert.Equal(Vector3.UnitY, colliders.Ground.NormalAt(x, z));
    }

    [Fact]
    public void NormalLeansDownhillInWorldSpace()
    {
        // Rising 10 cm a tile east and 20 cm a tile north, which is world -z, so the normal leans west and south.
        TileWorldDocument doc = FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(x * 10 + z * 20));
        TileWorldColliders colliders = Build(doc);
        Vector3 expected = Vector3.Normalize(new Vector3(-0.1f, 1f, 0.2f));

        foreach ((float x, float z) in new[] { (5.2f, -5.7f), (5.7f, -5.2f), (20.5f, -40.5f) })
        {
            Vector3 normal = colliders.Ground.NormalAt(x, z);
            Assert.Equal(expected.X, normal.X, Tolerance);
            Assert.Equal(expected.Y, normal.Y, Tolerance);
            Assert.Equal(expected.Z, normal.Z, Tolerance);
        }
    }

    [Fact]
    public void AnOffWorldPointTakesTheHeightJustInsideTheWorld()
    {
        TileWorldDocument doc = RoughWorld();
        TileWorldColliders colliders = Build(doc);
        // The region spans world x 0 to 64 and world z 0 to -64.
        const float inside = 64f - 1e-4f;

        foreach ((float x, float z) in new[]
                 {
                     (-10f, -5f), (80f, -5f), (5f, 10f), (5f, -80f), (-10f, 10f), (80f, -80f), (-1e30f, 1e30f),
                     (float.PositiveInfinity, float.NegativeInfinity),
                 })
        {
            float clampedX = Math.Clamp(x, 0f, inside), clampedZ = Math.Clamp(z, -inside, 0f);
            float height = colliders.Ground.HeightAt(x, z);

            Assert.True(float.IsFinite(height), $"height at ({x}, {z}) is {height}");
            Assert.Equal(colliders.Ground.HeightAt(clampedX, clampedZ), height, Millimetre);
            AssertUnitUp(colliders.Ground.NormalAt(x, z));
        }
    }

    [Fact]
    public void AnUndrawableTileAnswersTheLatticeHeightWithAnUpNormal()
    {
        // A void tile (2, 2) and a NoDraw tile (9, 9) on a slope, and a missing region inside the loaded rectangle.
        TileWorldDocument holes = VoidAndBlockedWorld();
        holes.SetSettings(9, 9, 0, TileSettings.NoDraw);
        TileWorldDocument gappy = MultiRegionWorld(new RegionCoord(0, 0), new RegionCoord(1, 0),
                                                   new RegionCoord(0, -1), new RegionCoord(-1, 1));
        Assert.False(gappy.Regions.ContainsKey(new RegionCoord(1, 1)));

        foreach ((TileWorldDocument doc, float tileX, float tileZ) in new[]
                 { (holes, 2.3f, 2.6f), (holes, 9.5f, 9.5f), (gappy, 90.5f, 64.25f) })
        {
            TileWorldColliders colliders = Build(doc);
            float worldX = TileWorldSpace.WorldX(tileX, doc.TileSize), worldZ = TileWorldSpace.WorldZ(tileZ, doc.TileSize);
            float height = colliders.Ground.HeightAt(worldX, worldZ);

            Assert.True(float.IsFinite(height));
            Assert.Equal(doc.HeightAt(worldX, worldZ, 0), height, Tolerance);
            // Up, not the slope's normal, which says the lattice answered rather than a triangle.
            Assert.Equal(Vector3.UnitY, colliders.Ground.NormalAt(worldX, worldZ));
        }
    }

    [Fact]
    public void ANaNPointOrAnEmptyWorldAnswersLevelGround()
    {
        TileWorldColliders empty = TileWorldColliders.Build(new TileWorldDocument(), Catalogs());
        TileWorldColliders loaded = Build(RoughWorld());

        foreach ((TileWorldColliders colliders, float x, float z) in new[]
                 {
                     (empty, float.PositiveInfinity, float.NegativeInfinity), (empty, 5f, -5f),
                     (loaded, float.NaN, -5f), (loaded, 5f, float.NaN),
                 })
        {
            Assert.Equal(0f, colliders.Ground.HeightAt(x, z));
            Assert.Equal(Vector3.UnitY, colliders.Ground.NormalAt(x, z));
        }
    }

    [Fact]
    public void TheDelegatesAnswerAsTheMethodsDo()
    {
        TileWorldColliders colliders = Build(RoughWorld());

        Assert.Same(colliders.Ground.HeightDelegate, colliders.Ground.HeightDelegate);
        Assert.Same(colliders.Ground.NormalDelegate, colliders.Ground.NormalDelegate);
        Assert.Same(colliders.Medium.MediumDelegate, colliders.Medium.MediumDelegate);
        Assert.Equal(colliders.Ground.HeightAt(3.3f, -1.7f), colliders.Ground.HeightDelegate(3.3f, -1.7f));
        Assert.Equal(colliders.Ground.NormalAt(3.3f, -1.7f), colliders.Ground.NormalDelegate(3.3f, -1.7f));
        Assert.Equal(colliders.Medium.MediumAt(3.3f, -1.7f, -5f), colliders.Medium.MediumDelegate(3.3f, -1.7f, -5f));
    }

    [Fact]
    public void AFeetPointInARiverIsInWaterAtTheRimLessTwoCentimetres()
    {
        TileWorldColliders colliders = Build(BridgedRiverWorld());
        const float surface = 0f - TileWaterBodies.SurfaceDropMetres;

        // Each row of the river, on the bed and just under the surface.
        for (int row = RiverFirstRow; row <= RiverLastRow; row++)
            foreach (float feetY in new[] { -1f, surface - 0.001f })
            {
                MovementMedium medium = colliders.Medium.MediumAt(5.5f, TileWorldSpace.WorldZ(row + 0.5f, 1f), feetY);

                Assert.True(medium.InWater);
                Assert.Equal(surface, medium.WaterSurfaceY, Tolerance);
            }
    }

    [Fact]
    public void APointBesideTheRiverIsDry()
    {
        TileWorldColliders colliders = Build(BridgedRiverWorld());

        // On the banks either side, even with feet far below the surface.
        foreach (float tileZ in new[] { RiverFirstRow - 0.01f, RiverLastRow + 1f, 30.5f })
            Assert.Equal(MovementMedium.Dry, colliders.Medium.MediumAt(5.5f, TileWorldSpace.WorldZ(tileZ, 1f), -5f));
        // Past the region's edge, where no body was collected.
        Assert.Equal(MovementMedium.Dry, colliders.Medium.MediumAt(-0.5f, TileWorldSpace.WorldZ(11.5f, 1f), -5f));
    }

    [Fact]
    public void AFeetPointOnADeckAboveTheRiverIsDry()
    {
        TileWorldColliders colliders = Build(BridgedRiverWorld());
        TileCollider deck = Assert.Single(colliders.Colliders, c => c.Kind == TileColliderKind.WalkSurface);
        Vector3 half = Assert.IsType<BoxShape>(deck.Shape).HalfExtents;
        float top = deck.Pose.Position.Y + half.Y;
        // The middle of the river's middle row, under the deck.
        float x = TileWorldSpace.WorldX(DeckX + 0.5f, 1f), z = TileWorldSpace.WorldZ(RiverFirstRow + 1.5f, 1f);
        Assert.True(MathF.Abs(x - deck.Pose.Position.X) < half.X && MathF.Abs(z - deck.Pose.Position.Z) < half.Z,
                    $"({x}, {z}) is not under the deck at {deck.Pose.Position}");
        Assert.True(colliders.Medium.MediumAt(x, z, -1f).InWater);
        Assert.True(top > 0f);

        Assert.Equal(MovementMedium.Dry, colliders.Medium.MediumAt(x, z, top));
        // In water only below the surface, not at it.
        Assert.Equal(MovementMedium.Dry, colliders.Medium.MediumAt(x, z, -TileWaterBodies.SurfaceDropMetres));
    }

    [Fact]
    public void AnInWaterMediumHasAPositiveWadeScale()
    {
        TileWorldColliders colliders = Build(BridgedRiverWorld());

        MovementMedium medium = colliders.Medium.MediumAt(5.5f, TileWorldSpace.WorldZ(11.5f, 1f), -0.5f);

        Assert.True(medium.InWater);
        Assert.True(medium.WadeSpeedScale > 0f);
    }

    [Fact]
    public void SamplingAllocatesNothing()
    {
        TileWorldDocument doc = BridgedRiverWorld();
        doc.SetUnderlay(40, 40, 0, 0);
        TileWorldColliders colliders = Build(doc);
        // Drawn ground, the river, a void tile and off the world.
        (float X, float Z)[] points = { (3.3f, -1.7f), (5.5f, -11.5f), (40.5f, -40.5f), (-9f, 99f) };
        Func<float, float, float> height = colliders.Ground.HeightDelegate;
        Func<float, float, Vector3> normal = colliders.Ground.NormalDelegate;
        Func<float, float, float, MovementMedium> medium = colliders.Medium.MediumDelegate;
        float sink = 0f;
        for (int warm = 0; warm < 2; warm++)
            foreach ((float x, float z) in points)
                sink += height(x, z) + normal(x, z).Y + medium(x, z, -0.5f).WaterSurfaceY;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            foreach ((float x, float z) in points)
                sink += height(x, z) + normal(x, z).Y + medium(x, z, -0.5f).WaterSurfaceY;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(float.IsFinite(sink));
        Assert.Equal(0, allocated);
    }
}
