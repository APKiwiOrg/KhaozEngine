using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Detached, validated descriptors. Never retains a caller's mutable shape storage.</summary>
internal static class RuntimeStaticGeometry
{
    internal static PhysicsShape Copy(PhysicsShape shape) => Copy(shape, new(ReferenceEqualityComparer.Instance), 0);

    static PhysicsShape Copy(PhysicsShape shape, HashSet<PhysicsShape> ancestors, int depth)
    {
        ArgumentNullException.ThrowIfNull(shape);
        // Bound recursive work before descending. This is a descriptor resource limit, not a geometric tolerance.
        Require(depth < 32 && ancestors.Add(shape));
        try
        {
            switch (shape)
            {
                case SphereShape sphere:
                    Positive(sphere.Radius);
                    return new SphereShape(sphere.Radius);
                case BoxShape box:
                    Positive(box.HalfExtents.X); Positive(box.HalfExtents.Y); Positive(box.HalfExtents.Z);
                    Require(MovementEnvironmentValidation.Finite(box.HalfExtents * 2));
                    return new BoxShape(box.HalfExtents);
                case CapsuleShape capsule:
                    Positive(capsule.Radius); Nonnegative(capsule.Length);
                    Require(float.IsFinite(capsule.Length + capsule.Radius * 2));
                    return new CapsuleShape(capsule.Radius, capsule.Length);
                case CylinderShape cylinder:
                    Positive(cylinder.Radius); Positive(cylinder.Length);
                    return new CylinderShape(cylinder.Radius, cylinder.Length);
                case ConvexHullShape hull:
                    Vector3[] points = CopyPoints(hull.Points);
                    Require(points.Length >= 4 && points.Length <= ushort.MaxValue);
                    _ = RuntimeStaticHullBounds.CentreMagnitude(points);
                    return new ConvexHullShape(points);
                case TriangleMeshShape mesh:
                    Vector3[] vertices = CopyPoints(mesh.Vertices);
                    ArgumentNullException.ThrowIfNull(mesh.Indices);
                    int[] indices = (int[])mesh.Indices.Clone();
                    Require(vertices.Length >= 3 && indices.Length > 0 && indices.Length % 3 == 0);
                    foreach (int index in indices) Require(index >= 0 && index < vertices.Length);
                    return new TriangleMeshShape(vertices, indices);
                case CompoundShape compound:
                    ArgumentNullException.ThrowIfNull(compound.Children);
                    Require(compound.Children.Length > 0);
                    var children = new CompoundChild[compound.Children.Length];
                    for (int i = 0; i < children.Length; i++)
                    {
                        CompoundChild child = compound.Children[i];
                        ValidatePose(child.Local);
                        if (child.Shape is TriangleMeshShape)
                            throw new NotSupportedException("A mesh cannot be a convex compound child.");
                        children[i] = new(Copy(child.Shape, ancestors, depth + 1), child.Local);
                    }
                    return new CompoundShape(children);
                default:
                    throw new NotSupportedException("The runtime static shape is not supported.");
            }
        }
        finally { ancestors.Remove(shape); }
    }

    static Vector3[] CopyPoints(Vector3[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copy = (Vector3[])source.Clone();
        foreach (Vector3 point in copy) Require(MovementEnvironmentValidation.Finite(point));
        return copy;
    }

    internal static void ValidatePose(Pose pose)
    {
        Quaternion q = pose.Orientation;
        double norm = (double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W;
        // Match the installed-geometry rigid quaternion domain. Do not normalize a caller's bits.
        Require(MovementEnvironmentValidation.Finite(pose.Position) && double.IsFinite(norm) &&
            RigidNorm(q, norm));
    }

    static bool RigidNorm(Quaternion q, double norm)
    {
        const double lower = 1d - 1d / (1 << 20), upper = 1d + 1d / (1 << 20);
        if (norm > lower && norm < upper) return true;
        if (norm != lower && norm != upper) return false;
        // Closed endpoints need the exact dyadic sum. A tiny represented component can
        // exceed the band even when binary64 summation rounds back onto its endpoint.
        BigInteger squared = BigInteger.Zero;
        ReadOnlySpan<float> components = [q.X, q.Y, q.Z, q.W];
        foreach (float component in components)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(component) & 0x7fffffffu;
            int exponent = (int)(bits >> 23);
            uint mantissa = bits & 0x7fffffu;
            if (exponent != 0) mantissa |= 0x800000u;
            int shift = exponent == 0 ? 0 : 2 * (exponent - 150) + 298;
            squared += ((BigInteger)mantissa * mantissa) << shift;
        }
        BigInteger one = BigInteger.One << 298, band = BigInteger.One << 278;
        return squared >= one - band && squared <= one + band;
    }

    static void Positive(float value) => Require(float.IsFinite(value) && value > 0);
    static void Nonnegative(float value) => Require(float.IsFinite(value) && value >= 0);
    internal static void Require(bool valid) => MovementEnvironmentValidation.Require(valid, "geometry");
}
