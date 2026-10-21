using System;
using System.Numerics;
using static KhaozEngine.Physics.ShapeQueryMath;

namespace KhaozEngine.Physics;

/// <summary>A finite double-precision ray. Direction need not be normalized. Distance is in metres.</summary>
public readonly record struct ShapeRay(double X, double Y, double Z, double Dx, double Dy, double Dz, double MaxDistance);

/// <summary>A surface hit, or a zero-distance hit with reversed ray normal when starting inside a solid.</summary>
public readonly record struct ShapeRayHit(double Distance, Vector3 Normal, bool Inside);

/// <summary>Backend-free scalar double ray queries for boxes, base-aligned cylinders, hulls, meshes and compounds.</summary>
public static class ShapeRayQueries
{
    public static bool Cast(in ShapeRay ray, PhysicsShape shape, Pose pose, out ShapeRayHit hit)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var (origin, direction) = Validate(ray);
        hit = default;
        if (!Cast(origin, direction, 0d, ray.MaxDistance, shape, ShapeDoublePose.From(pose), out double distance,
                out ShapeDouble3 normal, out bool inside))
            return false;
        hit = new(distance, Unit(inside ? direction.Scale(-1d) : normal), inside);
        return true;
    }

    internal static (ShapeDouble3 Origin, ShapeDouble3 Direction) Validate(in ShapeRay ray)
    {
        if (!double.IsFinite(ray.X) || !double.IsFinite(ray.Y) || !double.IsFinite(ray.Z))
            throw new ArgumentOutOfRangeException(nameof(ray), "Ray origin must be finite.");
        if (!double.IsFinite(ray.Dx) || !double.IsFinite(ray.Dy) || !double.IsFinite(ray.Dz))
            throw new ArgumentOutOfRangeException(nameof(ray), "Ray direction must be finite.");
        if (!double.IsFinite(ray.MaxDistance) || ray.MaxDistance < 0d)
            throw new ArgumentOutOfRangeException(nameof(ray), "Ray maximum distance must be finite and nonnegative.");
        var direction = new ShapeDouble3(ray.Dx, ray.Dy, ray.Dz);
        double squaredLength = Dot(direction, direction);
        // A nonzero subnormal square has already lost relative precision. Scale before its square root,
        // while keeping the released dot, square-root and normalization operations for float-range rays.
        if (squaredLength == 0d || double.IsSubnormal(squaredLength) || double.IsInfinity(squaredLength))
        {
            double scale = Math.Max(Math.Abs(ray.Dx), Math.Max(Math.Abs(ray.Dy), Math.Abs(ray.Dz)));
            if (scale == 0d) throw new ArgumentOutOfRangeException(nameof(ray), "Ray direction must not be zero.");
            direction = new(ray.Dx / scale, ray.Dy / scale, ray.Dz / scale);
            squaredLength = Dot(direction, direction);
        }
        double length = Math.Sqrt(squaredLength);
        return (new(ray.X, ray.Y, ray.Z), direction.Scale(1d / length));
    }

    static NotSupportedException Unsupported(PhysicsShape shape) => new(
        $"Shape queries measure boxes, cylinders, hulls, meshes and compounds, not {shape.GetType().Name}.");

    internal static bool Cast(ShapeDouble3 origin, ShapeDouble3 direction, double tStart, double tEnd, PhysicsShape shape,
        ShapeDoublePose pose, out double t, out ShapeDouble3 normal, out bool inside)
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
                                pose.Compose(child.Local), out double ct, out ShapeDouble3 cn, out bool ci) ||
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
                    ShapeDouble3 start = origin.Subtract(pose.Position).Add(direction.Scale(tStart));
                    if (!ShapeQueryMath.Raycast(Centred(pose, hull.Points), start, direction, tEnd - tStart,
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

    static bool CastBox(ShapeDouble3 origin, ShapeDouble3 direction, double tStart, double tEnd, BoxShape box,
        ShapeDoublePose pose, out double t, out ShapeDouble3 normal, out bool inside)
    {
        t = 0d;
        normal = default;
        inside = false;
        ShapeDoublePose inverse = Inverse(pose);
        ShapeDouble3 o = inverse.Rotate(origin.Subtract(pose.Position)), d = inverse.Rotate(direction);
        Vector3 h = box.HalfExtents;
        double enter = double.NegativeInfinity, exit = double.PositiveInfinity;
        ShapeDouble3 face = default;
        for (int axis = 0; axis < 3; axis++)
        {
            double oi = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
            double di = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            double hi = axis == 0 ? h.X : axis == 1 ? h.Y : h.Z;
            double before = enter;
            if (!ClipRay(oi, di, -hi, hi, 0d, ref enter, ref exit)) return false;
            if (enter > before)
            {
                double sign = di > 0d ? -1d : 1d;
                face = new ShapeDouble3(axis == 0 ? sign : 0d, axis == 1 ? sign : 0d, axis == 2 ? sign : 0d);
            }
        }
        if (exit < tStart || enter > tEnd) return false;
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
    internal static bool CastCylinder(ShapeDouble3 origin, ShapeDouble3 direction, double tStart, double tEnd, CylinderShape cylinder,
        ShapeDoublePose pose, out double t, out ShapeDouble3 normal, out bool inside)
    {
        t = 0d;
        normal = default;
        inside = false;
        ShapeDoublePose inverse = Inverse(pose);
        ShapeDouble3 o = inverse.Rotate(origin.Subtract(pose.Position)), d = inverse.Rotate(direction);
        double r = cylinder.Radius, length = cylinder.Length;

        // The slab between the base and the top cap.
        double enter = double.NegativeInfinity, exit = double.PositiveInfinity, cap = d.Y > 0d ? -1d : 1d;
        if (!ClipRay(o.Y, d.Y, 0d, length, 0d, ref enter, ref exit)) return false;

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
        ShapeDouble3 hit = o.Add(d.Scale(tEnter));
        normal = pose.Rotate(side ? new ShapeDouble3(hit.X, 0d, hit.Z) : new ShapeDouble3(0d, cap, 0d));
        return true;
    }
    internal static bool CastMesh(ShapeDouble3 origin, ShapeDouble3 direction, double tStart, double tEnd, TriangleMeshShape mesh,
        ShapeDoublePose pose, out double t, out ShapeDouble3 normal)
        => CastMesh(origin, direction, tStart, tEnd, Centred(pose, mesh.Vertices), mesh.Indices, pose.Position, out t, out normal);

    internal static bool CastMesh(ShapeDouble3 origin, ShapeDouble3 direction, double tStart, double tEnd,
        ReadOnlySpan<ShapeDouble3> vertices, ReadOnlySpan<int> indices, ShapeDouble3 position, out double t, out ShapeDouble3 normal)
    {
        t = 0d;
        normal = default;
        ShapeDouble3 o = origin.Subtract(position);
        bool any = false;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            ShapeDouble3 a = vertices[indices[i]];
            ShapeDouble3 e1 = vertices[indices[i + 1]].Subtract(a), e2 = vertices[indices[i + 2]].Subtract(a);
            ShapeDouble3 p = ShapeDouble3.Cross(direction, e2);
            double determinant = Dot(e1, p);
            if (determinant == 0d) continue;
            double inverse = 1d / determinant;
            ShapeDouble3 s = o.Subtract(a);
            double u = Dot(s, p) * inverse;
            if (u < 0d || u > 1d) continue;
            ShapeDouble3 q = ShapeDouble3.Cross(s, e1);
            double v = Dot(direction, q) * inverse;
            if (v < 0d || u + v > 1d) continue;
            double hit = Dot(e2, q) * inverse;
            if (hit < tStart || hit > tEnd || (any && hit >= t)) continue;
            any = true;
            t = hit;
            normal = ShapeDouble3.Cross(e1, e2);
            if (Dot(normal, direction) > 0d) normal = normal.Scale(-1d);
        }
        return any;
    }
}
