using System;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.Physics;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>World axis-aligned bounds of a physics shape at a pose, computed in scalar double so every platform
/// agrees. The bounds follow the physics seam's placement convention: boxes, spheres and capsules are centred on
/// their pose, a cylinder stands on its base at its pose and spans <c>Length</c> along its local Y axis, hull and
/// mesh points are relative to their pose, and compound children compose their local pose with the parent's.</summary>
public static class MapShapeBounds
{
    /// <summary>The tight world bounds of <paramref name="shape"/> placed at <paramref name="pose"/>.</summary>
    public static MapBox3 Of(PhysicsShape shape, Pose pose)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var box = MapBoundsAccumulator.Empty;
        Accumulate(shape, MapDoublePose.From(pose), ref box);
        return box.ToBox();
    }

    static void Accumulate(PhysicsShape shape, MapDoublePose pose, ref MapBoundsAccumulator box)
    {
        switch (shape)
        {
            case BoxShape b:
                AddCentredExtents(pose, b.HalfExtents, ref box);
                break;
            case SphereShape s:
                box.Add(pose.Position, s.Radius, s.Radius, s.Radius);
                break;
            case CapsuleShape c:
                {
                    MapDouble3 half = pose.Rotate(new MapDouble3(0, c.Length * 0.5, 0));
                    box.Add(pose.Position.Add(half), c.Radius, c.Radius, c.Radius);
                    box.Add(pose.Position.Subtract(half), c.Radius, c.Radius, c.Radius);
                    break;
                }
            case CylinderShape cy:
                {
                    // Base-aligned: the axis runs from the pose to pose + Length along local Y. A disc of radius r
                    // with axis a reaches r * sqrt(1 - a_i^2 / |a|^2) along world axis i. The sum of the other two
                    // squared components keeps a float orientation's rounding from inflating a near-zero extent.
                    MapDouble3 axis = pose.Rotate(new MapDouble3(0, 1, 0));
                    double r = cy.Radius;
                    double xx = axis.X * axis.X, yy = axis.Y * axis.Y, zz = axis.Z * axis.Z, n = xx + yy + zz;
                    double ex = r * Math.Sqrt((yy + zz) / n);
                    double ey = r * Math.Sqrt((xx + zz) / n);
                    double ez = r * Math.Sqrt((xx + yy) / n);
                    box.Add(pose.Position, ex, ey, ez);
                    box.Add(pose.Position.Add(axis.Scale(cy.Length)), ex, ey, ez);
                    break;
                }
            case ConvexHullShape h:
                foreach (Vector3 point in h.Points) box.Add(pose.Transform(point), 0, 0, 0);
                break;
            case TriangleMeshShape m:
                foreach (Vector3 vertex in m.Vertices) box.Add(pose.Transform(vertex), 0, 0, 0);
                break;
            case CompoundShape co:
                foreach (CompoundChild child in co.Children) Accumulate(child.Shape, pose.Compose(child.Local), ref box);
                break;
            default:
                throw new MapDocumentException($"Shape bounds do not support shape type {shape.GetType().Name}.");
        }
    }

    static void AddCentredExtents(MapDoublePose pose, Vector3 half, ref MapBoundsAccumulator box)
    {
        // Each world extent is the sum of the absolute rotated half extents.
        MapDouble3 x = pose.Rotate(new MapDouble3(half.X, 0, 0));
        MapDouble3 y = pose.Rotate(new MapDouble3(0, half.Y, 0));
        MapDouble3 z = pose.Rotate(new MapDouble3(0, 0, half.Z));
        box.Add(pose.Position,
            Math.Abs(x.X) + Math.Abs(y.X) + Math.Abs(z.X),
            Math.Abs(x.Y) + Math.Abs(y.Y) + Math.Abs(z.Y),
            Math.Abs(x.Z) + Math.Abs(y.Z) + Math.Abs(z.Z));
    }
}

/// <summary>A double-precision vector for the envelope math.</summary>
internal readonly record struct MapDouble3(double X, double Y, double Z)
{
    internal static MapDouble3 From(Vector3 v) => new(v.X, v.Y, v.Z);
    internal MapDouble3 Add(MapDouble3 o) => new(X + o.X, Y + o.Y, Z + o.Z);
    internal MapDouble3 Subtract(MapDouble3 o) => new(X - o.X, Y - o.Y, Z - o.Z);
    internal MapDouble3 Scale(double s) => new(X * s, Y * s, Z * s);
    internal static MapDouble3 Cross(MapDouble3 a, MapDouble3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

/// <summary>A rigid pose in double precision. Orientations compose as parent times child, matching the physics
/// seam's compound flattening.</summary>
internal readonly record struct MapDoublePose(MapDouble3 Position, double Qx, double Qy, double Qz, double Qw)
{
    internal static MapDoublePose From(Pose pose) => new(MapDouble3.From(pose.Position),
        pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W);

    internal MapDouble3 Rotate(MapDouble3 v)
    {
        var q = new MapDouble3(Qx, Qy, Qz);
        MapDouble3 t = MapDouble3.Cross(q, v).Scale(2);
        return v.Add(t.Scale(Qw)).Add(MapDouble3.Cross(q, t));
    }

    internal MapDouble3 Transform(Vector3 local) => Position.Add(Rotate(MapDouble3.From(local)));

    internal MapDoublePose Compose(Pose local)
    {
        double bx = local.Orientation.X, by = local.Orientation.Y, bz = local.Orientation.Z, bw = local.Orientation.W;
        return new(Transform(local.Position),
            Qw * bx + Qx * bw + Qy * bz - Qz * by,
            Qw * by - Qx * bz + Qy * bw + Qz * bx,
            Qw * bz + Qx * by - Qy * bx + Qz * bw,
            Qw * bw - Qx * bx - Qy * by - Qz * bz);
    }
}

/// <summary>A running min and max over points with per-axis padding.</summary>
internal struct MapBoundsAccumulator
{
    double _minX, _minY, _minZ, _maxX, _maxY, _maxZ;

    internal static MapBoundsAccumulator Empty => new()
    {
        _minX = double.PositiveInfinity,
        _minY = double.PositiveInfinity,
        _minZ = double.PositiveInfinity,
        _maxX = double.NegativeInfinity,
        _maxY = double.NegativeInfinity,
        _maxZ = double.NegativeInfinity,
    };

    internal void Add(MapDouble3 p, double ex, double ey, double ez)
    {
        _minX = Math.Min(_minX, p.X - ex);
        _minY = Math.Min(_minY, p.Y - ey);
        _minZ = Math.Min(_minZ, p.Z - ez);
        _maxX = Math.Max(_maxX, p.X + ex);
        _maxY = Math.Max(_maxY, p.Y + ey);
        _maxZ = Math.Max(_maxZ, p.Z + ez);
    }

    internal readonly MapBox3 ToBox()
    {
        if (!(_minX <= _maxX && _minY <= _maxY && _minZ <= _maxZ) ||
            !double.IsFinite(_minX) || !double.IsFinite(_minY) || !double.IsFinite(_minZ) ||
            !double.IsFinite(_maxX) || !double.IsFinite(_maxY) || !double.IsFinite(_maxZ))
            throw new MapDocumentException("Shape bounds are empty or not finite.");
        return new MapBox3(_minX, _minY, _minZ, _maxX, _maxY, _maxZ);
    }
}
