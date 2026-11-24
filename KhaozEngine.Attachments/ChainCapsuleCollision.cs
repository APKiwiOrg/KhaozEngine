using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static partial class ChainCapsuleCollision
{
    internal const double TouchTolerance = 0.0001;

    internal static bool BeyondReach(Vector3 root, double contactReach, in ColliderCapsule capsule, in Axis axis)
    {
        var residual = axis.Residual(new Offset(root), default, out _);
        var coordinates = Vector3.Max(Vector3.Abs(root), Vector3.Max(Vector3.Abs(capsule.Start), Vector3.Abs(capsule.End)));
        // Keep axes within reach plus both radii, contact tolerance and 1e-9 of coordinate scale for ~1e-16 double roundoff.
        double projectionMargin = 1e-9 * Math.Max(coordinates.X, Math.Max(coordinates.Y, coordinates.Z));
        double limit = contactReach + capsule.Radius + projectionMargin;
        return residual.LengthSquared > limit * limit;
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

    internal readonly record struct Offset(double X, double Y, double Z)
    {
        internal Offset(Vector3 value) : this(value.X, value.Y, value.Z) { }

        internal double LengthSquared => Dot(this, this);
        internal Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);

        internal bool SameBits(Offset other) =>
            BitConverter.DoubleToInt64Bits(X) == BitConverter.DoubleToInt64Bits(other.X) &&
            BitConverter.DoubleToInt64Bits(Y) == BitConverter.DoubleToInt64Bits(other.Y) &&
            BitConverter.DoubleToInt64Bits(Z) == BitConverter.DoubleToInt64Bits(other.Z);

        internal static double Dot(Offset a, Offset b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static Offset Cross(Offset a, Offset b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static Offset operator +(Offset a, Offset b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Offset operator -(Offset a, Offset b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Offset operator *(Offset a, double factor) => new(a.X * factor, a.Y * factor, a.Z * factor);
    }
}
