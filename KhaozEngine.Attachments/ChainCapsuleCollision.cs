using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainCapsuleCollision
{
    private const int ContactPasses = 8;

    internal static Vector3 Clear(Vector3 direction, Vector3 start, float length,
        float chainRadius, ReadOnlySpan<ColliderCapsule> colliders)
    {
        var root = new Offset(start);
        // Revisit earlier contacts after a correction moves the particle along its length sphere.
        for (int pass = 0; pass < ContactPasses; pass++)
        {
            bool changed = false;
            foreach (ref readonly var capsule in colliders)
            {
                var fromRoot = new Offset(direction) * length;
                var residual = CapsuleResidual(root, fromRoot, capsule);
                double radius = (double)capsule.Radius + chainRadius;
                if (residual.LengthSquared >= radius * radius)
                    continue;

                var corrected = ClearSphere(direction, length, fromRoot - residual, radius);
                changed |= corrected != direction;
                direction = corrected;
            }

            if (!changed)
                break;
        }

        return direction;
    }

    private static Offset CapsuleResidual(Offset root, Offset tipFromRoot, in ColliderCapsule capsule)
    {
        var start = new Offset(capsule.Start);
        var end = new Offset(capsule.End);
        var axis = end - start;
        double squared = axis.LengthSquared;
        var fromStart = root - start + tipFromRoot;
        if (squared == 0)
            return fromStart;

        double tipProjection = Offset.Dot(tipFromRoot, axis);
        if (Offset.Dot(root - start, axis) + tipProjection <= 0)
            return fromStart;
        var rootFromEnd = root - end;
        if (Offset.Dot(rootFromEnd, axis) + tipProjection >= 0)
            return rootFromEnd + tipFromRoot;

        // For the barrel use d cross (p - a), expanded as d cross root + a cross b
        // + d cross tipFromRoot. This retains local offsets even when the axis is very long.
        var crossed = Offset.Cross(axis, root) + Offset.Cross(start, end) + Offset.Cross(axis, tipFromRoot);
        return Offset.Cross(crossed, axis) * (1 / squared);
    }

    private static Vector3 ClearSphere(Vector3 direction, float length, Offset center, double radius)
    {
        double distance = Math.Sqrt(center.LengthSquared);
        if (distance == 0)
            return direction;

        // Move along the length sphere to the contact circle. Contact may exceed the
        // bend limit since clamping the bend again would put the endpoint inside.
        var axis = (center * (1 / distance)).ToVector3();
        double boundary = ((double)length * length + (distance - radius) * (distance + radius)) / (2 * length * distance);
        double cosine = Math.Clamp(boundary, -1, 1);
        double sine = Math.Sqrt(1 - cosine * cosine);
        var tangent = ChainMath.Tangent(direction, axis);
        if (tangent == Vector3.Zero)
            tangent = ChainMath.Perpendicular(axis);
        return (float)cosine * axis + (float)sine * tangent;
    }

    private readonly record struct Offset(double X, double Y, double Z)
    {
        internal Offset(Vector3 value) : this(value.X, value.Y, value.Z) { }

        internal double LengthSquared => Dot(this, this);
        internal Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

        internal static double Dot(Offset a, Offset b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static Offset Cross(Offset a, Offset b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static Offset operator +(Offset a, Offset b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Offset operator -(Offset a, Offset b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Offset operator *(Offset a, double factor) => new(a.X * factor, a.Y * factor, a.Z * factor);
    }
}
