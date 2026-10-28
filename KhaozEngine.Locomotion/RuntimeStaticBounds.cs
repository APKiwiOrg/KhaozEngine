using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Outward bounds over represented shape placement. Compound flattening retains each
/// rounded Pose.Compose result before enclosing backend root/leaf composition.</summary>
internal static class RuntimeStaticBounds
{
    internal static (Vector3 Minimum, Vector3 Maximum) Capture(PhysicsShape shape, Pose local, Vector3 origin)
    {
        RuntimeStaticGeometry.ValidatePose(local);
        Box bounds = Capture(shape, Pose.Identity, local);
        bounds = bounds.Add(Box.Point(origin));
        Vector3 minimum = new(bounds.X.Lower, bounds.Y.Lower, bounds.Z.Lower);
        Vector3 maximum = new(bounds.X.Upper, bounds.Y.Upper, bounds.Z.Upper);
        OrdinaryMovementExtent.ValidateBounds(minimum, maximum);
        return (minimum, maximum);
    }

    static Box Capture(PhysicsShape shape, Pose child, Pose root)
    {
        RuntimeStaticGeometry.ValidatePose(child);
        if (shape is CompoundShape compound)
        {
            Box? result = null;
            foreach (CompoundChild part in compound.Children)
            {
                Box next = Capture(part.Shape, child.Compose(part.Local), root);
                result = result.HasValue ? result.Value.Union(next) : next;
            }
            return result!.Value;
        }
        Box geometry;
        switch (shape)
        {
            case SphereShape sphere:
                return PlaceRounded(Box.Symmetric(new(sphere.Radius)), Box.Point(Vector3.Zero), sphere.Radius, child, root);
            case BoxShape box:
                geometry = Box.Symmetric(box.HalfExtents);
                break;
            case CapsuleShape capsule:
                Range height = Range.Enclose(-(double)capsule.Length * 0.5 - capsule.Radius,
                    (double)capsule.Length * 0.5 + capsule.Radius);
                geometry = new(new(-capsule.Radius, capsule.Radius), height, new(-capsule.Radius, capsule.Radius));
                Range segment = Range.Enclose(-(double)capsule.Length * 0.5, (double)capsule.Length * 0.5);
                return PlaceRounded(geometry, new(new(0, 0), segment, new(0, 0)), capsule.Radius, child, root);
            case CylinderShape cylinder:
                // ShapeFactory adds the base lift to the rounded flattened child pose.
                child = new(child.Position + Vector3.Transform(new(0, cylinder.Length * 0.5f, 0), child.Orientation), child.Orientation);
                geometry = Box.Symmetric(new(cylinder.Radius, cylinder.Length * 0.5f, cylinder.Radius));
                break;
            case ConvexHullShape hull:
                // The backend subtracts a computed centre from vertices and adds its rotated
                // value to the child pose. Retain both roundings, with no assumed cancellation.
                float magnitude = RuntimeStaticHullBounds.CentreMagnitude(hull.Points);
                Box centre = Box.Symmetric(new(magnitude));
                Box offset = Box.Point(child.Position).Add(centre.Transform(child.Orientation));
                return Place(Points(hull.Points).Subtract(centre), offset, child.Orientation, root);
            case TriangleMeshShape mesh:
                return CaptureMesh(mesh, child, root);
            default:
                throw new NotSupportedException("The runtime static bounds are not supported.");
        }
        return Place(geometry, Box.Point(child.Position), child.Orientation, root);
    }

    static Box PlaceRounded(Box geometry, Box segment, float radius, Pose child, Pose root)
    {
        // Native sphere and capsule radii do not rotate or contract. Retain the represented
        // affine query geometry too, since an admitted quaternion need not be exactly orthogonal.
        Box position = Box.Point(child.Position);
        Box native = Place(segment, position, child.Orientation, root).Add(Box.Symmetric(new(radius)));
        return native.Union(Place(geometry, position, child.Orientation, root));
    }

