using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Local evidence that a Legacy capsule contact belongs to a particular inclined static surface.</summary>
internal readonly record struct LegacySlopePlane(Vector3 AxisPoint, Vector3 Normal, StaticHandle Body)
{
    internal const float SurfaceTolerance = 0.001f;

    internal static bool TryRead(IPhysicsWorld world, Vector3 expectedAxis, float cosLimit, out LegacySlopePlane plane)
    {
        if (RayNear(world, expectedAxis, out RayHit axis) && axis.Body is { } body &&
            axis.Normal.Y >= cosLimit && (axis.Normal.X != 0f || axis.Normal.Z != 0f))
        {
            plane = new(axis.Point, axis.Normal, body);
            return true;
        }
        plane = default;
        return false;
    }

    internal bool Matches(StaticHandle? body, Vector3 point, Vector3 normal, float cosLimit)
        => body == Body && normal.Y >= cosLimit && Vector3.Dot(normal, Normal) >= 0.9999f &&
           float.Abs(Vector3.Dot(point - AxisPoint, Normal)) <= SurfaceTolerance;

    internal static bool ConfirmsPush(IPhysicsWorld world, CapsuleShape probe, Vector3 centre, Vector3 push,
        float depth, float cosLimit)
    {
        if (!(push.Y > 0f) || (push.X == 0f && push.Z == 0f) || depth > probe.Radius) return false;

        // For a planar contact the inflated lower sphere is radius minus depth from the plane. Confirm that
        // both the axis and the implied point of tangency hit the same inclined static and agree with the push.
        Vector3 normal = push / depth;
        Vector3 lowerSphere = centre - Vector3.UnitY * (probe.Length * 0.5f);
        Vector3 tangent = lowerSphere - normal * (probe.Radius - depth);
        float axisY = tangent.Y - ((centre.X - tangent.X) * normal.X + (centre.Z - tangent.Z) * normal.Z) / normal.Y;
        return TryRead(world, new(centre.X, axisY, centre.Z), cosLimit, out LegacySlopePlane plane) &&
            RayNear(world, tangent, out RayHit contact) &&
            plane.Matches(contact.Body, contact.Point, contact.Normal, cosLimit) &&
            plane.Matches(contact.Body, tangent, normal, cosLimit);
    }

    private static bool RayNear(IPhysicsWorld world, Vector3 point, out RayHit hit)
    {
        hit = default;
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z) &&
            world.Raycast(point + Vector3.UnitY * SurfaceTolerance, -Vector3.UnitY, 2f * SurfaceTolerance,
                out hit, QueryFilter.StaticsOnly);
    }
}
