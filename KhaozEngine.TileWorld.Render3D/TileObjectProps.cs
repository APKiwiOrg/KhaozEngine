using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

/// <summary>The prop placements of one region-plane, split by the roof rule so a view can hide the roofs while
/// the camera subject stands indoors and keep drawing everything else.</summary>
/// <param name="Ground">Placements for every non-roof object of the region-plane.</param>
/// <param name="Roofs">Placements for the region-plane's roof objects, drawn only when the roofs are shown.</param>
public sealed record TileRegionProps(IReadOnlyList<PropPlacement> Ground, IReadOnlyList<PropPlacement> Roofs);

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

    /// <summary>Builds the placements of one region-plane, roofs separated from the rest. A region the document
    /// does not hold builds two empty lists.</summary>
    public static TileRegionProps Build(TileWorldDocument doc, TileWorldCatalogs catalogs, RegionCoord region, int plane)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);

        var ground = new List<PropPlacement>();
        var roofs = new List<PropPlacement>();
        TileRegion? data = doc.GetRegion(region);
        if (data is null) return new TileRegionProps(ground, roofs);

        foreach (TileObject o in data.Objects)
        {
            if (o.Plane != plane) continue;
            TileObjectArchetype? a = catalogs.Archetype(o.ArchetypeId);
            if (a is null) continue;

            Vector3 at = AnchorPosition(doc, a, o);
            var placement = new PropPlacement(o.ArchetypeId, at.X, at.Y, at.Z, 1f, YawRadians(a, o.Rotation), 0);
            (a.IsRoof ? roofs : ground).Add(placement);
        }
        return new TileRegionProps(ground, roofs);
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