    static Box CaptureMesh(TriangleMeshShape mesh, Pose child, Pose root)
    {
        Box? result = null;
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            Vector3 a = mesh.Vertices[mesh.Indices[i]], b = mesh.Vertices[mesh.Indices[i + 1]], c = mesh.Vertices[mesh.Indices[i + 2]];
            Box raw = Place(Points([a, b, c]), Box.Point(child.Position), child.Orientation, root);
            // Native mesh sweeps recenter each referenced triangle with this rounded graph.
            // Keep the subtracted vertices and composed centroid pose as a second representation.
            Vector3 centre = (a + b + c) * (1f / 3f);
            Box offset = Box.Point(child.Position).Add(Box.Point(centre).Transform(child.Orientation));
            Box native = Place(Points([a - centre, b - centre, c - centre]), offset, child.Orientation, root);
            Box triangle = raw.Union(native);
            result = result.HasValue ? result.Value.Union(triangle) : triangle;
        }
        return result!.Value;
    }

    static Box Place(Box geometry, Box childPosition, Quaternion childOrientation, Pose root)
    {
        Box position = childPosition.Transform(root.Orientation).Add(Box.Point(root.Position));
        Rotation orientation = Rotation.Compose(childOrientation, root.Orientation);
        return geometry.Transform(orientation).Add(position);
    }

    static Box Points(ReadOnlySpan<Vector3> points)
    {
        Vector3 min = points[0], max = min;
        foreach (Vector3 point in points) { min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
        return new(new(min.X, max.X), new(min.Y, max.Y), new(min.Z, max.Z));
    }

    readonly record struct Box(Range X, Range Y, Range Z)
    {
        internal static Box Point(Vector3 p) => new(new(p.X, p.X), new(p.Y, p.Y), new(p.Z, p.Z));
        internal static Box Symmetric(Vector3 p) => new(new(-p.X, p.X), new(-p.Y, p.Y), new(-p.Z, p.Z));
        internal Box Union(Box b) => new(X.Union(b.X), Y.Union(b.Y), Z.Union(b.Z));
        internal Box Add(Box b) => new(X.Add(b.X), Y.Add(b.Y), Z.Add(b.Z));
        internal Box Subtract(Box b) => new(X.Subtract(b.X), Y.Subtract(b.Y), Z.Subtract(b.Z));
        internal Box Transform(Quaternion q)
            => Transform(Rotation.Exact(q));

        internal Box Transform(Rotation q)
        {
            if (q == Rotation.Exact(Quaternion.Identity)) return this;
            Range x = q.X, y = q.Y, z = q.Z, w = q.W;
            Range two = new(2, 2), one = new(1, 1);
            Range xx = x.Multiply(two).Multiply(x), yy = y.Multiply(two).Multiply(y), zz = z.Multiply(two).Multiply(z);
            Range xy = x.Multiply(two).Multiply(y), xz = x.Multiply(two).Multiply(z), yz = y.Multiply(two).Multiply(z);
            Range wx = w.Multiply(two).Multiply(x), wy = w.Multiply(two).Multiply(y), wz = w.Multiply(two).Multiply(z);
            return new(Dot(Diagonal(one, yy, zz), xy.Subtract(wz), xz.Add(wy)),
                Dot(xy.Add(wz), Diagonal(one, xx, zz), yz.Subtract(wx)),
                Dot(xz.Subtract(wy), yz.Add(wx), Diagonal(one, xx, yy)));
        }
        static Range Diagonal(Range one, Range a, Range b) => one.Subtract(a.Add(b)).Union(one.Subtract(a).Subtract(b));
        Range Dot(Range a, Range b, Range c)
        {
            Range x = X.Multiply(a), y = Y.Multiply(b), z = Z.Multiply(c);
            return x.Add(y).Add(z).Union(x.Add(z).Add(y)).Union(y.Add(z).Add(x));
        }
    }

    readonly record struct Rotation(Range X, Range Y, Range Z, Range W)
    {
        internal static Rotation Exact(Quaternion q) => new(new(q.X, q.X), new(q.Y, q.Y), new(q.Z, q.Z), new(q.W, q.W));
        internal static Rotation Compose(Quaternion child, Quaternion root)
        {
            Rotation a = Exact(child), b = Exact(root);
            return new(a.W.Multiply(b.X).Add(a.X.Multiply(b.W)).Add(a.Z.Multiply(b.Y)).Subtract(a.Y.Multiply(b.Z)),
                a.W.Multiply(b.Y).Add(a.Y.Multiply(b.W)).Add(a.X.Multiply(b.Z)).Subtract(a.Z.Multiply(b.X)),
                a.W.Multiply(b.Z).Add(a.Z.Multiply(b.W)).Add(a.Y.Multiply(b.X)).Subtract(a.X.Multiply(b.Y)),
                a.W.Multiply(b.W).Subtract(a.X.Multiply(b.X)).Subtract(a.Y.Multiply(b.Y)).Subtract(a.Z.Multiply(b.Z)));
        }
    }

    readonly record struct Range(float Lower, float Upper)
    {
        internal Range Union(Range b) => new(MathF.Min(Lower, b.Lower), MathF.Max(Upper, b.Upper));
        internal Range Add(Range b) => new(Sum(Lower, b.Lower, false), Sum(Upper, b.Upper, true));
        internal Range Subtract(Range b) => new(Sum(Lower, -b.Upper, false), Sum(Upper, -b.Lower, true));
        static float Sum(float a, float b, bool upper)
        {
            double sum = (double)a + b;
            double partial = sum - a;
            double residual = (a - (sum - partial)) + (b - partial);
            float result = (float)sum;
            if (upper && (result < sum || result == sum && residual > 0)) result = MathF.BitIncrement(result);
            if (!upper && (result > sum || result == sum && residual < 0)) result = MathF.BitDecrement(result);
            RuntimeStaticGeometry.Require(float.IsFinite(result));
            return result;
        }
        internal Range Multiply(Range b)
        {
            double a = (double)Lower * b.Lower, c = (double)Lower * b.Upper;
            double d = (double)Upper * b.Lower, e = (double)Upper * b.Upper;
            return Enclose(Math.Min(Math.Min(a, c), Math.Min(d, e)), Math.Max(Math.Max(a, c), Math.Max(d, e)));
        }
        internal static Range Enclose(double lower, double upper)
        {
            float lo = (float)lower, hi = (float)upper;
            if (lo > lower) lo = MathF.BitDecrement(lo);
            if (hi < upper) hi = MathF.BitIncrement(hi);
            RuntimeStaticGeometry.Require(float.IsFinite(lo) && float.IsFinite(hi));
            return new(lo, hi);
        }
    }
}
