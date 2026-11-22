using System.Numerics;

namespace KhaozEngine.Attachments;

internal readonly struct ChainPose
{
    internal ChainPose(in Matrix4x4 matrix)
    {
        Matrix = matrix;
        // Composing an animated pose can introduce shear. Factor out an orthonormal frame
        // while keeping all remaining scale and shear in the stretch matrix.
        var x = ChainMath.Direction(new(matrix.M11, matrix.M12, matrix.M13), Vector3.UnitX);
        var y = ChainMath.Tangent(new(matrix.M21, matrix.M22, matrix.M23), x);
        if (y == Vector3.Zero)
            y = ChainMath.Perpendicular(x);
        var z = Vector3.Cross(x, y);
        var basis = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        Rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
        var linear = matrix;
        linear.Translation = Vector3.Zero;
        Stretch = linear * Matrix4x4.Transpose(basis);
    }

    internal Matrix4x4 Matrix { get; }
    internal Quaternion Rotation { get; }
    private Matrix4x4 Stretch { get; }

    internal Matrix4x4 Interpolate(in ChainPose current, float amount)
    {
        if (amount == 1 || Matrix == current.Matrix)
            return current.Matrix;
        var rotation = Quaternion.Slerp(Rotation, current.Rotation, amount);
        var result = Matrix4x4.Lerp(Stretch, current.Stretch, amount) * Matrix4x4.CreateFromQuaternion(rotation);
        result.Translation = Vector3.Lerp(Matrix.Translation, current.Matrix.Translation, amount);
        return result;
    }
}
