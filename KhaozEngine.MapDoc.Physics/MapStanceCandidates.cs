using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>Seats one proposed stance. A game binds it to its character controller's placement proof, which either
/// seats <paramref name="candidateFeet"/> at <paramref name="seatedFeet"/> and returns true, or refuses it.</summary>
public delegate bool MapStanceValidator(Vector3 candidateFeet, out Vector3 seatedFeet);

/// <summary>How <see cref="MapStanceCandidates.Find"/> walks and filters. <see cref="Spacing"/> is the largest gap in
/// metres between neighbouring proposals along an outline. A seated stance is kept only while its capsule is within
/// <see cref="Range"/> plus <see cref="Tolerance"/> of the envelope inside <see cref="Band"/>, the same test as
/// <see cref="MapWorldQueries.Within"/>.</summary>
public sealed record MapStanceOptions(float Spacing, float Range, float Tolerance = 0f, MapInteractionBand? Band = null);

/// <summary>Walk-up stance positions around a placement's interaction envelope. Each envelope member's XZ outline,
/// outset by the capsule radius, is walked separately, so the inner sides of a compound's aperture are proposed too.
/// A native world proposes at the envelope's base height and a resolver-1 world at its legacy support height. The
/// validator owns every step, ledge, slope and support rule and the seated height. Outline points come from the
/// platform sine and cosine, so heads on different platforms can differ by an ulp in a proposal.</summary>
public static class MapStanceCandidates
{
    // The vertices of the regular polygon circumscribing each end cap of a cylinder member, as the envelope sweep uses.
    const int CapSides = MapInteractionEnvelope.CapSides;

    // A walk that would propose more points than this over all of one call's members is refused as too fine.
    const int MaximumProposals = 1 << 20;

    /// <summary>The distinct seated feet positions the validator accepts within reach of
    /// <paramref name="placementId"/>'s envelope, ordered by distance to <paramref name="actorFeet"/>, then X, Z and
    /// Y. Proposals outside the document's inclusive playable bounds are dropped before the validator sees them, and
    /// seats outside them are dropped after it. The capsule is <paramref name="tuning"/>'s radius and half height,
    /// standing on the seated feet. Invalid options, including a spacing that would propose more than 2^20 points,
    /// throw <see cref="ArgumentOutOfRangeException"/> naming <paramref name="options"/>.</summary>
    public static IReadOnlyList<Vector3> Find(MapBuiltWorld world, string placementId, Vector3 actorFeet,
        in MoveTuning tuning, MapStanceOptions options, MapStanceValidator validate)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(placementId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validate);
        if (!IsFinite(actorFeet))
            throw new ArgumentOutOfRangeException(nameof(actorFeet), "Actor feet must be finite.");
        if (!float.IsFinite(options.Spacing) || options.Spacing <= 0f)
            throw new ArgumentOutOfRangeException(nameof(options), "Spacing must be finite and positive.");
        MapWorldQueries.CheckReach(options.Range, options.Tolerance, nameof(options));
        _ = MapWorldQueries.Slab(options.Band, nameof(options));
        float radius = tuning.CapsuleRadius, halfHeight = tuning.CapsuleHalfHeight;
        if (!float.IsFinite(radius) || radius <= 0f || !float.IsFinite(halfHeight) || halfHeight < radius)
            throw new ArgumentOutOfRangeException(nameof(tuning),
                "The capsule needs a finite positive radius and a finite half height of at least the radius.");
        Func<float, float, float>? legacyHeight = world.IsNative ? null : world.LegacySupportHeight ??
            throw new InvalidOperationException("A resolver-1 world needs a legacy support height.");

        MapInteractionEnvelope envelope = world.Placement(placementId).Envelope;
        float baseY = (float)envelope.Bounds.MinY;
        MapResolvedBounds playable = world.Document.PlayableBounds;
        var footprints = new List<List<(double X, double Z)>>();
        Footprints(envelope.Shape, MapDoublePose.From(envelope.WorldPose), footprints);

        // Every outline is walked before the validator runs, so a refused walk never calls it.
        var proposals = new List<(double X, double Z)>();
        foreach (List<(double X, double Z)> footprint in footprints)
            if (!Outline(Hull(footprint), radius, options.Spacing, proposals))
                throw new ArgumentOutOfRangeException(nameof(options),
                    $"Spacing proposes more than {MaximumProposals} points around the envelope.");

