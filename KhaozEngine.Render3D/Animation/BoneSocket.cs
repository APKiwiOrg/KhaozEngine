using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Pure, GPU-free composition for a rigid piece attached to a posed skeleton joint.</summary>
    public static class BoneSocket
    {
        const float MinBasisLengthSquared = 1e-12f;

        /// <summary>Compose a piece-local transform through a model-space joint and the model transform.</summary>
        public static Matrix4x4 Compose(in Matrix4x4 local, in Matrix4x4 jointModel, in Matrix4x4 model) =>
            local * jointModel * model;

        /// <summary>Compose through the rigid part of the joint, preserving its translation while removing scale
        /// and shear. A reflected basis keeps its handedness. A degenerate basis with no recoverable orientation
        /// is used unchanged.</summary>
        public static Matrix4x4 ComposeRigid(in Matrix4x4 local, in Matrix4x4 jointModel, in Matrix4x4 model) =>
            local * Orthonormalize(jointModel) * model;

        static Matrix4x4 Orthonormalize(in Matrix4x4 value)
        {
            var x = new Vector3(value.M11, value.M12, value.M13);
            var y = new Vector3(value.M21, value.M22, value.M23);
            var z = new Vector3(value.M31, value.M32, value.M33);

            if (x.LengthSquared() < MinBasisLengthSquared) return value;
            x = Vector3.Normalize(x);

            y -= Vector3.Dot(y, x) * x;
            if (y.LengthSquared() < MinBasisLengthSquared) return value;
            y = Vector3.Normalize(y);

            z -= Vector3.Dot(z, x) * x + Vector3.Dot(z, y) * y;
            if (z.LengthSquared() < MinBasisLengthSquared) return value;
            z = Vector3.Normalize(z);

            return new Matrix4x4(
                x.X, x.Y, x.Z, 0f,
                y.X, y.Y, y.Z, 0f,
                z.X, z.Y, z.Z, 0f,
                value.M41, value.M42, value.M43, 1f);
        }
    }
}
