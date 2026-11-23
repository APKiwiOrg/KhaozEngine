using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class ChainMath
{
    internal static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static bool IsAffine(in Matrix4x4 matrix) =>
        IsFinite(new Vector3(matrix.M11, matrix.M12, matrix.M13)) &&
        IsFinite(new Vector3(matrix.M21, matrix.M22, matrix.M23)) &&
        IsFinite(new Vector3(matrix.M31, matrix.M32, matrix.M33)) &&
        IsFinite(matrix.Translation) &&
        matrix.M14 == 0 && matrix.M24 == 0 && matrix.M34 == 0 && matrix.M44 == 1;

    internal static bool TryInvert(in Matrix4x4 matrix, out Matrix4x4 inverse)
    {
        inverse = default;
        if (!IsAffine(matrix) || !Matrix4x4.Invert(matrix, out inverse))
            return false;

        // The generic inverse can round the homogeneous one away from exactly one.
        inverse.M44 = 1;
        return IsAffine(inverse);
    }

    internal static double SquaredBasisCondition(in Matrix4x4 matrix)
    {
        var x = new Vector3(matrix.M11, matrix.M12, matrix.M13);
        var y = new Vector3(matrix.M21, matrix.M22, matrix.M23);
        var z = new Vector3(matrix.M31, matrix.M32, matrix.M33);
        double xx = Dot(x, x), yy = Dot(y, y), zz = Dot(z, z);
        double xy = Math.Abs(Dot(x, y)), xz = Math.Abs(Dot(x, z)), yz = Math.Abs(Dot(y, z));

        // The row Gram matrix has the squared singular values as its eigenvalues.
        // Gershgorin bounds also reject bases whose smallest direction cannot be bounded away from zero.
        double lower = Math.Min(xx - (xy + xz), Math.Min(yy - (xy + yz), zz - (xz + yz)));
        double upper = Math.Max(xx + (xy + xz), Math.Max(yy + (xy + yz), zz + (xz + yz)));
        return lower > 0 ? upper / lower : double.PositiveInfinity;

        // Double intermediates retain small separations and keep the squared float range representable.
        static double Dot(Vector3 left, Vector3 right) =>
            (double)left.X * right.X + (double)left.Y * right.Y + (double)left.Z * right.Z;
    }

    internal static double DistanceSquared(Vector3 first, Vector3 second)
    {
        double x = (double)first.X - second.X;
        double y = (double)first.Y - second.Y;
        double z = (double)first.Z - second.Z;
        return x * x + y * y + z * z;
    }

    internal static Vector3 Direction(Vector3 offset, Vector3 fallback)
    {
        float squared = offset.LengthSquared();
        if (squared > 0 && float.IsFinite(squared))
            return offset / MathF.Sqrt(squared);

        float largest = MathF.Max(MathF.Abs(offset.X), MathF.Max(MathF.Abs(offset.Y), MathF.Abs(offset.Z)));
        return largest > 0 && float.IsFinite(largest) ? Vector3.Normalize(offset / largest) : fallback;
    }

    internal static Vector3 Perpendicular(Vector3 direction)
    {
        Vector3 axis = MathF.Abs(direction.X) < 0.75f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(direction, axis));
    }

    internal static Vector3 Tangent(Vector3 direction, Vector3 axis) =>
        Direction(Vector3.Cross(axis, Vector3.Cross(direction, axis)), Vector3.Zero);

    internal static Quaternion Swing(Vector3 from, Vector3 to)
    {
        var cross = Vector3.Cross(from, to);
        float sin = cross.Length();
        float cos = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (sin > 1e-7f)
            return Quaternion.CreateFromAxisAngle(cross / sin, MathF.Atan2(sin, cos));
        return cos < 0 ? new Quaternion(Perpendicular(from), 0) : Quaternion.Identity;
    }

    internal static float MidpointSpringWeight(float frequency, float seconds)
    {
        // Implicit midpoint for x'' = frequency squared times (target - x).
        double halfPhase = 0.5 * frequency * seconds;
        double squared = halfPhase * halfPhase;
        return (float)(2 * squared / (1 + squared));
    }
}
