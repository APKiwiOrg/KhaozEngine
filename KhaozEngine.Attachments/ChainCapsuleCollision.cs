using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainCapsuleCollision
{
    private const int ContactPasses = 8;

    internal static Vector3 Clear(Vector3 direction, Vector3 rest, float bendCosine, Vector3 start, float length,
        float chainRadius, ReadOnlySpan<ColliderCapsule> colliders)
    {
        // Revisit earlier contacts after a correction moves the particle along its length sphere.
        for (int pass = 0; pass < ContactPasses; pass++)
        {
            bool changed = false;
            foreach (ref readonly var capsule in colliders)
            {
                var position = start + length * direction;
                var axis = ClosestOnAxis(position, capsule);
                float radius = capsule.Radius + chainRadius;
                if (ChainMath.DistanceSquared(position, axis) >= (double)radius * radius)
                    continue;

                var corrected = ClearSphere(direction, rest, bendCosine, start, length, axis, radius);
                changed |= corrected != direction;
                direction = corrected;
            }

            if (!changed)
                break;
        }

        return direction;
    }

    private static Vector3 ClosestOnAxis(Vector3 position, in ColliderCapsule capsule)
    {
        var axis = capsule.End - capsule.Start;
        float squared = axis.LengthSquared();
        if (squared == 0)
            return capsule.Start;

        float fraction = Math.Clamp(Vector3.Dot(position - capsule.Start, axis) / squared, 0, 1);
        return capsule.Start + fraction * axis;
    }

    private static Vector3 ClearSphere(Vector3 direction, Vector3 rest, float bendCosine,
        Vector3 start, float length, Vector3 center, float radius)
    {
        double distance = Math.Sqrt(ChainMath.DistanceSquared(center, start));
        if (distance == 0)
            return direction;

        // The length sphere intersects this contact sphere at a circle. Slide to that
        // circle so enforcing length cannot move the particle back inside the collider.
        var axis = ChainMath.Direction(center - start, direction);
        double boundary = ((double)length * length + distance * distance - (double)radius * radius) /
            (2 * length * distance);
        float contactCosine = (float)Math.Clamp(boundary, -1, 1);
        float contactSine = MathF.Sqrt(1 - contactCosine * contactCosine);
        var tangent = ChainMath.Tangent(direction, axis);
        var restTangent = ChainMath.Tangent(rest, axis);
        if (tangent == Vector3.Zero)
            tangent = restTangent == Vector3.Zero ? ChainMath.Perpendicular(axis) : restTangent;
        var candidate = contactCosine * axis + contactSine * tangent;
        if (bendCosine == -1 || Vector3.Dot(rest, candidate) >= bendCosine)
            return candidate;

        // Find the closest direction on the contact circle that also lies in the bend cone.
        float axialRest = Math.Clamp(Vector3.Dot(rest, axis), -1f, 1f);
        float tangentRest = Vector3.Cross(rest, axis).Length();
        float denominator = contactSine * tangentRest;
        float required = denominator > 0 ? (bendCosine - contactCosine * axialRest) / denominator : float.PositiveInfinity;
        if (required < 1)
        {
            required = MathF.Max(required, -1);
            var side = Vector3.Normalize(Vector3.Cross(axis, restTangent));
            if (Vector3.Dot(side, tangent) < 0)
                side = -side;
            var allowed = required * restTangent + MathF.Sqrt(1 - required * required) * side;
            return contactCosine * axis + contactSine * allowed;
        }

        // An overconstrained particle keeps its length and bend and moves as far from the contact as possible.
        var away = ChainMath.Tangent(-axis, rest);
        if (away == Vector3.Zero)
            away = ChainMath.Perpendicular(rest);
        return bendCosine * rest + MathF.Sqrt(1 - bendCosine * bendCosine) * away;
    }
}
