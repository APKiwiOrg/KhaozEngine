using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Movement;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>An absolute world height range that clips interaction envelopes after their raise.</summary>
public readonly record struct MapInteractionBand(float MinY, float MaxY);

/// <summary>The nearest envelope a pick ray entered: the placement, its distance along the ray, the world point and
/// the unit world normal of the surface entered.</summary>
public sealed record MapPickHit(string PlacementId, long? NumericId, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>Pick, reach and physical distance over a built world, in scalar double with no physics backend, so a
/// client and a server built from one document give identical answers. Pick, <see cref="Distance"/> and
/// <see cref="Within"/> query interaction envelopes only, clipped to an optional band. <see cref="PhysicalDistance"/>
/// queries colliders only. A ray that starts inside an envelope member hits it at its start, or where it enters the
/// band. Heads agree except for one residual dependency on the platform's double sine and cosine. Placement poses take
/// the sine and cosine of half the yaw in double, rounded to float once, so a last-bit difference shows only when it
/// rounds across a float boundary. A box member turned about world Y alone is measured through
/// <see cref="ReachGeometry"/>, which also takes its yaw's sine and cosine in double, so a difference there shows only
/// exactly at a range boundary. Consumers that need both heads to agree pass a <c>tolerance</c> to
/// <see cref="Within"/>.</summary>
public sealed class MapWorldQueries
{
    readonly MapBuiltWorld _world;
    readonly IReadOnlyList<MapPlacementGeometry> _placements;
    readonly MapEnvelopeIndex _index;

    /// <summary>Indexes the envelopes of <paramref name="world"/>.</summary>
    public MapWorldQueries(MapBuiltWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _placements = world.Placements;
        var bounds = new MapBox3[_placements.Count];
        for (int i = 0; i < _placements.Count; i++) bounds[i] = _placements[i].Envelope.Bounds;
        _index = new MapEnvelopeIndex(bounds);
    }

    /// <summary>The nearest envelope <paramref name="ray"/> enters within its maximum distance and inside
    /// <paramref name="band"/>, or null. Equal distances resolve to the ordinally smallest placement id.</summary>
    public MapPickHit? Pick(MapPickRay ray, MapInteractionBand? band = null) => Pick(ray, band, out _);

    /// <summary><see cref="Pick(MapPickRay, Nullable{MapInteractionBand})"/>, also reporting how many distinct envelopes it
    /// tested exactly.</summary>
    internal MapPickHit? Pick(MapPickRay ray, MapInteractionBand? band, out int inspected)
    {
        (MapDouble3 origin, MapDouble3 direction) = MapShapeQueries.Validate(ray);
        MapSlab slab = Slab(band);
        inspected = 0;

        // The band clips the ray before any envelope is tested.
        double tStart = 0d, tEnd = ray.MaxDistance;
        if (!MapShapeQueries.ClipRay(origin.Y, direction.Y, slab.Min, slab.Max, 0d, ref tStart, ref tEnd)) return null;
        bool bandEntry = tStart > 0d;

        int best = -1;
        double bestT = double.PositiveInfinity;
        MapDouble3 bestNormal = default;
        var seen = new HashSet<int>();
        foreach ((double entry, int[] envelopes) in _index.Cells(origin, direction, tStart, tEnd))
        {
            if (entry > bestT) break;
            foreach (int i in envelopes)
            {
                if (!seen.Add(i)) continue;
                inspected++;
                MapInteractionEnvelope envelope = _placements[i].Envelope;
                double end = Math.Min(tEnd, bestT);
                if (!Touches(envelope.Bounds, origin, direction, tStart, end) ||
                    !MapShapeQueries.Cast(origin, direction, tStart, end, envelope.Shape,
                        MapDoublePose.From(envelope.WorldPose), out double t, out MapDouble3 normal, out bool inside))
                    continue;
                if (t > bestT || (t == bestT &&
                    string.CompareOrdinal(_placements[i].PlacementId, _placements[best].PlacementId) >= 0))
                    continue;
                if (inside)
                    normal = bandEntry && t == tStart
                        ? new MapDouble3(0d, direction.Y > 0d ? -1d : 1d, 0d)
                        : direction.Scale(-1d);
                best = i;
                bestT = t;
                bestNormal = normal;
            }
        }
        if (best < 0) return null;
        MapPlacementGeometry hit = _placements[best];
        MapDouble3 point = origin.Add(direction.Scale(bestT));
        return new MapPickHit(hit.PlacementId, hit.NumericId, (float)bestT,
            new Vector3((float)point.X, (float)point.Y, (float)point.Z), MapShapeQueries.Unit(bestNormal));
    }

    /// <summary>The edge distance from <paramref name="body"/> to the placement's envelope inside
    /// <paramref name="band"/>, zero on overlap, or positive infinity when no part of the envelope lies in the
    /// band.</summary>
    public float Distance(in MovementBody body, string placementId, MapInteractionBand? band = null)
    {
        MapInteractionEnvelope envelope = Placement(placementId).Envelope;
        return (float)MapShapeQueries.Measure(MapProbe.Of(body), envelope.Shape, MapDoublePose.From(envelope.WorldPose),
            Slab(band), default);
    }

    /// <summary>Whether <paramref name="body"/> is within <paramref name="range"/> plus <paramref name="tolerance"/> of
    /// the placement's envelope inside <paramref name="band"/>. A yawed box member answers through
    /// <see cref="ReachGeometry.Within"/>.</summary>
    public bool Within(in MovementBody body, string placementId, float range, float tolerance = 0f,
        MapInteractionBand? band = null)
    {
        CheckReach(range, tolerance);
        return Reaches(Placement(placementId).Envelope, body, range, tolerance, band);
    }

    /// <summary>Throws unless <paramref name="range"/> and <paramref name="tolerance"/> are finite and
    /// nonnegative, naming <paramref name="parameter"/> when given, otherwise the failing argument.</summary>
    internal static void CheckReach(float range, float tolerance, string? parameter = null)
    {
        if (!float.IsFinite(range) || range < 0f)
            throw new ArgumentOutOfRangeException(parameter ?? nameof(range), "Range must be finite and nonnegative.");
        if (!float.IsFinite(tolerance) || tolerance < 0f)
            throw new ArgumentOutOfRangeException(parameter ?? nameof(tolerance),
                "Tolerance must be finite and nonnegative.");
    }

    /// <summary>The reach test behind <see cref="Within"/>, for range and tolerance already checked.</summary>
    internal static bool Reaches(MapInteractionEnvelope envelope, in MovementBody body, float range, float tolerance,
        MapInteractionBand? band)
    {
        double distance = MapShapeQueries.Measure(MapProbe.Of(body), envelope.Shape,
            MapDoublePose.From(envelope.WorldPose), Slab(band), new MapReachTest(true, range, tolerance));
        return distance <= (double)range + tolerance;
    }

    /// <summary>The edge distance from <paramref name="body"/> to the placement's collider, zero on overlap, or positive
    /// infinity when the placement has no collider.</summary>
    public float PhysicalDistance(in MovementBody body, string placementId)
    {
        MapPlacementGeometry placement = Placement(placementId);
        if (placement.Collider is null) return float.PositiveInfinity;
        return (float)MapShapeQueries.Measure(MapProbe.Of(body), placement.Collider,
            MapDoublePose.From(placement.WorldPose), MapSlab.All, default);
    }

    MapPlacementGeometry Placement(string placementId) => _world.Placement(placementId);

    /// <summary>The world height range of <paramref name="band"/>, unbounded without one. Throws for a band that is not
    /// finite or is inverted, naming <paramref name="parameter"/> when given, otherwise the band.</summary>
    internal static MapSlab Slab(MapInteractionBand? band, string? parameter = null)
    {
        if (band is not { } b) return MapSlab.All;
        if (!float.IsFinite(b.MinY) || !float.IsFinite(b.MaxY) || b.MinY > b.MaxY)
            throw new ArgumentOutOfRangeException(parameter ?? nameof(band),
                "A band needs finite heights with MinY at most MaxY.");
        return new MapSlab(b.MinY, b.MaxY);
    }

    // Whether the ray segment meets the bounds, a cheap refusal before the exact test.
    static bool Touches(MapBox3 box, MapDouble3 origin, MapDouble3 direction, double tStart, double tEnd)
    {
        const double Padding = 1d / 1024d;
        return MapShapeQueries.ClipRay(origin.X, direction.X, box.MinX, box.MaxX, Padding, ref tStart, ref tEnd) &&
            MapShapeQueries.ClipRay(origin.Y, direction.Y, box.MinY, box.MaxY, Padding, ref tStart, ref tEnd) &&
            MapShapeQueries.ClipRay(origin.Z, direction.Z, box.MinZ, box.MaxZ, Padding, ref tStart, ref tEnd);
    }
}
