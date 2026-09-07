using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    /// <summary>The nearest visible ground or water surface on one plane, or null when the ray misses.
    /// <para>The ground candidate is the authored terrain from <see cref="TileRaycast"/>, limited to drawable
    /// tiles in regions this view has loaded. Water candidates are the exact cached <see cref="WaterPlane"/>
    /// rectangles this view draws for those regions. Like the draw path, picking reuses that cache until the
    /// affected mesh is rebuilt or its water look changes, matching the surface the next draw will submit.</para>
    /// <para><paramref name="direction"/> need not be normalised. The reported distance and
    /// <paramref name="maxDistance"/> are in world metres. Authored terrain wins an exact distance tie, because
    /// it depth-occludes a water plane at the same point.</para></summary>
    /// <param name="plane">The document plane to pick.</param>
    /// <param name="origin">Ray origin in world metres.</param>
    /// <param name="direction">Ray direction, not necessarily normalised.</param>
    /// <param name="maxDistance">Farthest accepted hit in world metres, inclusive.</param>
    public TileHit? PickSurface(int plane, Vector3 origin, Vector3 direction, float maxDistance = 2000f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        float lengthSquared = direction.LengthSquared();
        if (plane < 0 || plane >= _planes || !(maxDistance >= 0f)
            || !(lengthSquared >= 1e-12f) || !float.IsFinite(lengthSquared)
            || !IsFinite(origin))
            return null;

        Vector3 ray = Vector3.Normalize(direction);
        TileHit? terrain = TileRaycast.Pick(_doc, plane, origin, ray, maxDistance, _terrainPickFilter);
        TileHit? water = null;
        foreach (KeyValuePair<RegionCoord, RegionHandles> entry in _loaded)
        {
            IReadOnlyList<WaterPlane> planes = WaterOf(entry.Key, plane, entry.Value.Meshes[plane]);
            for (int i = 0; i < planes.Count; i++)
            {
                WaterPlane candidatePlane = planes[i];
                if (!IntersectWaterPlane(candidatePlane, origin, ray, maxDistance, out float distance)) continue;

                Vector3 point = origin + ray * distance;
                TileHit candidate = CreateWaterHit(candidatePlane, plane, point, distance);
                if (water is null || IsNearerWater(candidate, water.Value)) water = candidate;
            }
        }

        return water is { } nearestWater
            && (terrain is null || nearestWater.Distance < terrain.Value.Distance)
            ? nearestWater
            : terrain;
    }

    bool IsRenderedTerrain(int x, int z, int plane) =>
        _loaded.TryGetValue(RegionCoord.Of(x, z), out RegionHandles? handles)
        && handles.Meshes[plane] is not null
        && TileGroundMesher.IsDrawable(_doc, x, z, plane);

    static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    static bool IntersectWaterPlane(in WaterPlane plane, Vector3 origin, Vector3 direction, float maxDistance,
                                    out float distance)
    {
        distance = 0f;
        if (direction.Y == 0f) return false;

        var min = new Vector3(
            plane.CenterX - plane.HalfExtentX,
            plane.SurfaceY,
            plane.CenterZ - plane.HalfExtentZ);
        var max = new Vector3(
            plane.CenterX + plane.HalfExtentX,
            plane.SurfaceY,
            plane.CenterZ + plane.HalfExtentZ);
        return RayMath.IntersectAabb(origin, direction, min, max, out distance)
            && distance <= maxDistance;
    }

    TileHit CreateWaterHit(in WaterPlane plane, int documentPlane, Vector3 point, float distance)
    {
        float tileSize = _doc.TileSize;
        int minX = TileEdge(plane.CenterX - plane.HalfExtentX, tileSize, z: false);
        int maxX = TileEdge(plane.CenterX + plane.HalfExtentX, tileSize, z: false) - 1;
        int minZ = TileEdge(plane.CenterZ + plane.HalfExtentZ, tileSize, z: true);
        int maxZ = TileEdge(plane.CenterZ - plane.HalfExtentZ, tileSize, z: true) - 1;
        int x = Math.Clamp((int)MathF.Floor(TileWorldSpace.TileX(point.X, tileSize)), minX, maxX);
        int z = Math.Clamp((int)MathF.Floor(TileWorldSpace.TileZ(point.Z, tileSize)), minZ, maxZ);
        return new TileHit(x, z, documentPlane, point, distance);
    }

    // Water planes come from TileWaterPlanes.ToPlane, whose edges are exact tile lattice edges before their
    // float world transform. Rounding is the inverse that recovers those integer edges without letting a tiny
    // multiply or divide error move an outer-edge hit into the neighbouring tile.
    static int TileEdge(float world, float tileSize, bool z) =>
        (int)MathF.Round(z ? TileWorldSpace.TileZ(world, tileSize) : TileWorldSpace.TileX(world, tileSize));

    static bool IsNearerWater(in TileHit candidate, in TileHit current) =>
        candidate.Distance < current.Distance
        || (candidate.Distance == current.Distance
            && (candidate.X < current.X || (candidate.X == current.X && candidate.Z < current.Z)));
}
