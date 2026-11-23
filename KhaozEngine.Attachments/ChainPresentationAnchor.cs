using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal readonly record struct ChainPresentationAnchor(Matrix4x4 Inverse, Quaternion Rotation)
{
    internal bool TryCarryTo(in ChainPose current, out ChainSwingTransport transport)
    {
        transport = default;
        var relative = Inverse * current.Matrix;
        var x = new Vector3(relative.M11, relative.M12, relative.M13);
        var y = new Vector3(relative.M21, relative.M22, relative.M23);
        var z = new Vector3(relative.M31, relative.M32, relative.M33);
        var extent = Vector3.Max(Vector3.Abs(x), Vector3.Max(Vector3.Abs(y), Vector3.Abs(z)));
        float scale = MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
        if (!(scale > 0) || !float.IsFinite(scale))
            return false;
        x /= scale;
        y /= scale;
        z /= scale;

        float squared = x.LengthSquared();
        float tolerance = squared * 0.000001f;
        if (MathF.Abs(y.LengthSquared() - squared) > tolerance || MathF.Abs(z.LengthSquared() - squared) > tolerance ||
            MathF.Abs(Vector3.Dot(x, y)) > tolerance || MathF.Abs(Vector3.Dot(x, z)) > tolerance ||
            MathF.Abs(Vector3.Dot(y, z)) > tolerance || !(Vector3.Dot(Vector3.Cross(x, y), z) > 0))
        {
            return false;
        }

        // Right multiplication by a positive similarity rotates both endpoint bases by the same turn.
        if (current.Rotation != Rotation && current.Rotation != -Rotation)
        {
            var turn = Quaternion.Normalize(current.Rotation * Quaternion.Conjugate(Rotation));
            transport = new ChainSwingTransport(turn, Quaternion.Conjugate(turn));
        }
        return true;
    }
}

internal readonly record struct ChainSwingTransport(Quaternion Turn, Quaternion Inverse)
{
    internal Quaternion Apply(Quaternion swing) =>
        Turn == default || swing == Quaternion.Identity ? swing : Turn * swing * Inverse;
}
