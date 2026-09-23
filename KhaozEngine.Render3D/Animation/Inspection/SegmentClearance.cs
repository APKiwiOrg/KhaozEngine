using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Animation.Inspection
{
    /// <summary>A model-space capsule axis named by two skeleton nodes and its radius in metres.</summary>
    public readonly record struct Capsule(string NodeA, string NodeB, float Radius);

    /// <summary>Measures signed clearance between a node-mounted segment and skeleton capsules.</summary>
    public static class SegmentClearance
    {
        public static float Min(
            AnimationClip clip,
            Skeleton skeleton,
            string ridingNode,
            in Matrix4x4 segmentLocal,
            Vector3 segmentStart,
            Vector3 segmentEnd,
            IReadOnlyList<Capsule> capsules,
            int samples)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (skeleton is null) throw new ArgumentNullException(nameof(skeleton));
            if (ridingNode is null) throw new ArgumentNullException(nameof(ridingNode));
            if (capsules is null) throw new ArgumentNullException(nameof(capsules));
            if (capsules.Count == 0)
                throw new ArgumentException("At least one capsule is required.", nameof(capsules));
            if (samples < 2)
                throw new ArgumentOutOfRangeException(nameof(samples), samples, "At least two samples are required.");
            if (!IsFinite(segmentLocal))
                throw new ArgumentOutOfRangeException(nameof(segmentLocal), "Segment local matrix must be finite.");
            if (!IsFinite(segmentStart))
                throw new ArgumentOutOfRangeException(nameof(segmentStart), segmentStart,
                    "Segment start components must be finite.");
            if (!IsFinite(segmentEnd))
                throw new ArgumentOutOfRangeException(nameof(segmentEnd), segmentEnd,
                    "Segment end components must be finite.");

            int ridingNodeIndex = skeleton.IndexOf(ridingNode);
            var capsuleNodeA = new int[capsules.Count];
            var capsuleNodeB = new int[capsules.Count];
            for (int capsule = 0; capsule < capsules.Count; capsule++)
            {
                Capsule value = capsules[capsule];
                if (!float.IsFinite(value.Radius) || value.Radius < 0f)
                    throw new ArgumentOutOfRangeException(nameof(capsules), value.Radius,
                        $"Capsule radius at index {capsule} must be finite and non-negative.");
                capsuleNodeA[capsule] = skeleton.IndexOf(value.NodeA);
                capsuleNodeB[capsule] = skeleton.IndexOf(value.NodeB);
            }

            var probe = new PoseProbe(skeleton);
            float minimum = float.PositiveInfinity;
            for (int sample = 0; sample < samples; sample++)
            {
                float phase = (float)sample / (samples - 1);
                probe.SampleClip(clip, phase);
                Matrix4x4 segmentModel = segmentLocal * probe.JointModel(ridingNodeIndex);
                Vector3 ridingStart = Vector3.Transform(segmentStart, segmentModel);
                Vector3 ridingEnd = Vector3.Transform(segmentEnd, segmentModel);
                if (!IsFinite(ridingStart) || !IsFinite(ridingEnd))
                    throw new ArgumentException("Clip produced a non-finite riding segment.", nameof(clip));

                for (int capsule = 0; capsule < capsules.Count; capsule++)
                {
                    Vector3 capsuleStart = probe.JointModel(capsuleNodeA[capsule]).Translation;
                    Vector3 capsuleEnd = probe.JointModel(capsuleNodeB[capsule]).Translation;
                    if (!IsFinite(capsuleStart) || !IsFinite(capsuleEnd))
                        throw new ArgumentException("Clip produced a non-finite capsule axis.", nameof(clip));
                    float clearance = SegmentDistance(ridingStart, ridingEnd, capsuleStart, capsuleEnd)
                        - capsules[capsule].Radius;
                    if (!float.IsFinite(clearance))
                        throw new ArgumentException("Geometry produced a non-finite clearance.", nameof(clip));
                    minimum = MathF.Min(minimum, clearance);
                }
            }
            return minimum;
        }

        static float SegmentDistance(Vector3 firstStart, Vector3 firstEnd, Vector3 secondStart, Vector3 secondEnd)
        {
            Vector3 firstDirection = firstEnd - firstStart;
            Vector3 secondDirection = secondEnd - secondStart;
            Vector3 startOffset = firstStart - secondStart;
            float firstLengthSquared = Vector3.Dot(firstDirection, firstDirection);
            float secondLengthSquared = Vector3.Dot(secondDirection, secondDirection);

            float firstParameter;
            float secondParameter;
            if (firstLengthSquared == 0f && secondLengthSquared == 0f)
                return Vector3.Distance(firstStart, secondStart);

            if (firstLengthSquared == 0f)
            {
                firstParameter = 0f;
                secondParameter = Math.Clamp(
                    Vector3.Dot(secondDirection, startOffset) / secondLengthSquared,
                    0f,
                    1f);
            }
            else
            {
                float firstProjection = Vector3.Dot(firstDirection, startOffset);
                if (secondLengthSquared == 0f)
                {
                    secondParameter = 0f;
                    firstParameter = Math.Clamp(-firstProjection / firstLengthSquared, 0f, 1f);
                }
                else
                {
                    float directionsDot = Vector3.Dot(firstDirection, secondDirection);
                    float secondProjection = Vector3.Dot(secondDirection, startOffset);
                    float denominator = (firstLengthSquared * secondLengthSquared)
                        - (directionsDot * directionsDot);
                    firstParameter = denominator > 0f
                        ? Math.Clamp(
                            ((directionsDot * secondProjection) - (firstProjection * secondLengthSquared))
                                / denominator,
                            0f,
                            1f)
                        : 0f;

                    secondParameter = ((directionsDot * firstParameter) + secondProjection)
                        / secondLengthSquared;
                    if (secondParameter < 0f)
                    {
                        secondParameter = 0f;
                        firstParameter = Math.Clamp(-firstProjection / firstLengthSquared, 0f, 1f);
                    }
                    else if (secondParameter > 1f)
                    {
                        secondParameter = 1f;
                        firstParameter = Math.Clamp(
                            (directionsDot - firstProjection) / firstLengthSquared,
                            0f,
                            1f);
                    }
                }
            }

            Vector3 firstClosest = firstStart + (firstParameter * firstDirection);
            Vector3 secondClosest = secondStart + (secondParameter * secondDirection);
            return Vector3.Distance(firstClosest, secondClosest);
        }

        static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        static bool IsFinite(in Matrix4x4 value) =>
            float.IsFinite(value.M11) && float.IsFinite(value.M12)
            && float.IsFinite(value.M13) && float.IsFinite(value.M14)
            && float.IsFinite(value.M21) && float.IsFinite(value.M22)
            && float.IsFinite(value.M23) && float.IsFinite(value.M24)
            && float.IsFinite(value.M31) && float.IsFinite(value.M32)
            && float.IsFinite(value.M33) && float.IsFinite(value.M34)
            && float.IsFinite(value.M41) && float.IsFinite(value.M42)
            && float.IsFinite(value.M43) && float.IsFinite(value.M44);
    }
}
