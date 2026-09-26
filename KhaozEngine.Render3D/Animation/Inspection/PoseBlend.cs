using System;
using System.Numerics;

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
            BoneMask? mask = null)
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

        /// <summary>
        /// Adds each sample's offset from its reference onto the corresponding destination local pose, in the
        /// joint's local frame. Rotation composes <c>destination * slerp(identity, inverse(reference) * sample, w)</c>
        /// along the shortest arc and is normalized. Translation and scale add <c>(sample - reference) * w</c>
        /// componentwise, so scale is an offset and unit scale stays a no-op. An optional mask multiplies the weight
        /// per node. A destination equal to the reference at unit weight reproduces the sample. This is the same
        /// composition an additive <see cref="AnimationLayer"/> applies in <see cref="LayeredAnimator"/>.
        /// </summary>
        public static void AddInto(
            Span<JointPose> destination,
            ReadOnlySpan<JointPose> sample,
            ReadOnlySpan<JointPose> reference,
            float weight,
            BoneMask? mask = null)
        {
            if (sample.Length != destination.Length)
            {
                throw new ArgumentException(
                    $"Sample pose length {sample.Length} must equal destination length {destination.Length}.",
                    nameof(sample));
            }
            if (reference.Length != destination.Length)
            {
                throw new ArgumentException(
                    $"Reference pose length {reference.Length} must equal destination length {destination.Length}.",
                    nameof(reference));
            }
            if (mask is not null && mask.NodeCount != destination.Length)
            {
                throw new ArgumentException(
                    $"Mask node count {mask.NodeCount} must equal pose length {destination.Length}.",
                    nameof(mask));
            }
            if (!float.IsFinite(weight))
                throw new ArgumentOutOfRangeException(nameof(weight), weight, "Additive weight must be finite.");

            for (int node = 0; node < destination.Length; node++)
            {
                float effectiveWeight = Math.Clamp(
                    mask is null ? weight : weight * mask.Weight(node),
                    0f,
                    1f);
                if (effectiveWeight <= 0f) continue;

                destination[node] = ApplyAdditive(destination[node], sample[node], reference[node], effectiveWeight);
            }
        }

        // Additive composition of one node: contribute (sample - reference), scaled by w, on top of baseP.
        //
        // ONE convention, stated once, and every channel below obeys it: the delta lives in the joint's LOCAL frame.
        //
        //   rotation: delta = inverse(reference) * sample, applied on the RIGHT of the base (result = base * delta),
        //             scaled by w via a shortest-arc slerp from identity toward the full delta. This is the
        //             Unity/Unreal/glTF-additive convention: an additive clip is authored as a per-joint delta in the
        //             joint's OWN local space, so an aim offset or attack layered over locomotion bends the joint
        //             relative to its current local pose rather than swinging it around the parent axis (base * delta
        //             and delta * base disagree grossly for non-commuting rotations).
        //             The delta MUST be extracted on the side it is applied. With base == reference the result is
        //             reference * inverse(reference) * sample == sample, the defining invariant of additive animation
        //             (the authored pose is reproduced exactly when the base IS the reference). Extracting it as
        //             sample * inverse(reference) is the PARENT-frame delta and yields the sample CONJUGATED by the
        //             reference, which coincides with the sample only when the reference is identity or commutes with
        //             it. That was the shipped defect through 17.36.1, invisible to a suite whose additive references
        //             were all identity, and wrong for every glTF humanoid whose t=0 shoulder/spine is rotated (#20).
        //   translation/scale: baseP + (sample - reference) * w. Both are commutative componentwise adds between
        //             quantities already in one frame (a local translation is parent-frame, a scale is a per-axis
        //             factor on the joint's own axes), so neither channel has a side to get wrong. Scale is an
        //             OFFSET, which keeps unit scale a no-op.
        //
        // w == 0 leaves baseP unchanged (delta -> identity). w == 1 applies the full delta.
        // The one implementation: AddInto and LayeredAnimator's additive layers both call it.
        internal static JointPose ApplyAdditive(in JointPose baseP, in JointPose sample, in JointPose reference, float w)
        {
            Vector3 transDelta = sample.Translation - reference.Translation;
            Vector3 scaleDelta = sample.Scale - reference.Scale;

            Quaternion fullDelta = Quaternion.Normalize(Quaternion.Inverse(reference.Rotation) * sample.Rotation);
            // Scale the delta by w: shortest-arc slerp from identity toward fullDelta. Slerp handles the double cover
            // (negates fullDelta when Identity.fullDelta dot < 0, i.e. w scales the SHORT way around).
            Quaternion partialDelta = w >= 1f
                ? fullDelta
                : Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, fullDelta, w));

            return new JointPose
            {
                Translation = baseP.Translation + transDelta * w,
                Rotation = Quaternion.Normalize(baseP.Rotation * partialDelta),
                Scale = baseP.Scale + scaleDelta * w,
            };
        }
    }
}
