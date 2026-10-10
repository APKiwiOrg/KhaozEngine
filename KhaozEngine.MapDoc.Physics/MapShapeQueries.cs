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
        ArgumentNullException.ThrowIfNull(shape);
        (MapDouble3 origin, MapDouble3 direction) = Validate(ray);
        normal = Vector3.Zero;
        if (!Cast(origin, direction, 0d, ray.MaxDistance, shape, MapDoublePose.From(pose), out distance,
                out MapDouble3 n, out bool inside))
            return false;
        normal = Unit(inside ? direction.Scale(-1d) : n);
        return true;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Shared validation and conversion
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>The ray's origin and unit direction in double. Throws for a nonfinite origin or direction, a zero
    /// direction and a negative or nonfinite maximum distance.</summary>
    internal static (MapDouble3 Origin, MapDouble3 Direction) Validate(MapPickRay ray)
    {
        Vector3 o = ray.Origin, d = ray.Direction;
        if (!float.IsFinite(o.X) || !float.IsFinite(o.Y) || !float.IsFinite(o.Z))
            throw new ArgumentOutOfRangeException(nameof(ray), "Ray origin must be finite.");
        if (!float.IsFinite(d.X) || !float.IsFinite(d.Y) || !float.IsFinite(d.Z))
            throw new ArgumentOutOfRangeException(nameof(ray), "Ray direction must be finite.");
        if (!float.IsFinite(ray.MaxDistance) || ray.MaxDistance < 0f)
            throw new ArgumentOutOfRangeException(nameof(ray), "Ray maximum distance must be finite and nonnegative.");
        var direction = MapDouble3.From(d);
        double length = Math.Sqrt(Dot(direction, direction));
        if (length == 0d) throw new ArgumentOutOfRangeException(nameof(ray), "Ray direction must not be zero.");
        return (MapDouble3.From(o), direction.Scale(1d / length));
    }

    internal static Vector3 Unit(MapDouble3 v)
    {
        double length = Math.Sqrt(Dot(v, v));
        return length == 0d ? Vector3.Zero : new Vector3((float)(v.X / length), (float)(v.Y / length), (float)(v.Z / length));
    }

    static double Dot(MapDouble3 a, MapDouble3 b) => MapConvexQueries.Dot(a, b);

    static MapDoublePose Inverse(MapDoublePose pose) => new(default, -pose.Qx, -pose.Qy, -pose.Qz, pose.Qw);

    // Points rotated into the frame that is world-aligned and centred on the pose position, so far-from-origin worlds
    // keep their precision.
    static MapDouble3[] Centred(MapDoublePose pose, Vector3[] local)
    {
        var points = new MapDouble3[local.Length];
        for (int i = 0; i < local.Length; i++) points[i] = pose.Rotate(MapDouble3.From(local[i]));
        return points;
    }

    static double Surface(double core, double radius) => Math.Max(core - radius, 0d);

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
                return MeasureCylinder(probe, cylinder, pose, band);
            case ConvexHullShape hull:
                return MeasurePoints(probe, Centred(pose, hull.Points), pose, band);
            case TriangleMeshShape mesh:
                return MeasureMesh(probe, mesh, pose, band);
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

    static double MeasureCylinder(in MapProbe probe, CylinderShape cylinder, MapDoublePose pose, MapSlab band)
    {
        double r = cylinder.Radius, length = cylinder.Length;
        double min = band.Min - pose.Position.Y, max = band.Max - pose.Position.Y;
        MapDouble3 p0 = probe.Bottom.Subtract(pose.Position), p1 = probe.Top.Subtract(pose.Position);
        MapDouble3 axis = pose.Rotate(new MapDouble3(0d, 1d, 0d));
        if (axis.X == 0d && axis.Z == 0d)
        {
            // Upright either way up: the band clips its height, and the upright probe separates into a radial and a
            // vertical gap.
            double low = Math.Max(Math.Min(0d, length * axis.Y), min), high = Math.Min(Math.Max(0d, length * axis.Y), max);
            if (low > high) return double.PositiveInfinity;
            double radial = Math.Max(Math.Sqrt(p0.X * p0.X + p0.Z * p0.Z) - r, 0d);
            double vertical = Gap(p0.Y, p1.Y, low, high);
            return Surface(Math.Sqrt(radial * radial + vertical * vertical), probe.Radius);
        }

        double xx = axis.X * axis.X, zz = axis.Z * axis.Z, n = xx + axis.Y * axis.Y + zz;
        double rise = r * Math.Sqrt((xx + zz) / n);
        double lowest = Math.Min(0d, length * axis.Y) - rise, highest = Math.Max(0d, length * axis.Y) + rise;
        if (highest < min || lowest > max) return double.PositiveInfinity;

        // The distance from a point to a solid cylinder is closed form and convex along the probe, so a fixed golden
        // section search over the probe finds its minimum to double precision.
        MapDoublePose inverse = Inverse(pose);
        MapDouble3 l0 = inverse.Rotate(p0), l1 = inverse.Rotate(p1);
        double core = GoldenMinimum(s => PointToCylinder(Lerp(l0, l1, s), r, length), 0d, 1d, out double at);
        if (lowest >= min && highest <= max) return Surface(core, probe.Radius);

        // A closest point inside the band is the answer. Otherwise the clipped answer lies on the band plane it
        // crossed, because the distance is convex over the cylinder.
        MapDouble3 closest = pose.Rotate(ClosestOnCylinder(Lerp(l0, l1, at), r, length));
        if (closest.Y >= min && closest.Y <= max) return Surface(core, probe.Radius);
        return Surface(SliceDistance(p0, p1, axis, r, length, closest.Y > max ? max : min), probe.Radius);
    }

    // The distance from the upright probe to the slice of a tilted cylinder at height k. Each cross-section disc meets
    // the plane in a horizontal chord, and the horizontal distance to the chord is convex along the axis.
    static double SliceDistance(MapDouble3 p0, MapDouble3 p1, MapDouble3 axis, double r, double length, double k)
    {
        double norm = Math.Sqrt(Dot(axis, axis));
        MapDouble3 a = axis.Scale(1d / norm);
        double extent = length * norm;
        double h = Math.Sqrt(a.X * a.X + a.Z * a.Z);
        var u = new MapDouble3(-a.Z / h, 0d, a.X / h);
        MapDouble3 v = MapDouble3.Cross(u, a);
        if (v.Y < 0d) v = v.Scale(-1d);
        double reachY = r * v.Y, sLow, sHigh;
        if (a.Y == 0d)
        {
            if (Math.Abs(k) > reachY) return double.PositiveInfinity;
            sLow = 0d;
            sHigh = extent;
        }
        else
        {
            double s1 = (k - reachY) / a.Y, s2 = (k + reachY) / a.Y;
            sLow = Math.Max(0d, Math.Min(s1, s2));
            sHigh = Math.Min(extent, Math.Max(s1, s2));
            if (sLow > sHigh) return double.PositiveInfinity;
        }
        double horizontal = GoldenMinimum(s =>
        {
            double beta = Math.Clamp((k - s * a.Y) / v.Y, -r, r), alpha = Math.Sqrt(r * r - beta * beta);
            MapDouble3 centre = a.Scale(s).Add(v.Scale(beta));
            return HorizontalToSegment(p0.X, p0.Z, centre.Subtract(u.Scale(alpha)), centre.Add(u.Scale(alpha)));
        }, sLow, sHigh, out _);
        double vertical = Gap(p0.Y, p1.Y, k, k);
        return Math.Sqrt(horizontal * horizontal + vertical * vertical);
    }

    static double PointToCylinder(MapDouble3 p, double r, double length)
    {
        double radial = Math.Max(Math.Sqrt(p.X * p.X + p.Z * p.Z) - r, 0d);
        double axial = p.Y < 0d ? -p.Y : p.Y > length ? p.Y - length : 0d;
        return Math.Sqrt(radial * radial + axial * axial);
    }

    static MapDouble3 ClosestOnCylinder(MapDouble3 p, double r, double length)
    {
        double rho = Math.Sqrt(p.X * p.X + p.Z * p.Z), scale = rho > r ? r / rho : 1d;
        return new MapDouble3(p.X * scale, Math.Clamp(p.Y, 0d, length), p.Z * scale);
    }

    static double HorizontalToSegment(double x, double z, MapDouble3 a, MapDouble3 b)
    {
        double dx = b.X - a.X, dz = b.Z - a.Z, lengthSquared = dx * dx + dz * dz;
        double s = lengthSquared == 0d ? 0d : Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / lengthSquared, 0d, 1d);
        double ex = x - (a.X + s * dx), ez = z - (a.Z + s * dz);
        return Math.Sqrt(ex * ex + ez * ez);
    }

    // The vertical gap between [low0, high0] and [low1, high1].
    static double Gap(double low0, double high0, double low1, double high1) =>
        high1 < low0 ? low0 - high1 : low1 > high0 ? low1 - high0 : 0d;

    static MapDouble3 Lerp(MapDouble3 a, MapDouble3 b, double s) => a.Add(b.Subtract(a).Scale(s));

    // A fixed-length golden section search for the minimum of a convex function on [low, high]. The best value seen
    // wins, the first on a tie.
    static double GoldenMinimum(Func<double, double> f, double low, double high, out double at)
    {
        const int Iterations = 90;
        double ratio = (Math.Sqrt(5d) - 1d) * 0.5;
        at = low;
        double best = f(low);
        Consider(high, f(high), ref best, ref at);
        if (!(high > low)) return best;
        double a = low, b = high;
        double x1 = b - ratio * (b - a), x2 = a + ratio * (b - a);
        double f1 = f(x1), f2 = f(x2);
        Consider(x1, f1, ref best, ref at);
        Consider(x2, f2, ref best, ref at);
        for (int i = 0; i < Iterations; i++)
        {
            if (f1 <= f2)
            {
                b = x2;
                x2 = x1;
                f2 = f1;
                x1 = b - ratio * (b - a);
                f1 = f(x1);
                Consider(x1, f1, ref best, ref at);
            }
            else
            {
                a = x1;
                x1 = x2;
                f1 = f2;
                x2 = a + ratio * (b - a);
                f2 = f(x2);
                Consider(x2, f2, ref best, ref at);
            }
        }
        return best;
    }

    static void Consider(double x, double value, ref double best, ref double at)
    {
        if (value >= best) return;
        best = value;
        at = x;
    }

    static double MeasureMesh(in MapProbe probe, TriangleMeshShape mesh, MapDoublePose pose, MapSlab band)
    {
        MapDouble3[] vertices = Centred(pose, mesh.Vertices);
        double min = band.Min - pose.Position.Y, max = band.Max - pose.Position.Y;
        MapDouble3 p0 = probe.Bottom.Subtract(pose.Position), p1 = probe.Top.Subtract(pose.Position);
        Span<MapDouble3> polygon = stackalloc MapDouble3[8];
        Span<MapDouble3> scratch = stackalloc MapDouble3[8];
        int[] indices = mesh.Indices;
        double best = double.PositiveInfinity;
        for (int i = 0; i + 2 < indices.Length && best > 0d; i += 3)
        {
            polygon[0] = vertices[indices[i]];
            polygon[1] = vertices[indices[i + 1]];
            polygon[2] = vertices[indices[i + 2]];
            int count = 3;
            if (!double.IsNegativeInfinity(min))
            {
                count = ClipPolygon(polygon, count, min, keepAbove: true, scratch);
                scratch[..count].CopyTo(polygon);
            }
            if (!double.IsPositiveInfinity(max))
            {
                count = ClipPolygon(polygon, count, max, keepAbove: false, scratch);
                scratch[..count].CopyTo(polygon);
            }
            for (int j = 1; j + 1 < count; j++)
                best = Math.Min(best, SegmentToTriangle(p0, p1, polygon[0], polygon[j], polygon[j + 1]));
        }
        return double.IsPositiveInfinity(best) ? best : Surface(best, probe.Radius);
    }

    // One Sutherland-Hodgman pass against the plane at height y. A triangle grows to at most five points.
    static int ClipPolygon(ReadOnlySpan<MapDouble3> input, int count, double y, bool keepAbove, Span<MapDouble3> output)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            MapDouble3 current = input[i], next = input[(i + 1) % count];
            bool currentIn = keepAbove ? current.Y >= y : current.Y <= y;
            bool nextIn = keepAbove ? next.Y >= y : next.Y <= y;
            if (currentIn) output[n++] = current;
            if (currentIn == nextIn) continue;
            double s = (y - current.Y) / (next.Y - current.Y);
            output[n++] = new MapDouble3(current.X + s * (next.X - current.X), y, current.Z + s * (next.Z - current.Z));
        }
        return n;
    }

    static double SegmentToTriangle(MapDouble3 p0, MapDouble3 p1, MapDouble3 a, MapDouble3 b, MapDouble3 c)
    {
        MapDouble3 normal = MapDouble3.Cross(b.Subtract(a), c.Subtract(a));
        if (Dot(normal, normal) > 0d)
        {
            double d0 = Dot(normal, p0.Subtract(a)), d1 = Dot(normal, p1.Subtract(a));
            if (d0 != d1 && ((d0 <= 0d && d1 >= 0d) || (d0 >= 0d && d1 <= 0d)))
            {
                MapDouble3 x = Lerp(p0, p1, d0 / (d0 - d1));
                if (Dot(MapDouble3.Cross(b.Subtract(a), x.Subtract(a)), normal) >= 0d &&
                    Dot(MapDouble3.Cross(c.Subtract(b), x.Subtract(b)), normal) >= 0d &&
                    Dot(MapDouble3.Cross(a.Subtract(c), x.Subtract(c)), normal) >= 0d)
                    return 0d;
            }
        }
        double best = Length(MapConvexQueries.ClosestOnTriangle(p0, a, b, c).Subtract(p0));
        best = Math.Min(best, Length(MapConvexQueries.ClosestOnTriangle(p1, a, b, c).Subtract(p1)));
        best = Math.Min(best, SegmentToSegment(p0, p1, a, b));
        best = Math.Min(best, SegmentToSegment(p0, p1, b, c));
        return Math.Min(best, SegmentToSegment(p0, p1, c, a));
    }

    static double Length(MapDouble3 v) => Math.Sqrt(Dot(v, v));

    // The closest points of two segments, after Ericson, Real-Time Collision Detection 5.1.9.
    static double SegmentToSegment(MapDouble3 p1, MapDouble3 q1, MapDouble3 p2, MapDouble3 q2)
    {
        MapDouble3 d1 = q1.Subtract(p1), d2 = q2.Subtract(p2), r = p1.Subtract(p2);
        double a = Dot(d1, d1), e = Dot(d2, d2), f = Dot(d2, r), s, t;
        if (a == 0d && e == 0d) return Length(r);
        if (a == 0d)
        {
            s = 0d;
            t = Math.Clamp(f / e, 0d, 1d);
        }
        else
        {
            double c = Dot(d1, r);
            if (e == 0d)
            {
                t = 0d;
                s = Math.Clamp(-c / a, 0d, 1d);
            }
            else
            {
                double b = Dot(d1, d2), denominator = a * e - b * b;
                s = denominator != 0d ? Math.Clamp((b * f - c * e) / denominator, 0d, 1d) : 0d;
                t = (b * s + f) / e;
                if (t < 0d)
                {
                    t = 0d;
                    s = Math.Clamp(-c / a, 0d, 1d);
                }
                else if (t > 1d)
                {
                    t = 1d;
                    s = Math.Clamp((b - c) / a, 0d, 1d);
                }
            }
        }
        return Length(Lerp(p1, q1, s).Subtract(Lerp(p2, q2, t)));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Ray cast
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Casts origin + t direction, t in [<paramref name="tStart"/>, <paramref name="tEnd"/>], against
    /// <paramref name="shape"/>. A hit reports t and the world normal of the surface entered. A ray already inside a
    /// solid member at <paramref name="tStart"/> hits it there with <paramref name="inside"/> set. The nearest member
    /// wins, the first on a tie.</summary>
    internal static bool Cast(MapDouble3 origin, MapDouble3 direction, double tStart, double tEnd, PhysicsShape shape,
        MapDoublePose pose, out double t, out MapDouble3 normal, out bool inside)
    {
        t = 0d;
        normal = default;
        inside = false;
        switch (shape)
        {
            case CompoundShape compound:
                {
                    bool any = false;
                    foreach (CompoundChild child in compound.Children)
                    {
                        if (!Cast(origin, direction, tStart, any ? Math.Min(tEnd, t) : tEnd, child.Shape,
                                pose.Compose(child.Local), out double ct, out MapDouble3 cn, out bool ci) ||
                            (any && ct >= t))
                            continue;
                        any = true;
                        t = ct;
                        normal = cn;
                        inside = ci;
                    }
                    return any;
                }
            case BoxShape box:
                return CastBox(origin, direction, tStart, tEnd, box, pose, out t, out normal, out inside);
            case CylinderShape cylinder:
                return CastCylinder(origin, direction, tStart, tEnd, cylinder, pose, out t, out normal, out inside);
            case ConvexHullShape hull:
                {
                    MapDouble3 start = origin.Subtract(pose.Position).Add(direction.Scale(tStart));
                    if (!MapConvexQueries.Raycast(Centred(pose, hull.Points), start, direction, tEnd - tStart,
                            out double lambda, out normal))
                        return false;
                    t = tStart + lambda;
                    inside = Dot(normal, normal) == 0d;
                    return true;
                }
            case TriangleMeshShape mesh:
                return CastMesh(origin, direction, tStart, tEnd, mesh, pose, out t, out normal);
            default:
                throw Unsupported(shape);
        }
    }

    static bool CastBox(MapDouble3 origin, MapDouble3 direction, double tStart, double tEnd, BoxShape box,
        MapDoublePose pose, out double t, out MapDouble3 normal, out bool inside)
    {
        t = 0d;
        normal = default;
        inside = false;
        MapDoublePose inverse = Inverse(pose);
        MapDouble3 o = inverse.Rotate(origin.Subtract(pose.Position)), d = inverse.Rotate(direction);
        Vector3 h = box.HalfExtents;
        double enter = double.NegativeInfinity, exit = double.PositiveInfinity;
        MapDouble3 face = default;
        for (int axis = 0; axis < 3; axis++)
        {
            double oi = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
            double di = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            double hi = axis == 0 ? h.X : axis == 1 ? h.Y : h.Z;
            if (di == 0d)
            {
                if (oi < -hi || oi > hi) return false;
                continue;
            }
            double a = (-hi - oi) / di, b = (hi - oi) / di;
            if (Math.Min(a, b) > enter)
            {
                enter = Math.Min(a, b);
                double sign = di > 0d ? -1d : 1d;
                face = new MapDouble3(axis == 0 ? sign : 0d, axis == 1 ? sign : 0d, axis == 2 ? sign : 0d);
            }
            exit = Math.Min(exit, Math.Max(a, b));
        }
        if (enter > exit || exit < tStart || enter > tEnd) return false;
        if (enter >= tStart)
        {
            t = enter;
            normal = pose.Rotate(face);
            return true;
        }
        t = tStart;
        inside = true;
        return true;
    }

    static bool CastCylinder(MapDouble3 origin, MapDouble3 direction, double tStart, double tEnd, CylinderShape cylinder,
        MapDoublePose pose, out double t, out MapDouble3 normal, out bool inside)
    {
        t = 0d;
        normal = default;
        inside = false;
        MapDoublePose inverse = Inverse(pose);
        MapDouble3 o = inverse.Rotate(origin.Subtract(pose.Position)), d = inverse.Rotate(direction);
        double r = cylinder.Radius, length = cylinder.Length;

        // The slab between the base and the top cap.
        double enter = double.NegativeInfinity, exit = double.PositiveInfinity, cap = 0d;
        if (d.Y == 0d)
        {
            if (o.Y < 0d || o.Y > length) return false;
        }
        else
        {
            double a = -o.Y / d.Y, b = (length - o.Y) / d.Y;
            enter = Math.Min(a, b);
            exit = Math.Max(a, b);
            cap = d.Y > 0d ? -1d : 1d;
        }

        // The infinite side wall.
        double sideEnter = double.NegativeInfinity, sideExit = double.PositiveInfinity;
        double qa = d.X * d.X + d.Z * d.Z, qb = o.X * d.X + o.Z * d.Z, qc = o.X * o.X + o.Z * o.Z - r * r;
        if (qa == 0d)
        {
            if (qc > 0d) return false;
        }
        else
        {
            double discriminant = qb * qb - qa * qc;
            if (discriminant < 0d) return false;
            double root = Math.Sqrt(discriminant);
            sideEnter = (-qb - root) / qa;
            sideExit = (-qb + root) / qa;
        }

        bool side = sideEnter > enter;
        double tEnter = Math.Max(enter, sideEnter), tExit = Math.Min(exit, sideExit);
        if (tEnter > tExit || tExit < tStart || tEnter > tEnd) return false;
        if (tEnter < tStart)
        {
            t = tStart;
            inside = true;
            return true;
        }
        t = tEnter;
        MapDouble3 hit = o.Add(d.Scale(tEnter));
        normal = pose.Rotate(side ? new MapDouble3(hit.X, 0d, hit.Z) : new MapDouble3(0d, cap, 0d));
        return true;
    }

    static bool CastMesh(MapDouble3 origin, MapDouble3 direction, double tStart, double tEnd, TriangleMeshShape mesh,
        MapDoublePose pose, out double t, out MapDouble3 normal)
    {
        t = 0d;
        normal = default;
        MapDouble3[] vertices = Centred(pose, mesh.Vertices);
        MapDouble3 o = origin.Subtract(pose.Position);
        int[] indices = mesh.Indices;
        bool any = false;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            MapDouble3 a = vertices[indices[i]];
            MapDouble3 e1 = vertices[indices[i + 1]].Subtract(a), e2 = vertices[indices[i + 2]].Subtract(a);
            MapDouble3 p = MapDouble3.Cross(direction, e2);
            double determinant = Dot(e1, p);
            if (determinant == 0d) continue;
            double inverse = 1d / determinant;
            MapDouble3 s = o.Subtract(a);
            double u = Dot(s, p) * inverse;
            if (u < 0d || u > 1d) continue;
            MapDouble3 q = MapDouble3.Cross(s, e1);
            double v = Dot(direction, q) * inverse;
            if (v < 0d || u + v > 1d) continue;
            double hit = Dot(e2, q) * inverse;
            if (hit < tStart || hit > tEnd || (any && hit >= t)) continue;
            any = true;
            t = hit;
            normal = MapDouble3.Cross(e1, e2);
            if (Dot(normal, direction) > 0d) normal = normal.Scale(-1d);
        }
        return any;
    }
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
