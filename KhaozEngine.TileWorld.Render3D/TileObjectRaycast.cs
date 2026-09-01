using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileWorld.Render3D;

/// <summary>One object a ray passes through.</summary>
/// <param name="ObjectId">The document object id.</param>
/// <param name="ArchetypeId">Its archetype, so a caller can gate or name the hit without a second document
/// walk.</param>
/// <param name="Distance">Entry distance along the ray, in units of the direction's length.</param>
public readonly record struct TileObjectHit(long ObjectId, string ArchetypeId, float Distance);

/// <summary>
/// Ray picking against the MODELS of a document's objects: every object whose drawn body the ray passes
/// through, nearest first, so what a player can click IS what they can see, a well's roof included. The
/// counterpart of <see cref="TileRaycast"/>, which answers the GROUND, and of the footprint join, which answers
/// tiles: this one answers the picture.
/// </summary>
/// <remarks>
/// The placement is derived exactly the way a prop draw derives it (<see cref="TileObjectProps.AnchorPosition"/>
/// and <see cref="TileObjectProps.YawRadians"/>, scale 1), so a hit tests the same transform the object was
/// drawn at rather than a second copy of the placement rule. The box is the model's own, from the
/// <see cref="BoundsSource"/>, carried into the object's local frame (untranslate, unrotate) and slab-tested
/// with <see cref="RayMath.IntersectAabb"/>, the same shape the actor clickbox uses downstream.
/// <para>What this deliberately does NOT decide: whether a hit is clickable. A hidden roof, an out-of-radius
/// prop, or a non-interactive archetype are game and view rules, and the hit carries the archetype id so the
/// caller can apply them. It also does not cut at the ground: a caller that wants "nothing behind the hill"
/// passes the ground hit's distance (plus its own slack) as the max distance.</para>
/// </remarks>
public static class TileObjectRaycast
{
    /// <summary>The local-space model box of an archetype, relative to its anchor. False when the source has
    /// none, which drops that object from picking rather than inventing a box.</summary>
    /// <param name="archetype">The archetype being asked about.</param>
    /// <param name="min">The box minimum, relative to the anchor position.</param>
    /// <param name="max">The box maximum.</param>
    public delegate bool BoundsSource(TileObjectArchetype archetype, out Vector3 min, out Vector3 max);

    /// <summary>Names every object the ray passes through, nearest first, into <paramref name="hits"/>.</summary>
    /// <param name="document">The world the ray is against. Only resident regions carry objects.</param>
    /// <param name="catalogs">The archetypes the document's objects reference. An object whose archetype the
    /// catalog does not know is skipped: there is no model to have clicked.</param>
    /// <param name="plane">The plane picked against.</param>
    /// <param name="origin">Ray origin in world metres.</param>
    /// <param name="direction">Ray direction, not necessarily normalised. Distances come back in units of its
    /// length, exactly as <see cref="RayMath.IntersectAabb"/> reports them.</param>
    /// <param name="maxDistance">Hits at or past this are cut, in the same units.</param>
    /// <param name="bounds">The model box per archetype. See <see cref="BoundsSource"/>.</param>
    /// <param name="hits">Cleared first, then filled nearest first. An exact distance tie orders by the LOWER
    /// object id, so a stack of objects answers the same way on every run. The caller is expected to hold one
    /// list and reuse it.</param>
    /// <returns>The number of hits.</returns>
    public static int Pick(TileWorldDocument document, TileWorldCatalogs catalogs, int plane,
        Vector3 origin, Vector3 direction, float maxDistance, BoundsSource bounds, List<TileObjectHit> hits)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(hits);
        hits.Clear();
        if (maxDistance <= 0f || direction.LengthSquared() <= 1e-12f) return 0;

        // The search rect is the ray segment's XZ shadow in TILES, padded by the widest footprint the catalog
        // allows, so an object whose anchor sits beside the shadow but whose body leans into it is still
        // tested. World z runs opposite to tile z, which TileWorldSpace owns, so both endpoints go through it
        // rather than through arithmetic done here.
        Vector3 end = origin + direction * maxDistance;
        float ts = document.TileSize;
        int x0 = (int)MathF.Floor(Math.Min(TileWorldSpace.TileX(origin.X, ts), TileWorldSpace.TileX(end.X, ts)));
        int x1 = (int)MathF.Ceiling(Math.Max(TileWorldSpace.TileX(origin.X, ts), TileWorldSpace.TileX(end.X, ts)));
        int z0 = (int)MathF.Floor(Math.Min(TileWorldSpace.TileZ(origin.Z, ts), TileWorldSpace.TileZ(end.Z, ts)));
        int z1 = (int)MathF.Ceiling(Math.Max(TileWorldSpace.TileZ(origin.Z, ts), TileWorldSpace.TileZ(end.Z, ts)));
        const int Pad = 8;
        var search = new TileRect(x0 - Pad, z0 - Pad, x1 - x0 + Pad * 2 + 1, z1 - z0 + Pad * 2 + 1);

        foreach (TileObject o in document.ObjectsIn(search, plane))
        {
            if (catalogs.Archetype(o.ArchetypeId) is not { } archetype) continue;
            if (!bounds(archetype, out Vector3 min, out Vector3 max)) continue;

            // The object's own frame: anchored at its drawn position, yawed by its drawn rotation. RayMath owns
            // the untranslate + unrotate, so the engine is not hand-rolling the oriented-box test against itself.
            Vector3 at = TileObjectProps.AnchorPosition(document, archetype, o);
            float yaw = TileObjectProps.YawRadians(archetype, o.Rotation);

            if (!RayMath.IntersectObbY(origin, direction, at, yaw, min, max, out float t)) continue;
            if (t >= maxDistance) continue;
            hits.Add(new TileObjectHit(o.Id, o.ArchetypeId, t));
        }

        hits.Sort(static (a, b) =>
        {
            int byDistance = a.Distance.CompareTo(b.Distance);
            return byDistance != 0 ? byDistance : a.ObjectId.CompareTo(b.ObjectId);
        });
        return hits.Count;
    }
}
