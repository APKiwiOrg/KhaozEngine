using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The outward face normal of a body triangle at rest.</summary>
/// <param name="BodyTriangleIndex">The zero-based triangle index across the body map's parts.</param>
/// <param name="Normal">The unit face normal in the body's rest frame.</param>
public readonly record struct LiftFootprintBodyNormal(int BodyTriangleIndex, Vector3 Normal);

internal static class LiftFootprintBodyNormalRules
{
    internal const double UnitLengthTolerance = 0.001;

    internal static void CheckTriangleIndex(long triangle, int bodyTriangleCount, int normalIndex, int? byteOffset = null)
    {
        if (triangle < 0 || triangle >= bodyTriangleCount)
            throw new LiftFootprintDataException(LiftFootprintRefusal.BodyNormalTriangleOutsideRange,
                "A face normal's triangle must lie within the declared body triangles.")
            { BodyTriangleIndex = triangle, BodyTriangleCount = bodyTriangleCount, NormalIndex = normalIndex, ByteOffset = byteOffset };
    }

    internal static void CheckEntry(LiftFootprintBodyNormal entry, int previousTriangle, int normalIndex, int? byteOffset = null)
    {
        if (entry.BodyTriangleIndex <= previousTriangle)
            throw new LiftFootprintDataException(LiftFootprintRefusal.BodyNormalTrianglesNotAscending,
                "Body triangles with face normals must increase strictly.")
            { BodyTriangleIndex = entry.BodyTriangleIndex, NormalIndex = normalIndex, ByteOffset = byteOffset };

        int? vectorOffset = byteOffset + sizeof(uint);
        double x = entry.Normal.X, y = entry.Normal.Y, z = entry.Normal.Z;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new LiftFootprintDataException(LiftFootprintRefusal.BodyNormalNonFinite, "A body face normal must be finite.")
            { BodyTriangleIndex = entry.BodyTriangleIndex, NormalIndex = normalIndex, ByteOffset = vectorOffset };
        if (Math.Abs(Math.Sqrt(x * x + y * y + z * z) - 1) > UnitLengthTolerance)
            throw new LiftFootprintDataException(LiftFootprintRefusal.BodyNormalNotUnit,
                "A body face normal's length must be within 0.001 of one.")
            { BodyTriangleIndex = entry.BodyTriangleIndex, NormalIndex = normalIndex, ByteOffset = vectorOffset };
    }
}
