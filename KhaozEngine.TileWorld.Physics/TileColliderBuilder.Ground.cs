using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

// The colliders read from the tile layers alone: one ground mesh per region and one box per blocked tile. Also the
// two rules every box shares, which corner it stands on and how a world-space span becomes a box.
static partial class TileColliderBuilder
{
    // The one plane this round describes.
    const int Plane = 0;

    internal static void AddGround(TileWorldDocument document, RegionCoord[] regions, List<TileCollider> into)
    {
        foreach (RegionCoord region in regions)
        {
            TileGroundMesh mesh = TileGroundTriangles.Build(document, region, Plane);
            if (mesh.Indices.Length == 0) continue;

            // A backend mesh is one-sided and its front is cross(C-A, B-A), the side a clockwise triangle faces.
            // The tile ground is counter-clockwise seen from above, so swapping B and C turns that front up, the
            // same swap the terrain's collision mesh makes.
            int[] source = mesh.Indices;
            var indices = new int[source.Length];
            for (int t = 0; t + 2 < source.Length; t += 3)
            {
                indices[t] = source[t];
                indices[t + 1] = source[t + 2];
                indices[t + 2] = source[t + 1];
            }
            Vector3 origin = TileWorldSpace.ToWorld(region.OriginX, 0f, region.OriginZ, document.TileSize);
            into.Add(new TileCollider(TileColliderKind.Ground, new TriangleMeshShape(mesh.Positions, indices),
                                      Pose.At(origin)));
        }
    }

    internal static void AddBlocked(TileWorldDocument document, RegionCoord[] regions, float height,
                                    List<TileCollider> into)
    {
        float tileSize = document.TileSize;
        foreach (RegionCoord region in regions)
            for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
                for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                {
                    // The collision baker's ground rule.
                    if (document.GetUnderlay(x, z, Plane) != 0
                        && (document.GetSettings(x, z, Plane) & TileSettings.Blocked) == 0) continue;
                    // The tile map refuses the whole tile, so the box stands above every point of its ground.
                    float bottom = LowestCorner(document, x, z, x + 1, z + 1);
                    float top = HighestCorner(document, x, z, x + 1, z + 1) + height;
                    into.Add(Box(TileColliderKind.Blocked,
                        TileWorldSpace.WorldX(x + 0.5f, tileSize), TileWorldSpace.WorldZ(z + 0.5f, tileSize),
                        tileSize * 0.5f, tileSize * 0.5f, bottom, top));
                }
    }

    // The lowest lattice corner from (x0, z0) to (x1, z1) inclusive, in metres.
    static float LowestCorner(TileWorldDocument document, int x0, int z0, int x1, int z1)
    {
        short lowest = short.MaxValue;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                lowest = Math.Min(lowest, document.CornerHeightCm(x, z, Plane));
        return lowest * 0.01f;
    }

    // The highest lattice corner from (x0, z0) to (x1, z1) inclusive, in metres.
    static float HighestCorner(TileWorldDocument document, int x0, int z0, int x1, int z1)
    {
        short highest = short.MinValue;
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                highest = Math.Max(highest, document.CornerHeightCm(x, z, Plane));
        return highest * 0.01f;
    }

    // An axis-aligned box centred on (centreX, centreZ) in world metres, from bottom to top.
    static TileCollider Box(TileColliderKind kind, float centreX, float centreZ, float halfX, float halfZ,
                            float bottom, float top) =>
        new(kind, new BoxShape(new Vector3(halfX, (top - bottom) * 0.5f, halfZ)),
            Pose.At(new Vector3(centreX, (bottom + top) * 0.5f, centreZ)));
}
