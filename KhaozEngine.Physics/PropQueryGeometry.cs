using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>World axis-aligned bounds of a physics shape at a pose, computed in scalar double so every platform
/// agrees. The bounds follow the physics seam's placement convention: boxes, spheres and capsules are centred on
/// their pose, a cylinder stands on its base at its pose and spans <c>Length</c> along its local Y axis, hull and
/// mesh points are relative to their pose, and compound children compose their local pose with the parent's.</summary>
public static class PropQueryGeometry
{
    /// <summary>The tight world bounds of <paramref name="shape"/> placed at <paramref name="pose"/>.</summary>
    public static (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds(PhysicsShape shape, Pose pose)
        => Bounds(shape, ShapeDoublePose.From(pose));

    internal static (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) Bounds(PhysicsShape shape, ShapeDoublePose pose)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var box = ShapeBoundsAccumulator.Empty;
        Accumulate(shape, pose, ref box);
        return box.ToBounds();
    }

    static void Accumulate(PhysicsShape shape, ShapeDoublePose pose, ref ShapeBoundsAccumulator box)
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
                    ShapeDouble3 half = pose.Rotate(new ShapeDouble3(0, c.Length * 0.5, 0));
                    box.Add(pose.Position.Add(half), c.Radius, c.Radius, c.Radius);
                    box.Add(pose.Position.Subtract(half), c.Radius, c.Radius, c.Radius);
                    break;
                }
            case CylinderShape cy:
                {
                    // Base-aligned: the axis runs from the pose to pose + Length along local Y. A disc of radius r
                    // with axis a reaches r * sqrt(1 - a_i^2 / |a|^2) along world axis i. The sum of the other two
                    // squared components keeps a float orientation's rounding from inflating a near-zero extent.
                    ShapeDouble3 axis = pose.Rotate(new ShapeDouble3(0, 1, 0));
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
                throw new NotSupportedException($"Shape bounds do not support shape type {shape.GetType().Name}.");
        }
    }

    static void AddCentredExtents(ShapeDoublePose pose, Vector3 half, ref ShapeBoundsAccumulator box)
    {
        // Each world extent is the sum of the absolute rotated half extents.
        ShapeDouble3 x = pose.Rotate(new ShapeDouble3(half.X, 0, 0));
        ShapeDouble3 y = pose.Rotate(new ShapeDouble3(0, half.Y, 0));
        ShapeDouble3 z = pose.Rotate(new ShapeDouble3(0, 0, half.Z));
        box.Add(pose.Position,
            Math.Abs(x.X) + Math.Abs(y.X) + Math.Abs(z.X),
            Math.Abs(x.Y) + Math.Abs(y.Y) + Math.Abs(z.Y),
            Math.Abs(x.Z) + Math.Abs(y.Z) + Math.Abs(z.Z));
    }
    // Shared with stance candidates through the released MapDoc wrapper.
    internal const int CapSides = 32;
    const float VerticalTolerance = 1e-5f;

    internal static float RequiredRaise(PhysicsShape source, Pose pose, float minimumHeight)
    {
        var unrotated = Bounds(source, Pose.At(pose.Position));
        double height = unrotated.MaxY - unrotated.MinY;
        return (float)Math.Max(0d, minimumHeight - height);
    }

    /// <summary>Sweeps every member upward to the requested minimum height without changing the physical source.</summary>
    public static (PhysicsShape Shape, Pose Pose, float RaiseMetres) RaiseEnvelope(PhysicsShape source, Pose pose,
        float minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(minimumHeight) || minimumHeight < 0f)
            throw new ArgumentOutOfRangeException(nameof(minimumHeight));
        float raise = RequiredRaise(source, pose, minimumHeight);
        if (raise == 0f) return (source, pose, 0f);
        Vector3 up = Vector3.Transform(new Vector3(0f, raise, 0f), Quaternion.Conjugate(pose.Orientation));
        (PhysicsShape swept, Vector3 offset) = Sweep(source, up);
        var sweptPose = new Pose(pose.Position + Vector3.Transform(offset, pose.Orientation), pose.Orientation);
        return (swept, sweptPose, raise);
    }

    // Returns the swept shape and the offset its pose takes in the parent frame. Raise is world up in the shape frame.
    static (PhysicsShape Shape, Vector3 Offset) Sweep(PhysicsShape shape, Vector3 raise)
    {
        switch (shape)
        {
            case BoxShape b:
                return (Hull(Corners(b.HalfExtents), raise), Vector3.Zero);
            case ConvexHullShape h:
                return (Hull(h.Points, raise), Vector3.Zero);
            case TriangleMeshShape m:
                // A hull of the mesh is coarse, which is acceptable only because a raise applies to objects under
                // the minimum reach height.
                return (Hull(m.Vertices, raise), Vector3.Zero);
            case CylinderShape c:
                {
                    float length = raise.Length();
                    if (MathF.Abs(raise.X) > VerticalTolerance * length || MathF.Abs(raise.Z) > VerticalTolerance * length)
                        return (Hull(CircumscribedCaps(c, raise), raise), Vector3.Zero);
                    // The cylinder stands on its base. Pointing up it keeps its base and grows. Pointing down its base
                    // moves up by the raise.
                    return (new CylinderShape(c.Radius, c.Length + length), raise.Y < 0f ? raise : Vector3.Zero);
                }
            case CompoundShape co:
                {
                    var children = new CompoundChild[co.Children.Length];
                    for (int i = 0; i < children.Length; i++)
                    {
                        CompoundChild child = co.Children[i];
                        Quaternion orientation = child.Local.Orientation;
                        Vector3 childRaise = Vector3.Transform(raise, Quaternion.Conjugate(orientation));
                        (PhysicsShape swept, Vector3 offset) = Sweep(child.Shape, childRaise);
                        children[i] = new CompoundChild(swept,
                            new Pose(child.Local.Position + Vector3.Transform(offset, orientation), orientation));
                    }
                    return (new CompoundShape(children), Vector3.Zero);
                }
            default:
                throw new NotSupportedException($"Interaction envelope cannot sweep a {shape.GetType().Name}.");
        }
    }

    static Vector3[] Corners(Vector3 h) => new[]
    {
        new Vector3(-h.X, -h.Y, -h.Z), new Vector3(h.X, -h.Y, -h.Z), new Vector3(-h.X, h.Y, -h.Z), new Vector3(h.X, h.Y, -h.Z),
        new Vector3(-h.X, -h.Y, h.Z), new Vector3(h.X, -h.Y, h.Z), new Vector3(-h.X, h.Y, h.Z), new Vector3(h.X, h.Y, h.Z),
    };

    // A regular polygon circumscribing each end cap of a base-aligned cylinder, in the cylinder's frame, so the hull
    // of both contains the cylinder. One polygon edge faces world up and one faces world down, which keeps the hull's
    // world height equal to the cylinder's. Up is the raise direction in the cylinder's frame.
    static Vector3[] CircumscribedCaps(CylinderShape c, Vector3 up)
    {
        // The in-plane basis: u is world up projected onto the cap plane, v completes it around the local Y axis.
        double ux = up.X, uz = up.Z, norm = Math.Sqrt(ux * ux + uz * uz);
        ux /= norm;
        uz /= norm;
        double vx = uz, vz = -ux;
        double circumradius = c.Radius / Math.Cos(Math.PI / CapSides);
        var points = new Vector3[CapSides * 2];
        for (int k = 0; k < CapSides; k++)
        {
            // Vertices sit half a step off the basis, so edge normals fall on multiples of the step, including up.
            double angle = (k + 0.5) * 2 * Math.PI / CapSides;
            double a = circumradius * Math.Cos(angle), b = circumradius * Math.Sin(angle);
            float x = (float)(a * ux + b * vx), z = (float)(a * uz + b * vz);
            points[k] = new Vector3(x, 0f, z);
            points[CapSides + k] = new Vector3(x, c.Length, z);
        }
        return points;
    }

    static ConvexHullShape Hull(Vector3[] points, Vector3 raise)
    {
        var swept = new Vector3[points.Length * 2];
        for (int i = 0; i < points.Length; i++)
        {
            swept[i] = points[i];
            swept[points.Length + i] = points[i] + raise;
        }
        return new ConvexHullShape(swept);
    }
}
