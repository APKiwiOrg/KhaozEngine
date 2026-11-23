using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainCapsuleCollision
{
    private const int ContactPasses = 8;
    internal const double TouchTolerance = 0.0001;

    [ThreadStatic]
    private static Vector3? _firstCorrectionDirectionForTests;

    internal static void SetFirstCorrectionDirectionForTests(Vector3? direction) =>
        _firstCorrectionDirectionForTests = direction;

    internal static bool BeyondReach(Vector3 root, double contactReach, in ColliderCapsule capsule)
    {
        var residual = CapsuleResidual(new Offset(root), default, capsule, out _);
        var coordinates = Vector3.Max(Vector3.Abs(root), Vector3.Max(Vector3.Abs(capsule.Start), Vector3.Abs(capsule.End)));
        // Keep axes within reach plus both radii, contact tolerance and 1e-9 of coordinate scale for ~1e-16 double roundoff.
        double projectionMargin = 1e-9 * Math.Max(coordinates.X, Math.Max(coordinates.Y, coordinates.Z));
        double limit = contactReach + capsule.Radius + projectionMargin;
        return residual.LengthSquared > limit * limit;
    }

    internal static Vector3 Clear(Vector3 direction, Vector3 start, float length,
        float chainRadius, ChainCapsuleCandidates capsules, out bool replayedAll)
    {
        replayedAll = capsules.IsFiltered && !ChainCapsuleCull.FitsReach(direction);
        if (replayedAll)
            capsules = capsules.All;
        var initial = direction;
        var firstCorrectionForTests = _firstCorrectionDirectionForTests;
        var root = new Offset(start);
        // Revisit earlier contacts after a correction moves the particle along its length sphere.
        for (int pass = 0; pass < ContactPasses; pass++)
        {
            bool changed = false;
            for (int index = 0; index < capsules.Count; index++)
            {
                ref readonly var capsule = ref capsules[index];
                var fromRoot = new Offset(direction) * length;
                var residual = CapsuleResidual(root, fromRoot, capsule, out _);
                double radius = (double)capsule.Radius + chainRadius;
                if (residual.LengthSquared >= radius * radius)
                    continue;

                var corrected = firstCorrectionForTests ?? ClearSphere(direction, length, fromRoot - residual, radius);
                firstCorrectionForTests = null;
                // A correction outside padded reach requires a full replay before the next position test.
                if (capsules.IsFiltered && !ChainCapsuleCull.FitsReach(corrected))
                {
                    replayedAll = true;
                    return Clear(initial, start, length, chainRadius, capsules.All, out _);
                }
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
        double along, Vector3 normal, in ChainCarry carry, float seconds)
    {
        // Compare the same material point on the axis. Endpoint deltas avoid cancellation on long axes.
        var previousStart = new Offset(previous.Start);
        var previousEnd = new Offset(previous.End);
        var startMotion = new Offset(capsule.Start) - previousStart;
        var endMotion = new Offset(capsule.End) - previousEnd;
        var motion = startMotion * (1 - along) + endMotion * along - new Offset(carry.Offset);
        if (carry.Turn != Quaternion.Identity)
        {
            var pivot = new Offset(carry.Pivot);
            var fromPivot = (previousStart - pivot) * (1 - along) + (previousEnd - pivot) * along;
            var axis = new Offset(carry.Turn.X, carry.Turn.Y, carry.Turn.Z);
            // Subtract the quaternion's displacement without differencing distant rotated points.
            var twiceCross = Offset.Cross(axis, fromPivot) * 2;
            motion -= twiceCross * carry.Turn.W + Offset.Cross(axis, twiceCross);
        }

        motion += new Offset(normal) * ((double)capsule.Radius - previous.Radius);
        return (motion * (1d / seconds)).ToVector3();
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
