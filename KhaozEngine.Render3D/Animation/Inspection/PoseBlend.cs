using System;

namespace KhaozEngine.Render3D.Animation.Inspection
{
    /// <summary>Allocation-free blending between caller-owned local pose buffers.</summary>
    public static class PoseBlend
    {
        /// <summary>
        /// Blends each destination local pose toward the corresponding source pose. Translation and scale
        /// interpolate componentwise. Rotation follows the shortest spherical arc and is normalized. An optional
        /// mask multiplies the blend weight per node.
        /// </summary>
        public static void BlendInto(
            Span<JointPose> into,
            ReadOnlySpan<JointPose> from,
            float weight,
            BoneMask? mask)
        {
            if (from.Length != into.Length)
            {
                throw new ArgumentException(
                    $"Source pose length {from.Length} must equal destination length {into.Length}.",
                    nameof(from));
            }
            if (mask is not null && mask.NodeCount != into.Length)
            {
                throw new ArgumentException(
                    $"Mask node count {mask.NodeCount} must equal pose length {into.Length}.",
                    nameof(mask));
            }
            if (!float.IsFinite(weight))
                throw new ArgumentOutOfRangeException(nameof(weight), weight, "Blend weight must be finite.");

            for (int node = 0; node < into.Length; node++)
            {
                float effectiveWeight = Math.Clamp(
                    mask is null ? weight : weight * mask.Weight(node),
                    0f,
                    1f);
                if (effectiveWeight <= 0f) continue;
                if (effectiveWeight >= 1f)
                {
                    into[node] = from[node];
                    continue;
                }

                into[node] = JointPose.Lerp(into[node], from[node], effectiveWeight);
            }
        }
    }
}
