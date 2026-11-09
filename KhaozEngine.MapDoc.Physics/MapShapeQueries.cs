using System;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A pick ray in world metres. <see cref="Direction"/> need not be unit length. Distances along the ray are
/// metres along its normalized direction, from <see cref="Origin"/> up to <see cref="MaxDistance"/>.</summary>
public readonly record struct MapPickRay(Vector3 Origin, Vector3 Direction, float MaxDistance);

/// <summary>Backend-free distance and ray queries against a physics shape at a pose, in scalar double so both heads
/// agree. Shapes follow the physics seam's convention: a box is centred on its pose, a cylinder stands on its base and
/// spans <c>Length</c> along its local Y axis, hull and mesh points are relative to their pose, and compound children
/// compose their local pose with the parent's. Boxes, cylinders, hulls, meshes and compounds are measured, the shapes
/// a collision payload carries. A ray that starts inside a solid member hits it at its start.</summary>
public static class MapShapeQueries
{
    /// <summary>The distance from <paramref name="point"/> to <paramref name="shape"/> at <paramref name="pose"/>,
    /// zero inside it.</summary>
    public static double Distance(Vector3 point, PhysicsShape shape, Pose pose)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
            throw new ArgumentOutOfRangeException(nameof(point), "Point must be finite.");
        var p = MapDouble3.From(point);
        return Measure(new MapProbe(p, p, 0d, default, false), shape, MapDoublePose.From(pose), MapSlab.All, default);
    }

    /// <summary>The edge distance from the upright capsule <paramref name="body"/> to <paramref name="shape"/> at
    /// <paramref name="pose"/>, zero when they overlap.</summary>
    public static double Distance(in MovementBody body, PhysicsShape shape, Pose pose)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return Measure(MapProbe.Of(body), shape, MapDoublePose.From(pose), MapSlab.All, default);
    }

    /// <summary>Casts <paramref name="ray"/> against <paramref name="shape"/> at <paramref name="pose"/>. A hit reports
    /// its distance along the ray and the unit world normal of the surface it entered, or the reversed ray direction
    /// when the ray starts inside.</summary>
    public static bool Raycast(MapPickRay ray, PhysicsShape shape, Pose pose, out double distance, out Vector3 normal)
    {
        var shared = new ShapeRay(ray.Origin.X, ray.Origin.Y, ray.Origin.Z,
            ray.Direction.X, ray.Direction.Y, ray.Direction.Z, ray.MaxDistance);
        bool found = ShapeRayQueries.Cast(shared, shape, pose, out ShapeRayHit hit);
        distance = hit.Distance;
        normal = hit.Normal;
        return found;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Shared validation and conversion
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>The ray's origin and unit direction in double. Throws for a nonfinite origin or direction, a zero
    /// direction and a negative or nonfinite maximum distance.</summary>
    internal static (MapDouble3 Origin, MapDouble3 Direction) Validate(MapPickRay ray)
    {
        return ShapeRayQueries.Validate(new(ray.Origin.X, ray.Origin.Y, ray.Origin.Z,
            ray.Direction.X, ray.Direction.Y, ray.Direction.Z, ray.MaxDistance));
    }

    internal static Vector3 Unit(MapDouble3 v) => ShapeQueryMath.Unit(v);
    internal static double Dot(MapDouble3 a, MapDouble3 b) => ShapeQueryMath.Dot(a, b);
    internal static MapDoublePose Inverse(MapDoublePose pose) => ShapeQueryMath.Inverse(pose);
    internal static MapDouble3[] Centred(MapDoublePose pose, Vector3[] local) => ShapeQueryMath.Centred(pose, local);
    internal static double Surface(double core, double radius) => ShapeQueryMath.Surface(core, radius);
    internal static MapDouble3 Lerp(MapDouble3 a, MapDouble3 b, double s) => ShapeQueryMath.Lerp(a, b, s);
    internal static bool ClipRay(double origin, double direction, double min, double max, double padding,
        ref double tStart, ref double tEnd) => ShapeQueryMath.ClipRay(origin, direction, min, max, padding, ref tStart, ref tEnd);

    static NotSupportedException Unsupported(PhysicsShape shape) => new(
        $"Shape queries measure boxes, cylinders, hulls, meshes and compounds, not {shape.GetType().Name}.");

    // -----------------------------------------------------------------------------------------------------------------
    // Distance
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>The edge distance from <paramref name="probe"/> to the part of <paramref name="shape"/> between the
    /// slab's world heights, or positive infinity when no part lies there. When <paramref name="reach"/> is active a
    /// yaw-only box member answers zero inside the range and positive infinity outside it.</summary>
    internal static double Measure(in MapProbe probe, PhysicsShape shape, MapDoublePose pose, MapSlab band,
        MapReachTest reach)
    {
        switch (shape)
        {
            case CompoundShape compound:
                {
                    double best = double.PositiveInfinity;
                    foreach (CompoundChild child in compound.Children)
                    {
                        best = Math.Min(best, Measure(probe, child.Shape, pose.Compose(child.Local), band, reach));
                        if (best == 0d) break;
                    }
                    return best;
                }
            case BoxShape box:
                return MeasureBox(probe, box, pose, band, reach);
            case CylinderShape cylinder:
                return MapCylinderQueries.Measure(probe, cylinder, pose, band);
            case ConvexHullShape hull:
                return MeasurePoints(probe, Centred(pose, hull.Points), pose, band);
            case TriangleMeshShape mesh:
                return MapMeshQueries.Measure(probe, mesh, pose, band);
            default:
                throw Unsupported(shape);
        }
    }

    static double MeasureBox(in MapProbe probe, BoxShape box, MapDoublePose pose, MapSlab band, MapReachTest reach)
    {
        Vector3 h = box.HalfExtents;
        // A box turned about world Y alone is the yawed box the movement reach contract measures exactly.
        if (probe.HasBody && pose.Qx == 0d && pose.Qz == 0d && h.X > 0f && h.Z > 0f && h.Y >= 0f &&
            float.IsFinite(h.X) && float.IsFinite(h.Y) && float.IsFinite(h.Z))
        {
            double low = Math.Max(pose.Position.Y - h.Y, band.Min), high = Math.Min(pose.Position.Y + h.Y, band.Max);
            if (low > high) return double.PositiveInfinity;
            ReachTarget target = ReachTarget.Box(
                new Vector3((float)pose.Position.X, (float)((low + high) * 0.5), (float)pose.Position.Z),
                new Vector3(h.X, (float)((high - low) * 0.5), h.Z), (float)(2d * Math.Atan2(pose.Qy, pose.Qw)));
            if (reach.Active)
                return ReachGeometry.Within(probe.Body, target, reach.Range, reach.Tolerance) ? 0d : double.PositiveInfinity;
            return ReachGeometry.Distance(probe.Body, target);
        }
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
        return MeasurePoints(probe, Centred(pose, corners), pose, band);
    }

    static double MeasurePoints(in MapProbe probe, MapDouble3[] points, MapDoublePose pose, MapSlab band)
    {
        double min = band.Min - pose.Position.Y, max = band.Max - pose.Position.Y;
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        foreach (MapDouble3 p in points)
        {
            low = Math.Min(low, p.Y);
            high = Math.Max(high, p.Y);
        }
        if (high < min || low > max) return double.PositiveInfinity;
        if (low < min || high > max)
        {
            points = MapConvexQueries.ClipToSlab(points, min, max);
            if (points.Length == 0) return double.PositiveInfinity;
        }
        ReadOnlySpan<MapDouble3> segment = [probe.Bottom.Subtract(pose.Position), probe.Top.Subtract(pose.Position)];
        return Surface(MapConvexQueries.Distance(points, segment), probe.Radius);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Ray cast
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Casts origin + t direction, t in [<paramref name="tStart"/>, <paramref name="tEnd"/>], against
    /// <paramref name="shape"/>. A hit reports t and the world normal of the surface entered. A ray already inside a
    /// solid member at <paramref name="tStart"/> hits it there with <paramref name="inside"/> set. The nearest member
    /// wins, the first on a tie.</summary>
    internal static bool Cast(MapDouble3 origin, MapDouble3 direction, double tStart, double tEnd, PhysicsShape shape,
        MapDoublePose pose, out double t, out MapDouble3 normal, out bool inside) =>
        ShapeRayQueries.Cast(origin, direction, tStart, tEnd, shape, pose, out t, out normal, out inside);
}

/// <summary>An upright probe segment in world metres with the radius around it. A movement body keeps itself so a
/// yawed box can be measured by the movement reach contract.</summary>
internal readonly struct MapProbe(MapDouble3 bottom, MapDouble3 top, double radius, MovementBody body, bool hasBody)
{
    internal MapDouble3 Bottom { get; } = bottom;
    internal MapDouble3 Top { get; } = top;
    internal double Radius { get; } = radius;
    internal MovementBody Body { get; } = body;
    internal bool HasBody { get; } = hasBody;

    /// <summary>The body's core segment, whose half length is its half height less its radius.</summary>
    internal static MapProbe Of(in MovementBody body)
    {
        if (body.Radius <= 0f) throw new ArgumentException("A default movement body is invalid.", nameof(body));
        var centre = MapDouble3.From(body.Centre);
        double half = (double)body.HalfHeight - body.Radius;
        return new MapProbe(new MapDouble3(centre.X, centre.Y - half, centre.Z),
            new MapDouble3(centre.X, centre.Y + half, centre.Z), body.Radius, body, true);
    }
}

/// <summary>A world height range. <see cref="All"/> is unbounded.</summary>
internal readonly record struct MapSlab(double Min, double Max)
{
    internal static MapSlab All => new(double.NegativeInfinity, double.PositiveInfinity);
}

/// <summary>A reach test in progress: when active, a yawed box member answers through
/// <see cref="ReachGeometry.Within"/>.</summary>
internal readonly record struct MapReachTest(bool Active, float Range, float Tolerance);
