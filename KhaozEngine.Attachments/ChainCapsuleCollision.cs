using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainCapsuleCollision
{
    private const int ContactPasses = 8;

    internal static Vector3 Clear(Vector3 direction, Vector3 start, float length,
        float chainRadius, ReadOnlySpan<ColliderCapsule> colliders)
    {
        // Revisit earlier contacts after a correction moves the particle along its length sphere.
        for (int pass = 0; pass < ContactPasses; pass++)
        {
            bool changed = false;
            foreach (ref readonly var capsule in colliders)
            {
                var offset = (
                    X: (double)length * direction.X,
                    Y: (double)length * direction.Y,
                    Z: (double)length * direction.Z);
                var center = ClosestOnAxis(offset, start, capsule);
                double radius = (double)capsule.Radius + chainRadius;
                double x = offset.X - center.X;
                double y = offset.Y - center.Y;
                double z = offset.Z - center.Z;
                if (x * x + y * y + z * z >= radius * radius)
                    continue;

                var corrected = ClearSphere(direction, length, center, radius);
                changed |= corrected != direction;
                direction = corrected;
            }

            if (!changed)
                break;
        }

        return direction;
    }

    private static (double X, double Y, double Z) ClosestOnAxis(
        (double X, double Y, double Z) particle, Vector3 root, in ColliderCapsule capsule)
    {
        double axisX = (double)capsule.End.X - capsule.Start.X;
        double axisY = (double)capsule.End.Y - capsule.Start.Y;
        double axisZ = (double)capsule.End.Z - capsule.Start.Z;
        // Use the capsule midpoint and root-relative coordinates to retain small offsets
        // when a long capsule crosses the chain.
        double midX = 0.5 * ((double)capsule.Start.X + capsule.End.X) - root.X;
        double midY = 0.5 * ((double)capsule.Start.Y + capsule.End.Y) - root.Y;
        double midZ = 0.5 * ((double)capsule.Start.Z + capsule.End.Z) - root.Z;
        double squared = axisX * axisX + axisY * axisY + axisZ * axisZ;
        if (squared == 0)
            return (midX, midY, midZ);

        double projection = (particle.X - midX) * axisX + (particle.Y - midY) * axisY + (particle.Z - midZ) * axisZ;
        double fraction = Math.Clamp(projection / squared, -0.5, 0.5);
        return (midX + fraction * axisX, midY + fraction * axisY, midZ + fraction * axisZ);
    }

    private static Vector3 ClearSphere(Vector3 direction, float length, (double X, double Y, double Z) center, double radius)
    {
        double distance = Math.Sqrt(center.X * center.X + center.Y * center.Y + center.Z * center.Z);
        if (distance == 0)
            return direction;

        // Move along the length sphere to the contact circle. Contact may exceed the
        // bend limit since clamping the bend again would put the endpoint inside.
        var axis = new Vector3((float)(center.X / distance), (float)(center.Y / distance), (float)(center.Z / distance));
        double boundary = ((double)length * length + (distance - radius) * (distance + radius)) / (2 * length * distance);
        double cosine = Math.Clamp(boundary, -1, 1);
        double sine = Math.Sqrt(1 - cosine * cosine);
        var tangent = ChainMath.Tangent(direction, axis);
        if (tangent == Vector3.Zero)
            tangent = ChainMath.Perpendicular(axis);
        return (float)cosine * axis + (float)sine * tangent;
    }
}
