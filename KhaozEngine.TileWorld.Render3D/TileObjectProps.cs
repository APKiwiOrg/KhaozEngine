using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>The prop placements of one region-plane, split by the roof rule so a view can hide the roofs over
/// the building the camera subject stands in and keep drawing everything else.</summary>
/// <param name="Ground">Placements for every non-roof object of the region-plane.</param>
/// <param name="Roofs">Placements for the region-plane's roof objects, drawn only when the roofs are shown.</param>
public sealed record TileRegionProps(IReadOnlyList<PropPlacement> Ground, IReadOnlyList<PropPlacement> Roofs)
{
    /// <summary>The world tile footprint of each <see cref="Roofs"/> entry, same order and same length. A
    /// placement carries a world POSITION and no extent, and the roof rule has to know which tiles a roof covers
    /// to decide whether it belongs to the observer's building, so the footprints ride alongside.
    /// <para>Empty by default, which keeps a record built by hand compiling: a roof this list does not reach is
    /// never hidden by the interior rule (<c>RoofVisibility.AlwaysHidden</c> still hides it), the same
    /// hide-nothing-you-cannot-place direction the interior fill's cap takes.
    /// <see cref="TileObjectProps.Build"/> always fills it.</para></summary>
    public IReadOnlyList<TileRect> RoofFootprints { get; init; } = Array.Empty<TileRect>();

    /// <summary>The <c>TileObject.Id</c> behind each <see cref="Ground"/> entry, same order and same length. A
    /// placement carries an ARCHETYPE id and nothing that names the object it came from, so without this there
    /// is no way to find the one entry a per-object change touches and the only correct answer is to rebuild the
    /// whole region-plane.
    /// <para>Empty by default, which keeps a record built by hand compiling: an entry this list does not reach
    /// simply cannot be found by object id, and <see cref="TileObjectProps.TryReplaceObject"/> answers null
    /// rather than guessing. <see cref="TileObjectProps.Build"/> always fills it.</para></summary>
    public IReadOnlyList<long> GroundObjectIds { get; init; } = Array.Empty<long>();

    /// <summary>The <c>TileObject.Id</c> behind each <see cref="Roofs"/> entry, same order and same length. The
    /// roof half of <see cref="GroundObjectIds"/>.</summary>
    public IReadOnlyList<long> RoofObjectIds { get; init; } = Array.Empty<long>();
}

/// <summary>Turns a region-plane's <see cref="TileObject"/>s into the <see cref="PropPlacement"/>s the existing
/// prop path draws: one placement per object, anchored at the centre of its rotated footprint with the
/// document's ground height, yawed by the tile-world rotation convention, and split into roofs and everything
/// else. Objects on another plane, and objects whose archetype the catalogs do not define, are skipped rather
/// than thrown on, because content routinely outlives a catalog edit and a missing tree must not take the whole
/// region's props down with it.</summary>
public static class TileObjectProps
{
    /// <summary>Degrees of yaw one quarter turn of <see cref="TileObject.Rotation"/> adds.</summary>
    public const float DegreesPerRotation = 90f;

    /// <summary>Builds the placements of one region-plane, roofs separated from the rest and each roof's rotated
    /// tile footprint recorded beside it. A region the document does not hold builds empty lists.</summary>
    /// <param name="doc">The world the objects and the ground heights come from.</param>
    /// <param name="catalogs">The archetypes each object is resolved through.</param>
    /// <param name="region">The region to build.</param>
    /// <param name="plane">The plane to build. Objects on any other plane are skipped.</param>
    /// <param name="archetypeOverride">Consulted at the archetype lookup with each object's id, so a caller can
    /// draw one placed object as a DIFFERENT archetype without touching the document: null, or a null answer,
    /// leaves the object as it was authored. This is the per-object look seam, and it is deliberately not
    /// woodcutting-shaped: a depleted resource node, a damage state, a seasonal or day-night swap and an
    /// editor's preview of a change all ride it. An answer naming an archetype the catalogs do not hold SKIPS
    /// the object, the same way an unresolvable authored archetype does, because content routinely outlives a
    /// catalog edit and a missing stump must not take the region's props down with it.</param>
    public static TileRegionProps Build(TileWorldDocument doc, TileWorldCatalogs catalogs, RegionCoord region,
                                        int plane, Func<long, string?>? archetypeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);

