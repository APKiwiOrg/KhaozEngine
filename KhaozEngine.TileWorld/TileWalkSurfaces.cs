using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.TileWorld;

/// <summary>
/// The walkable object tops a document's objects carry (<see cref="TileObjectArchetype.WalkSurfaces"/>), asked two
/// ways: how high the highest one is over a planar point, and where a ray first lands on one. A bridge deck is the
/// case this exists for: its prop is anchored on the carved bed under it, so the terrain lattice answers the bed and
/// a body, a marker or a click that trusted the lattice alone would sink through the planks.
/// <para>Every surface is placed through <see cref="TileObjectPlacement"/>, the transform the prop was drawn with,
/// so a deck turned in the editor carries its walkable rect round with it. A planar point goes into each
/// candidate's mesh-local frame (untranslate, unrotate) and is tested against the rect there, edges inclusive.
/// Quarter turns use exact axes, so a point exactly on a deck edge stays on it under every rotation.</para>
/// <para>The document and the catalogs are READ THROUGH on every call rather than indexed once, for the reason
/// <c>TileDocumentTargets</c> gives in the tile netcode: an edit to either is meant to be visible to the next frame,
/// and a cached index would stand bodies on a deck the world no longer has. The search window is every anchor that
/// could reach the point, measured from the archetypes that HAVE a surface: the farthest rect corner from its mesh
/// origin in tiles, rounded up, plus the largest footprint side and a tile of slack. That bound is a radius, so it
/// holds under any yaw offset as well as the four quarter turns. A catalog with no surfaced archetype answers
/// before a single region is touched.</para>
/// <para>Only LOADED regions are searched, an object whose archetype the catalogs do not define is skipped, and a
/// surface covers every body on its plane under it: something meant to pass beneath a deck belongs on another
/// plane. The archetype is always the AUTHORED one, the same one the model pick and the collision baker read.</para>
/// </summary>
public static class TileWalkSurfaces
{
    // A sanity bound on the search window, not a content limit: a catalog whose extents reach past this would have
    // TryHeightAt walk tens of thousands of region slots per call. 4096 tiles is the pathfinder's own search cap.
    const int MaxReachTiles = 4096;
    // Past this a tile coordinate plus the reach can overflow the int arithmetic the window runs on.
    const float MaxTileCoordinate = 1 << 28;

    /// <summary>
    /// The height of the HIGHEST walk surface covering a planar point, in world metres: the covering object's anchor
    /// height plus <see cref="TileWalkSurface.Height"/>. Allocates nothing, because it runs per drawn body and per
    /// marker outline point per frame.
    /// <para>Terrain is not consulted, so a surface buried under the ground is still reported here. Taking the
    /// higher of the two is the caller's job, and <c>TileDocumentGroundHeight</c> does exactly that.</para>
    /// </summary>
    /// <param name="doc">The world the objects and their anchor heights come from.</param>
    /// <param name="catalogs">The archetypes carrying the surfaces.</param>
    /// <param name="worldX">World x in metres, the space <see cref="TileWorldDocument.HeightAt"/> takes.</param>
    /// <param name="worldZ">World z in metres, negated against tile z through <see cref="TileWorldSpace"/>.</param>
    /// <param name="plane">The plane. Objects on any other plane are ignored, and a plane the document does not
    /// have answers false.</param>
    /// <param name="height">The highest covering surface in world metres, 0 when none covers the point.</param>
    /// <returns>True when at least one surface covers the point.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> or <paramref name="catalogs"/> is null.</exception>
    public static bool TryHeightAt(TileWorldDocument doc, TileWorldCatalogs catalogs, float worldX, float worldZ,
                                   int plane, out float height)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        height = 0f;
        if ((uint)plane >= (uint)doc.PlaneCount) return false;
        int reach = ReachTiles(catalogs, doc.TileSize);
        if (reach < 0) return false;

        float tileSize = doc.TileSize;
        float tileX = TileWorldSpace.TileX(worldX, tileSize), tileZ = TileWorldSpace.TileZ(worldZ, tileSize);
        if (!(MathF.Abs(tileX) < MaxTileCoordinate) || !(MathF.Abs(tileZ) < MaxTileCoordinate)) return false;

        // Anchors are SW tiles, and every surface point lies within reach tiles of its object's anchor corner, so the
        // anchors that can cover this point sit in this square.
        int x0 = (int)MathF.Floor(tileX) - reach, x1 = (int)MathF.Floor(tileX) + reach;
        int z0 = (int)MathF.Floor(tileZ) - reach, z1 = (int)MathF.Floor(tileZ) + reach;
        RegionCoord lo = RegionCoord.Of(x0, z0), hi = RegionCoord.Of(x1, z1);

