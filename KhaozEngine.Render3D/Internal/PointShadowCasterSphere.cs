using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

internal readonly record struct PointShadowCasterSphere(Vector3 Center, float Radius)
{
    internal static PointShadowCasterSphere FromRestBounds(
        in MeshBounds bounds, in Matrix4x4 world, float safetyFactor)
    {
        bounds.WorldSphere(world, out Vector3 center, out float radius);
        return new PointShadowCasterSphere(center, radius * safetyFactor);
    }

    internal bool TouchesShadowingShell(Vector3 lightPosition, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        float distanceSquared = (Center - lightPosition).LengthSquared();
        float reach = Radius + radius;
        if (distanceSquared > reach * reach) return false;
        if (nearRadius > 0f && MathF.Sqrt(distanceSquared) + Radius <= nearRadius) return false;
        if (!Scene3D.IsExclusionBox(exclusionMin, exclusionMax)) return true;
        return !(Center.X - Radius >= exclusionMin.X && Center.X + Radius <= exclusionMax.X
            && Center.Y - Radius >= exclusionMin.Y && Center.Y + Radius <= exclusionMax.Y
            && Center.Z - Radius >= exclusionMin.Z && Center.Z + Radius <= exclusionMax.Z);
    }
}