        var seated = new List<Vector3>();
        foreach ((double px, double pz) in proposals)
        {
            float x = (float)px, z = (float)pz;
            if (!Playable(playable, x, z)) continue;
            float y = legacyHeight is null ? baseY : legacyHeight(x, z);
            if (!float.IsFinite(y))
                throw new InvalidOperationException("The legacy support height is not finite at a proposal.");
            if (!validate(new Vector3(x, y, z), out Vector3 feet)) continue;
            if (!IsFinite(feet))
                throw new InvalidOperationException("The stance validator seated a proposal at a nonfinite position.");
            if (!Playable(playable, feet.X, feet.Z)) continue;
            var body = new MovementBody(feet + Vector3.UnitY * halfHeight, radius, halfHeight);
            if (MapWorldQueries.Reaches(envelope, body, options.Range, options.Tolerance, options.Band))
                seated.Add(feet);
        }

        seated.Sort((a, b) => Compare(a, b, actorFeet));
        var distinct = new List<Vector3>(seated.Count);
        foreach (Vector3 feet in seated)
            if (distinct.Count == 0 || distinct[^1] != feet) distinct.Add(feet);
        return distinct.AsReadOnly();
    }

    static bool Playable(MapResolvedBounds playable, float x, float z) =>
        x >= playable.MinX && x <= playable.MaxX && z >= playable.MinZ && z <= playable.MaxZ;

    static int Compare(Vector3 a, Vector3 b, Vector3 actor)
    {
        int order = DistanceSquared(a, actor).CompareTo(DistanceSquared(b, actor));
        if (order != 0) return order;
        if ((order = a.X.CompareTo(b.X)) != 0) return order;
        if ((order = a.Z.CompareTo(b.Z)) != 0) return order;
        return a.Y.CompareTo(b.Y);
    }

    static double DistanceSquared(Vector3 a, Vector3 b)
    {
        double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y, dz = (double)a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // The world XZ points of every leaf member, one list per leaf in depth-first child order. Children are placed
    // through their composed poses. A cylinder stands on its base and contributes a circumscribed polygon at each end
    // cap, so its outline never cuts inside it.
    static void Footprints(PhysicsShape shape, MapDoublePose pose, List<List<(double X, double Z)>> footprints)
    {
        switch (shape)
        {
            case CompoundShape compound:
                foreach (CompoundChild child in compound.Children)
                    Footprints(child.Shape, pose.Compose(child.Local), footprints);
                return;
            case BoxShape box:
                {
                    Vector3 h = box.HalfExtents;
                    var points = new List<(double X, double Z)>(8);
                    for (int i = 0; i < 8; i++)
                        Add(points, pose.Transform(new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y,
                            (i & 4) == 0 ? -h.Z : h.Z)));
                    footprints.Add(points);
                    return;
                }
            case ConvexHullShape hull:
                footprints.Add(Points(pose, hull.Points));
                return;
            case TriangleMeshShape mesh:
                footprints.Add(Points(pose, mesh.Vertices));
                return;
            case CylinderShape cylinder:
                {
                    double circumradius = cylinder.Radius / Math.Cos(Math.PI / CapSides);
                    var points = new List<(double X, double Z)>(CapSides * 2);
                    for (int k = 0; k < CapSides; k++)
                    {
                        double angle = k * 2d * Math.PI / CapSides;
                        double a = circumradius * Math.Cos(angle), b = circumradius * Math.Sin(angle);
                        Add(points, pose.Transform(new Vector3((float)a, 0f, (float)b)));
                        Add(points, pose.Transform(new Vector3((float)a, cylinder.Length, (float)b)));
                    }
                    footprints.Add(points);
                    return;
                }
            default:
                throw new NotSupportedException(
                    $"Stance candidates walk boxes, cylinders, hulls, meshes and compounds, not {shape.GetType().Name}.");
        }
    }

    static List<(double X, double Z)> Points(MapDoublePose pose, Vector3[] local)
    {
        var points = new List<(double X, double Z)>(local.Length);
        foreach (Vector3 p in local) Add(points, pose.Transform(p));
        return points;
    }

    static void Add(List<(double X, double Z)> points, MapDouble3 p) => points.Add((p.X, p.Z));

    // The counterclockwise convex hull in (X, Z) by the monotone chain, starting at the lowest X then Z. Collinear
    // points are dropped, so a flat member leaves a segment of two points and a vertical line leaves one point.
    static List<(double X, double Z)> Hull(List<(double X, double Z)> points)
    {
        if (points.Count == 0) return points;
        var sorted = new List<(double X, double Z)>(points);
        sorted.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
        var hull = new List<(double X, double Z)>(sorted.Count + 1);
        for (int pass = 0; pass < 2; pass++)
        {
            int floor = hull.Count;
            for (int i = 0; i < sorted.Count; i++)
            {
                (double X, double Z) p = sorted[pass == 0 ? i : sorted.Count - 1 - i];
                while (hull.Count >= floor + 2 && Cross(hull[^2], hull[^1], p) <= 0d) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        if (hull.Count == 0) hull.Add(sorted[0]);
        else if (hull.Count == 2 && hull[0] == hull[1]) hull.RemoveAt(1);
        return hull;
    }

    static double Cross((double X, double Z) o, (double X, double Z) a, (double X, double Z) b) =>
        (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);

    // Appends points along the hull's outline outset by radius, evenly spaced by at most spacing. The outline is an arc
    // about each hull vertex followed by the outset edge to the next vertex, starting on the first vertex's arc. False,
    // appending nothing, when the points would take the result past MaximumProposals.
    static bool Outline(List<(double X, double Z)> hull, double radius, double spacing, List<(double X, double Z)> result)
    {
        int n = hull.Count;
        if (n == 0) return true;

        // The outward unit normal of each edge from vertex i to vertex i + 1, and each vertex's arc from the normal of
        // the edge before it to the normal of the edge after it.
        var normals = new (double X, double Z)[n];
        var lengths = new double[n];
        for (int i = 0; i < n && n > 1; i++)
        {
            (double X, double Z) a = hull[i], b = hull[(i + 1) % n];
            double dx = b.X - a.X, dz = b.Z - a.Z, length = Math.Sqrt(dx * dx + dz * dz);
            normals[i] = (dz / length, -dx / length);
            lengths[i] = length;
        }
        var starts = new double[n];
        var sweeps = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (n == 1)
            {
                sweeps[i] = 2d * Math.PI;
                continue;
            }
            (double X, double Z) before = normals[(i + n - 1) % n], after = normals[i];
            starts[i] = Math.Atan2(before.Z, before.X);
            double sweep = Math.Atan2(before.X * after.Z - before.Z * after.X, before.X * after.X + before.Z * after.Z);
            // A counterclockwise hull only turns left. A turn of minus a half circle is a two point hull doubling back
            // and a small negative turn is rounding.
            if (sweep < 0d) sweep = sweep < -Math.PI / 2d ? sweep + 2d * Math.PI : 0d;
            sweeps[i] = sweep;
        }

        double total = 0d;
        for (int i = 0; i < n; i++) total += sweeps[i] * radius + lengths[i];
        double count = Math.Ceiling(total / spacing);
        if (!(count <= MaximumProposals - result.Count)) return false;
        int steps = Math.Max(1, (int)count);
        double step = total / steps, offset = 0d;
        int k = 0;
        for (int i = 0; i < n && k < steps; i++)
        {
            (double X, double Z) v = hull[i];
            double arc = sweeps[i] * radius;
            for (; k < steps && k * step < offset + arc; k++)
            {
                double angle = starts[i] + (k * step - offset) / radius;
                result.Add((v.X + radius * Math.Cos(angle), v.Z + radius * Math.Sin(angle)));
            }
            offset += arc;
            if (n == 1) break;
            (double X, double Z) w = hull[(i + 1) % n], normal = normals[i];
            for (; k < steps && k * step < offset + lengths[i]; k++)
            {
                double s = (k * step - offset) / lengths[i];
                result.Add((v.X + (w.X - v.X) * s + radius * normal.X, v.Z + (w.Z - v.Z) * s + radius * normal.Z));
            }
            offset += lengths[i];
        }
        return true;
    }
}