        bool found = false;
        float best = float.NegativeInfinity;
        for (int rz = lo.Rz; rz <= hi.Rz; rz++)
        {
            for (int rx = lo.Rx; rx <= hi.Rx; rx++)
            {
                if (doc.GetRegion(new RegionCoord(rx, rz)) is not { } region) continue;
                List<TileObject> objects = region.Objects;
                for (int i = 0; i < objects.Count; i++)
                {
                    TileObject o = objects[i];
                    if (o.Plane != plane || o.X < x0 || o.X > x1 || o.Z < z0 || o.Z > z1) continue;
                    if (catalogs.Archetype(o.ArchetypeId) is not { WalkSurfaces: { Count: > 0 } surfaces } archetype)
                        continue;
                    if (!TryHighestCovering(doc, archetype, o, surfaces, worldX, worldZ, out float top)) continue;
                    if (!found || top > best) best = top;
                    found = true;
                }
            }
        }
        if (found) height = best;
        return found;
    }

    /// <summary>
    /// The nearest point where a ray lands on the TOP of a walk surface on one plane, or null. Only a ray travelling
    /// downward can land on a top, so a level or rising ray misses by definition, and a ray that starts under a deck
    /// passes up through it.
    /// </summary>
    /// <param name="doc">The world the objects and their anchor heights come from. Only loaded regions carry
    /// objects.</param>
    /// <param name="catalogs">The archetypes carrying the surfaces.</param>
    /// <param name="plane">The plane picked against. A plane the document does not have misses.</param>
    /// <param name="origin">Ray origin in world metres.</param>
    /// <param name="direction">Ray direction, not necessarily normalised.</param>
    /// <param name="maxDistance">Farthest accepted hit in world metres, inclusive.</param>
    /// <param name="include">Consulted once per candidate object that carries a surface near the ray, before its
    /// surfaces are tested. Null tests every object. A view uses it to limit the pick to what it draws.</param>
    /// <returns>The hit, as the same <see cref="TileHit"/> <see cref="TileRaycast"/> answers: the tile is the floor
    /// of the hit point in tile coordinates, and the distance is in world metres.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> or <paramref name="catalogs"/> is null.</exception>
    public static TileHit? Raycast(TileWorldDocument doc, TileWorldCatalogs catalogs, int plane, Vector3 origin,
                                   Vector3 direction, float maxDistance, Func<TileObject, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        float lengthSquared = direction.LengthSquared();
        if ((uint)plane >= (uint)doc.PlaneCount || !(maxDistance >= 0f)
            || !(lengthSquared >= 1e-12f) || !float.IsFinite(lengthSquared)
            || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            return null;
        Vector3 ray = Vector3.Normalize(direction);
        if (!(ray.Y < 0f)) return null;
        int reach = ReachTiles(catalogs, doc.TileSize);
        if (reach < 0) return null;

        float tileSize = doc.TileSize;
        float reachMetres = reach * tileSize;
        float bestDistance = float.PositiveInfinity;
        Vector3 bestPoint = default;
        foreach (TileRegion region in doc.LoadedRegions)
        {
            if (!ShadowTouchesRegion(region.Coord, tileSize, reachMetres, origin, ray, maxDistance)) continue;
            List<TileObject> objects = region.Objects;
            for (int i = 0; i < objects.Count; i++)
            {
                TileObject o = objects[i];
                if (o.Plane != plane) continue;
                if (!AnchorNearShadow(o, tileSize, reachMetres, origin, ray, maxDistance)) continue;
                if (catalogs.Archetype(o.ArchetypeId) is not { WalkSurfaces: { Count: > 0 } surfaces } archetype)
                    continue;
                if (include is not null && !include(o)) continue;

                TileObjectPlacement.AnchorPlanar(tileSize, archetype, o, out float anchorX, out float anchorZ);
                TileObjectPlacement.PlanarBasis(archetype, o.Rotation, out float cos, out float sin);
                float anchorY = doc.HeightAt(anchorX, anchorZ, o.Plane);
                float halfX = archetype.SizeX * tileSize * 0.5f, halfZ = archetype.SizeZ * tileSize * 0.5f;
                for (int s = 0; s < surfaces.Count; s++)
                {
                    if (surfaces[s] is not { } surface) continue;
                    float distance = (anchorY + surface.Height - origin.Y) / ray.Y;
                    if (!(distance >= 0f) || distance > maxDistance || !(distance < bestDistance)) continue;
                    Vector3 point = origin + ray * distance;
                    ToLocal(point.X - anchorX, point.Z - anchorZ, cos, sin, out float localX, out float localZ);
                    if (!Covers(surface, localX, localZ, halfX, halfZ)) continue;
                    bestDistance = distance;
                    bestPoint = point;
                }
            }
        }
        if (float.IsPositiveInfinity(bestDistance)) return null;
        int tileX = (int)MathF.Floor(TileWorldSpace.TileX(bestPoint.X, tileSize));
        int tileZ = (int)MathF.Floor(TileWorldSpace.TileZ(bestPoint.Z, tileSize));
        return new TileHit(tileX, tileZ, plane, bestPoint, bestDistance);
    }

    // How far, in tiles, an anchor corner can sit from a point one of its surfaces covers, or -1 when no archetype
    // carries a surface at all. Walked per call over the concrete value collection, which allocates nothing.
    internal static int ReachTiles(TileWorldCatalogs catalogs, float tileSize)
    {
        bool any = false;
        int side = 0;
        float radius = 0f;
        foreach (TileObjectArchetype archetype in catalogs.ArchetypeValues)
        {
            if (archetype.WalkSurfaces is not { Count: > 0 } surfaces) continue;
            any = true;
            side = Math.Max(side, Math.Max(archetype.SizeX, archetype.SizeZ));
            float halfX = archetype.SizeX * tileSize * 0.5f, halfZ = archetype.SizeZ * tileSize * 0.5f;
            for (int i = 0; i < surfaces.Count; i++)
            {
                if (surfaces[i] is not { } s) continue;
                float x = MathF.Max(MathF.Abs(s.MinX ?? -halfX), MathF.Abs(s.MaxX ?? halfX));
                float z = MathF.Max(MathF.Abs(s.MinZ ?? -halfZ), MathF.Abs(s.MaxZ ?? halfZ));
                float corner = MathF.Sqrt(x * x + z * z);
                if (corner > radius) radius = corner;
            }
        }
        if (!any) return -1;
        double tiles = Math.Ceiling(radius / (double)tileSize) + side + 1;
        return tiles < MaxReachTiles ? (int)tiles : MaxReachTiles;
    }

    static bool TryHighestCovering(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o,
                                   List<TileWalkSurface> surfaces, float worldX, float worldZ, out float top)
    {
        float tileSize = doc.TileSize;
        TileObjectPlacement.AnchorPlanar(tileSize, archetype, o, out float anchorX, out float anchorZ);
        TileObjectPlacement.PlanarBasis(archetype, o.Rotation, out float cos, out float sin);
        ToLocal(worldX - anchorX, worldZ - anchorZ, cos, sin, out float localX, out float localZ);
        float halfX = archetype.SizeX * tileSize * 0.5f, halfZ = archetype.SizeZ * tileSize * 0.5f;

        bool covered = false;
        float highest = 0f;
        for (int i = 0; i < surfaces.Count; i++)
        {
            if (surfaces[i] is not { } surface || !Covers(surface, localX, localZ, halfX, halfZ)) continue;
            if (!covered || surface.Height > highest) highest = surface.Height;
            covered = true;
        }
        // The lattice is sampled only once a surface covers the point, so a deck the point is nowhere near costs
        // the planar test alone.
        top = covered ? doc.HeightAt(anchorX, anchorZ, o.Plane) + highest : 0f;
        return covered;
    }

    // The inverse of the planar rotation PlanarBasis describes: rotate a world offset from the anchor by minus the
    // yaw. x = dx cos - dz sin, z = dx sin + dz cos.
    static void ToLocal(float dx, float dz, float cos, float sin, out float localX, out float localZ)
    {
        localX = dx * cos - dz * sin;
        localZ = dx * sin + dz * cos;
    }

    // Edges inclusive. A NaN anywhere compares false, so a surface edited to carry one covers nothing.
    static bool Covers(TileWalkSurface surface, float localX, float localZ, float halfX, float halfZ) =>
        localX >= (surface.MinX ?? -halfX) && localX <= (surface.MaxX ?? halfX)
        && localZ >= (surface.MinZ ?? -halfZ) && localZ <= (surface.MaxZ ?? halfZ);

    // Whether the ray's XZ shadow, from its origin out to maxDistance, passes within reachMetres of a region's rect.
    // The box is unbounded in y, so the slab test is a 2D one. World z runs opposite to tile z, so the region's
    // north edge is its minimum world z.
    static bool ShadowTouchesRegion(RegionCoord region, float tileSize, float reachMetres, Vector3 origin,
                                    Vector3 ray, float maxDistance)
    {
        var min = new Vector3(TileWorldSpace.WorldX(region.OriginX, tileSize) - reachMetres, -float.MaxValue,
            TileWorldSpace.WorldZ(region.OriginZ + TileRegion.Size, tileSize) - reachMetres);
        var max = new Vector3(TileWorldSpace.WorldX(region.OriginX + TileRegion.Size, tileSize) + reachMetres,
            float.MaxValue, TileWorldSpace.WorldZ(region.OriginZ, tileSize) + reachMetres);
        return RayMath.IntersectAabb(origin, ray, min, max, out float entry) && entry <= maxDistance;
    }

    // Whether an object's anchor corner lies within reachMetres of the ray's XZ shadow segment. Cheap enough to run
    // on every object of a touched region ahead of the archetype lookup.
    static bool AnchorNearShadow(TileObject o, float tileSize, float reachMetres, Vector3 origin, Vector3 ray,
                                 float maxDistance)
    {
        float px = TileWorldSpace.WorldX(o.X, tileSize) - origin.X;
        float pz = TileWorldSpace.WorldZ(o.Z, tileSize) - origin.Z;
        float shadowSquared = ray.X * ray.X + ray.Z * ray.Z;
        float along = shadowSquared > 0f ? Math.Clamp((px * ray.X + pz * ray.Z) / shadowSquared, 0f, maxDistance) : 0f;
        float ex = px - ray.X * along, ez = pz - ray.Z * along;
        return ex * ex + ez * ez <= reachMetres * reachMetres;
    }
}
