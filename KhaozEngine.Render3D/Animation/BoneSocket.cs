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
        /// and shear. A reflected basis keeps its handedness. Throws an <see cref="System.ArgumentException"/>
        /// when the joint basis is non-finite, collapsed, or linearly dependent.</summary>
        public static Matrix4x4 ComposeRigid(in Matrix4x4 local, in Matrix4x4 jointModel, in Matrix4x4 model) =>
            local * Orthonormalize(jointModel) * model;

        static Matrix4x4 Orthonormalize(in Matrix4x4 jointModel)
        {
            var x = new Vector3(jointModel.M11, jointModel.M12, jointModel.M13);
            var y = new Vector3(jointModel.M21, jointModel.M22, jointModel.M23);
            var z = new Vector3(jointModel.M31, jointModel.M32, jointModel.M33);

            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z)) throw InvalidBasis(nameof(jointModel));
            float xLengthSquared = x.LengthSquared();
            if (!float.IsFinite(xLengthSquared) || xLengthSquared < MinBasisLengthSquared)
                throw InvalidBasis(nameof(jointModel));
            x = Vector3.Normalize(x);

            y -= Vector3.Dot(y, x) * x;
            float yLengthSquared = y.LengthSquared();
            if (!float.IsFinite(yLengthSquared) || yLengthSquared < MinBasisLengthSquared)
                throw InvalidBasis(nameof(jointModel));
            y = Vector3.Normalize(y);

            z -= Vector3.Dot(z, x) * x + Vector3.Dot(z, y) * y;
            float zLengthSquared = z.LengthSquared();
            if (!float.IsFinite(zLengthSquared) || zLengthSquared < MinBasisLengthSquared)
                throw InvalidBasis(nameof(jointModel));
            z = Vector3.Normalize(z);

            return new Matrix4x4(
                x.X, x.Y, x.Z, 0f,
                y.X, y.Y, y.Z, 0f,
                z.X, z.Y, z.Z, 0f,
                jointModel.M41, jointModel.M42, jointModel.M43, 1f);
        }

        static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        static System.ArgumentException InvalidBasis(string paramName) =>
            new("The joint basis must contain three finite, linearly independent axes.", paramName);
    }
}
