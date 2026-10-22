using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Source identity and optional animation sample for cold skinning validation diagnostics.</summary>
    public readonly record struct SkinningValidationContext(string Asset, string? Clip, int Sample);

    /// <summary>Validates the source and composed-pose domains of an explicit skinning profile without repair.</summary>
    public static class SkinningValidation
    {
        /// <summary>Checks finite attributes, all four indices and nonnegative weights for rigid DQ sources.
        /// Linear blend sources retain their existing input behavior and are not restricted by this check.</summary>
        public static void ValidateSource(ReadOnlySpan<SkinnedVertex> vertices, int boneCount,
            SkinningProfile profile, in SkinningValidationContext context)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (profile.Method == SkinningMethod.LinearBlend) return;
            if (boneCount < 0 || boneCount > SkinningMath.MaxBonesPerDraw)
                throw InvalidSource(context, profile,
                    $"Bone count {boneCount} is outside [0, {SkinningMath.MaxBonesPerDraw}].", nameof(boneCount));

            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                ref readonly SkinnedVertex source = ref vertices[vertex];
                if (!IsFinite(source.Position))
                    throw InvalidSource(context, profile, $"vertex {vertex} Position must have finite components.");
                if (!IsFinite(source.Normal))
                    throw InvalidSource(context, profile, $"vertex {vertex} Normal must have finite components.");
                if (!IsFinite(source.Color))
                    throw InvalidSource(context, profile, $"vertex {vertex} Color must have finite components.");
                if (!float.IsFinite(source.Uv.X) || !float.IsFinite(source.Uv.Y))
                    throw InvalidSource(context, profile, $"vertex {vertex} Uv must have finite components.");
                if (!IsFinite(source.Tangent))
                    throw InvalidSource(context, profile, $"vertex {vertex} Tangent must have finite components.");

                for (int slot = 0; slot < 4; slot++)
                {
                    float index = source.BoneIndices[slot];
                    if (!float.IsFinite(index) || index < 0f || index >= boneCount || index != MathF.Truncate(index))
                        throw InvalidSource(context, profile,
                            $"vertex {vertex} BoneIndices[{slot}] must be a finite integer in [0, {boneCount}).");
                    float weight = source.BoneWeights[slot];
                    if (!float.IsFinite(weight) || weight < 0f)
                        throw InvalidSource(context, profile,
                            $"vertex {vertex} BoneWeights[{slot}] must be finite and nonnegative.");
                }

                // Exactly three scalar binary32 additions, left-associated in stored X/Y/Z/W order.
                float total = ((source.BoneWeights.X + source.BoneWeights.Y) + source.BoneWeights.Z) + source.BoneWeights.W;
                const float totalTolerance = 1f / (1 << 20);
                if (!float.IsFinite(total) || (total >= 1e-8f && MathF.Abs(total - 1f) > totalTolerance))
                    throw InvalidSource(context, profile,
                        $"vertex {vertex} BoneWeights total must be below 1e-8 or within 2^-20 of one.");
            }
        }

        internal static void ValidateComposed(in Matrix4x4 value, in SkinningValidationContext context, int joint)
        {
            if (!float.IsFinite(value.M11) || !float.IsFinite(value.M12) || !float.IsFinite(value.M13) || !float.IsFinite(value.M14) ||
                !float.IsFinite(value.M21) || !float.IsFinite(value.M22) || !float.IsFinite(value.M23) || !float.IsFinite(value.M24) ||
                !float.IsFinite(value.M31) || !float.IsFinite(value.M32) || !float.IsFinite(value.M33) || !float.IsFinite(value.M34) ||
                !float.IsFinite(value.M41) || !float.IsFinite(value.M42) || !float.IsFinite(value.M43) || !float.IsFinite(value.M44))
                throw InvalidPose(context, joint, "Composed matrix must have finite components.");
            if (value.M14 != 0f || value.M24 != 0f || value.M34 != 0f || value.M44 != 1f)
                throw InvalidPose(context, joint, "Composed matrix must have the exact affine last column (0,0,0,1).");

            // A*A_transpose has six independent entries. Each uses three scalar products and two
            // left-associated adds. Diagonal residuals subtract one after the complete dot product.
            float g11 = ((value.M11 * value.M11) + (value.M12 * value.M12)) + (value.M13 * value.M13);
            float g22 = ((value.M21 * value.M21) + (value.M22 * value.M22)) + (value.M23 * value.M23);
            float g33 = ((value.M31 * value.M31) + (value.M32 * value.M32)) + (value.M33 * value.M33);
            float g12 = ((value.M11 * value.M21) + (value.M12 * value.M22)) + (value.M13 * value.M23);
            float g13 = ((value.M11 * value.M31) + (value.M12 * value.M32)) + (value.M13 * value.M33);
            float g23 = ((value.M21 * value.M31) + (value.M22 * value.M32)) + (value.M23 * value.M33);
            const float gramAllowance = 1f / (1 << 16);
            if (!WithinGram(g11 - 1f) || !WithinGram(g22 - 1f) || !WithinGram(g33 - 1f) ||
                !WithinGram(g12) || !WithinGram(g13) || !WithinGram(g23))
                throw InvalidPose(context, joint, "Composed Gram matrix residual must be at most 2^-16.");

            float determinant = ((value.M11 * ((value.M22 * value.M33) - (value.M23 * value.M32))) -
                (value.M12 * ((value.M21 * value.M33) - (value.M23 * value.M31)))) +
                (value.M13 * ((value.M21 * value.M32) - (value.M22 * value.M31)));
            if (!float.IsFinite(determinant) || determinant <= 0f)
                throw InvalidPose(context, joint, "Composed determinant must be finite and positive.");

            static bool WithinGram(float residual) => float.IsFinite(residual) && MathF.Abs(residual) <= gramAllowance;
        }

        internal static void ValidatePreparedFinite(in RigidDualQuaternion value,
            in SkinningValidationContext context, int joint)
        {
            if (!IsFinite(value.Real) || !IsFinite(value.Dual))
                throw InvalidPose(context, joint, "Prepared dual quaternion arithmetic must remain finite.");
        }

        internal static ArgumentException InvalidPose(in SkinningValidationContext context, int joint,
            string detail, string parameter = "composed") =>
            InvalidSource(context, SkinningProfile.DualQuaternionRigid, $"joint {joint}: {detail}", parameter);

        static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        static bool IsFinite(Vector4 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

        static ArgumentException InvalidSource(in SkinningValidationContext context, SkinningProfile profile,
            string detail, string parameter = "vertices") =>
            new($"Skinning validation failed for asset '{context.Asset}', clip '{context.Clip ?? "<none>"}', " +
                $"sample {context.Sample}, profile {profile.Method} version {profile.AlgorithmVersion}: {detail}", parameter);
    }
}