        var ground = new List<PropPlacement>();
        var roofs = new List<PropPlacement>();
        var roofFootprints = new List<TileRect>();
        var groundIds = new List<long>();
        var roofIds = new List<long>();
        TileRegion? data = doc.GetRegion(region);
        if (data is null) return new TileRegionProps(ground, roofs);

        foreach (TileObject o in data.Objects)
        {
            if (o.Plane != plane) continue;
            string archetypeId = archetypeOverride?.Invoke(o.Id) ?? o.ArchetypeId;
            TileObjectArchetype? a = catalogs.Archetype(archetypeId);
            if (a is null) continue;

            Vector3 at = AnchorPosition(doc, a, o);
            var placement = new PropPlacement(archetypeId, at.X, at.Y, at.Z, 1f, YawRadians(a, o.Rotation), 0);
            if (!a.IsRoof) { ground.Add(placement); groundIds.Add(o.Id); continue; }
            roofs.Add(placement);
            roofIds.Add(o.Id);
            roofFootprints.Add(TileFootprint.Of(a, o.X, o.Z, o.Rotation));
        }
        return new TileRegionProps(ground, roofs)
        {
            RoofFootprints = roofFootprints,
            GroundObjectIds = groundIds,
            RoofObjectIds = roofIds,
        };
    }

    /// <summary>
    /// The NARROW path behind a per-object look change: one object's placement recomputed and spliced into an
    /// already-built region-plane, so a chopped tree costs one anchor sample and a list copy rather than a walk
    /// of every object in the region. What it deliberately does NOT do is touch the ground mesh, which is the
    /// expensive half of the region-plane rebuild <c>TileWorldView.MarkDirty</c> plus <c>Flush</c> pays: a prop
    /// swap changes no vertex of the ground and no height, so remeshing and re-uploading it is work for nothing.
    /// <para>Answers null when the splice cannot be proved equivalent to a full <see cref="Build"/>, and the
    /// caller then runs one. Three cases reach it, all of them ORDER questions: the object has no entry in this
    /// region-plane (nothing to replace, and where to insert it depends on the document's own object order), the
    /// new archetype does not resolve (the entry has to be removed, same question in reverse), and the new
    /// archetype's roof flag differs from the old one (the entry moves between two lists, and its index in the
    /// destination is the same question again). The common case by far, one non-roof object drawn as another, is
    /// none of them: the entry is replaced where it stands and the result is byte for byte what a rebuild would
    /// have produced.</para>
    /// <para>Costs O(placements on the region-plane) all the same, and allocates: the id list is walked to find
    /// the entry and the whole affected placement list is copied. So N changes in one snapshot cost N of those
    /// walks and N copies rather than one, which is fine at the handful-at-a-time rate a game depletes resource
    /// nodes at and is worth knowing before driving a whole-region seasonal or day-night swap through it. There
    /// is no batch door, deliberately: a caller with many changes at once calls <see cref="Build"/> once instead,
    /// and a batched splice is its own round rather than a knob bolted onto this one.</para>
    /// </summary>
    /// <param name="doc">The world the anchor height comes from.</param>
    /// <param name="catalogs">The archetypes the new archetype is resolved through.</param>
    /// <param name="props">The region-plane's current placements, from <see cref="Build"/>.</param>
    /// <param name="o">The object whose look changed, from <c>TileWorldDocument.FindObject</c>.</param>
    /// <param name="archetypeId">The archetype to draw it as, overridden or authored.</param>
    /// <returns>The region-plane's placements with that one object rewritten, or null when the caller must
    /// rebuild the region-plane instead.</returns>
    public static TileRegionProps? TryReplaceObject(TileWorldDocument doc, TileWorldCatalogs catalogs,
                                                    TileRegionProps props, TileObject o, string archetypeId)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(o);

        TileObjectArchetype? a = catalogs.Archetype(archetypeId);
        if (a is null) return null;

        int groundAt = IndexOf(props.GroundObjectIds, o.Id);
        int roofAt = groundAt < 0 ? IndexOf(props.RoofObjectIds, o.Id) : -1;
        if (groundAt < 0 && roofAt < 0) return null;
        // The classification moved, so the entry moves between the two lists and the destination index is the
        // document's own object order, which a built record does not carry. A rebuild answers it for free.
        if (a.IsRoof != roofAt >= 0) return null;

        Vector3 at = AnchorPosition(doc, a, o);
        var placement = new PropPlacement(archetypeId, at.X, at.Y, at.Z, 1f, YawRadians(a, o.Rotation), 0);

        if (groundAt >= 0)
        {
            List<PropPlacement> ground = Copy(props.Ground);
            ground[groundAt] = placement;
            return props with { Ground = ground };
        }

        List<PropPlacement> roofsCopy = Copy(props.Roofs);
        roofsCopy[roofAt] = placement;
        // The footprint rides with the roof and is read by the interior rule, so a stump-for-roof swap that
        // changed the footprint would otherwise hide the wrong tiles. A hand-built record whose footprint list
        // is short keeps the hide-nothing-you-cannot-place direction TileRegionProps already takes.
        if (roofAt >= props.RoofFootprints.Count) return props with { Roofs = roofsCopy };
        var footprints = new List<TileRect>(props.RoofFootprints);
        footprints[roofAt] = TileFootprint.Of(a, o.X, o.Z, o.Rotation);
        return props with { Roofs = roofsCopy, RoofFootprints = footprints };
    }

    static List<PropPlacement> Copy(IReadOnlyList<PropPlacement> from)
    {
        var copy = new List<PropPlacement>(from.Count);
        for (int i = 0; i < from.Count; i++) copy.Add(from[i]);
        return copy;
    }

    // Indexed rather than IndexOf on the interface, which has none, and rather than LINQ, which would allocate
    // an enumerator on a path a game calls once per depleted object.
    static int IndexOf(IReadOnlyList<long> ids, long id)
    {
        for (int i = 0; i < ids.Count; i++)
            if (ids[i] == id) return i;
        return -1;
    }

    /// <summary>The yaw in radians for an instance rotation, NEGATIVE per quarter turn. That sign is what makes
    /// <c>Matrix4x4.CreateRotationY</c> turn clockwise seen from above with north up: north is -z in world space
    /// (<see cref="TileWorldSpace"/>), and a row-vector rotation by t sends the west point (-0.5, 0, 0) to
    /// (-0.5 cos t, 0, +0.5 sin t), which only reaches the north point (0, 0, -0.5) at t of -90 degrees. Under
    /// rotation 1 a mesh point on the WEST side of the tile centre therefore lands on the NORTH side, which is
    /// the tile-world convention (0 west, 1 north, 2 east, 3 south). The archetype's yaw offset is folded in
    /// under the same sign, for a mesh authored off-axis.</summary>
    public static float YawRadians(TileObjectArchetype archetype, int rotation)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        return -(rotation * DegreesPerRotation + archetype.YawOffsetDegrees) * (MathF.PI / 180f);
    }

    /// <summary>Where an instance's mesh origin sits in world metres: the centre of the footprint it covers after
    /// rotation, at the document's ground height for that spot. A mesh is therefore authored centred on its own
    /// footprint, with its base at y 0.</summary>
    public static Vector3 AnchorPosition(TileWorldDocument doc, TileObjectArchetype archetype, TileObject o)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(o);

        (int sizeX, int sizeZ) = TileFootprint.Rotated(archetype, o.Rotation);
        float cx = TileWorldSpace.WorldX(o.X + sizeX / 2f, doc.TileSize);
        float cz = TileWorldSpace.WorldZ(o.Z + sizeZ / 2f, doc.TileSize);
        return new Vector3(cx, doc.HeightAt(cx, cz, o.Plane), cz);
    }
}
