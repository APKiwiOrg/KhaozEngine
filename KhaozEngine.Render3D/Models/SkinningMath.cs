using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Pure, GPU-free skinning math: composing a per-bone skin matrix from a joint world transform and
    /// the bone's inverse-bind, blending the 4-bone palette by weights, and normalizing weights. This is the
    /// CPU mirror of the skinned vertex shader's blend, so the deform is headless-unit-testable. Presentation
    /// only: never feed skinning results into simulation/RNG/netcode.</summary>
    public static class SkinningMath
    {
        /// <summary>The model-space skinning matrix for one bone: <c>inverseBind * jointWorld</c>. When
        /// <paramref name="jointWorld"/> equals the bone's rest world transform this is the identity (no deform).</summary>
        public static Matrix4x4 Compose(Matrix4x4 jointWorld, Matrix4x4 inverseBind) => inverseBind * jointWorld;

        /// <summary>Blend up to 4 composed bone matrices by <paramref name="weights"/>, indexing
        /// <paramref name="composedBones"/> with the (float-encoded) <paramref name="indices"/>. A zero total
        /// weight returns the identity so an unrigged vertex is left in place. All four entries of
        /// <paramref name="indices"/> must be valid positions within <paramref name="composedBones"/> regardless
        /// of their weight because the reads are unconditional.</summary>
        public static Matrix4x4 BlendSkinMatrix(ReadOnlySpan<Matrix4x4> composedBones, Vector4 indices, Vector4 weights)
        {
            float total = weights.X + weights.Y + weights.Z + weights.W;
            if (total < 1e-8f) return Matrix4x4.Identity;
            Matrix4x4 m = default;
            m += composedBones[(int)indices.X] * weights.X;
            m += composedBones[(int)indices.Y] * weights.Y;
            m += composedBones[(int)indices.Z] * weights.Z;
            m += composedBones[(int)indices.W] * weights.W;
            return m;
        }

        /// <summary>Normalize a 4-weight vector to sum to 1; an all-zero input stays zero (identity fallback).</summary>
        public static Vector4 NormalizeWeights(Vector4 w)
        {
            float total = w.X + w.Y + w.Z + w.W;
            return total < 1e-8f ? Vector4.Zero : w / total;
        }

    }
}
