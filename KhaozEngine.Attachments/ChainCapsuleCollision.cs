using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainCapsuleCollision
{
    private const int ContactPasses = 8;
    private const double TouchTolerance = 0.0001;

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
                var residual = CapsuleResidual(root, fromRoot, capsule, out _);
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

    internal static bool TryContact(Vector3 root, float length, Vector3 direction, float chainRadius,
        in ColliderCapsule capsule, out Vector3 normal, out double along)
    {
        var residual = CapsuleResidual(new Offset(root), new Offset(direction) * length, capsule, out along);
        double squared = residual.LengthSquared;
        double radius = capsule.Radius + (double)chainRadius + TouchTolerance;
        if (squared == 0 || squared > radius * radius)
        {
            normal = Vector3.Zero;
            return false;
        }

        normal = (residual * (1 / Math.Sqrt(squared))).ToVector3();
        return true;
    }

    internal static Vector3 SurfaceVelocity(in ColliderCapsule capsule, in ColliderCapsule previous,
        double along, float seconds)
    {
        // Compare the same material point on the axis. Endpoint deltas avoid cancellation on long axes.
        var startMotion = new Offset(capsule.Start) - new Offset(previous.Start);
        var endMotion = new Offset(capsule.End) - new Offset(previous.End);
        return ((startMotion * (1 - along) + endMotion * along) * (1d / seconds)).ToVector3();
    }

    private static Offset CapsuleResidual(Offset root, Offset tipFromRoot, in ColliderCapsule capsule, out double along)
    {
        var start = new Offset(capsule.Start);
        var end = new Offset(capsule.End);
        var axis = end - start;
        double squared = axis.LengthSquared;
        var fromStart = root - start + tipFromRoot;
        along = 0;
        if (squared == 0)
            return fromStart;

        double tipProjection = Offset.Dot(tipFromRoot, axis);
        double axisProjection = Offset.Dot(root - start, axis) + tipProjection;
        if (axisProjection <= 0)
            return fromStart;
        var rootFromEnd = root - end;
        if (Offset.Dot(rootFromEnd, axis) + tipProjection >= 0)
        {
            along = 1;
            return rootFromEnd + tipFromRoot;
        }

        double inverseSquared = 1 / squared;
        along = axisProjection * inverseSquared;

        // For the barrel use d cross (p - a), expanded as d cross root + a cross b
        // + d cross tipFromRoot. This retains local offsets even when the axis is very long.
        var crossed = Offset.Cross(axis, root) + Offset.Cross(start, end) + Offset.Cross(axis, tipFromRoot);
        return Offset.Cross(crossed, axis) * inverseSquared;
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
