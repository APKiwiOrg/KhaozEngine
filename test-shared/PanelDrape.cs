using System;
using System.Numerics;

namespace KhaozEngine.Tests.Attachments;

/// <summary>The analytic tangent, curved bearing span and world-plumb tail in the central cross-section.</summary>
internal readonly record struct PanelDrape(Vector3 Seam, Vector3 Center, float Radius)
{
    internal static bool TryTangent(Vector3 pin, Vector3 center, float radius, out Vector3 tangent)
    {
        Vector3 fromCenter = pin - center;
        fromCenter.X = 0;
        float squared = fromCenter.LengthSquared();
        if (squared <= radius * radius || MathF.Abs(fromCenter.Z) >= radius || fromCenter.Y <= 0)
        {
            tangent = default;
            return false;
        }

        Vector3 outward = new(0, -fromCenter.Z, fromCenter.Y);
        Vector3 normal = (radius * fromCenter + MathF.Sqrt(squared - radius * radius) * outward) / squared;
        tangent = center + radius * normal;
        return true;
    }

    internal (Vector3 Point, Vector3 Normal) AtLength(float length)
    {
        if (!TryTangent(Seam, Center, Radius, out Vector3 tangent))
            throw new InvalidOperationException("The fitted pin must load the lip.");
        Vector3 upperNormal = Vector3.Normalize(tangent - Center);
        float upperLength = Vector3.Distance(Seam, tangent);
        if (length <= upperLength)
            return (Vector3.Lerp(Seam, tangent, length / upperLength), upperNormal);

        float bendAngle = MathF.Atan2(upperNormal.Y, upperNormal.Z);
        float arcTravel = length - upperLength;
        if (arcTravel <= Radius * bendAngle)
        {
            float angle = bendAngle - arcTravel / Radius;
            var normal = new Vector3(0, MathF.Sin(angle), MathF.Cos(angle));
            return (Center + Radius * normal, normal);
        }

        return (Center + Radius * Vector3.UnitZ - (arcTravel - Radius * bendAngle) * Vector3.UnitY, Vector3.UnitZ);
    }
}
