using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Source identity and optional animation sample for cold skinning validation diagnostics.</summary>
    public readonly record struct SkinningValidationContext(string Asset, string? Clip, int Sample);

    /// <summary>Validates the input domain of an explicitly selected skinning profile without repairing it.</summary>
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
