using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Animation.Inspection
{
    /// <summary>Minimum sole height and phase, plus the largest horizontal stance slide.</summary>
    public readonly record struct FootPlantReport(float MinSoleHeight, float MinSolePhase, float MaxStanceSlide);

    /// <summary>Measures model-space foot height and distance-driven stance stability for a looping clip.</summary>
    public static class FootPlant
    {
        public static FootPlantReport Measure(
            AnimationClip clip,
            Skeleton skeleton,
            IReadOnlyList<string> footNodes,
            Vector3 soleOffset,
            float groundHeight,
            float strideMetres,
            int samples)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (skeleton is null) throw new ArgumentNullException(nameof(skeleton));
            if (footNodes is null) throw new ArgumentNullException(nameof(footNodes));
            if (footNodes.Count == 0)
                throw new ArgumentException("At least one foot node is required.", nameof(footNodes));
            if (!IsFinite(soleOffset))
                throw new ArgumentOutOfRangeException(nameof(soleOffset), soleOffset,
                    "Sole offset components must be finite.");
            if (!float.IsFinite(groundHeight))
                throw new ArgumentOutOfRangeException(nameof(groundHeight), groundHeight,
                    "Ground height must be finite.");
            if (!float.IsFinite(strideMetres) || strideMetres < 0f)
                throw new ArgumentOutOfRangeException(nameof(strideMetres), strideMetres,
                    "Stride must be finite and non-negative.");
            if (samples < 2)
                throw new ArgumentOutOfRangeException(nameof(samples), samples, "At least two samples are required.");

            var footIndices = new int[footNodes.Count];
            for (int foot = 0; foot < footIndices.Length; foot++)
                footIndices[foot] = skeleton.IndexOf(footNodes[foot]);

            var probe = new PoseProbe(skeleton);
            var soleBySample = new Vector3[samples];
            var stanceBySample = new bool[samples];
            float minSoleHeight = float.PositiveInfinity;
            float minSolePhase = 0f;
            float maxStanceSlide = 0f;

            for (int foot = 0; foot < footIndices.Length; foot++)
            {
                for (int sample = 0; sample < samples; sample++)
                {
                    float phase = (float)sample / samples;
                    probe.SampleClip(clip, phase);
                    Vector3 sole = Vector3.Transform(soleOffset, probe.JointModel(footIndices[foot]));
                    if (!IsFinite(sole))
                        throw new ArgumentException("Clip produced a non-finite sole position.", nameof(clip));
                    soleBySample[sample] = sole;
                    stanceBySample[sample] = sole.Y <= groundHeight;
                    if (sole.Y < minSoleHeight)
                    {
                        minSoleHeight = sole.Y;
                        minSolePhase = phase;
                    }
                }

                float footSlide = MeasureFootSlide(soleBySample, stanceBySample, strideMetres);
                if (!float.IsFinite(footSlide))
                    throw new ArgumentException("Clip produced a non-finite stance slide.", nameof(clip));
                maxStanceSlide = MathF.Max(maxStanceSlide, footSlide);
            }

            return new FootPlantReport(minSoleHeight, minSolePhase, maxStanceSlide);
        }

        static float MeasureFootSlide(Vector3[] soleBySample, bool[] stanceBySample, float strideMetres)
        {
            int samples = soleBySample.Length;
            bool allStance = true;
            for (int sample = 0; sample < samples; sample++)
                allStance &= stanceBySample[sample];

            if (allStance)
                return MeasureRunSlide(soleBySample, strideMetres, start: 0, length: samples);

            float maxSlide = 0f;
            for (int sample = 0; sample < samples; sample++)
            {
                int previous = sample == 0 ? samples - 1 : sample - 1;
                if (!stanceBySample[sample] || stanceBySample[previous]) continue;

                int length = 1;
                while (length < samples && stanceBySample[(sample + length) % samples]) length++;
                maxSlide = MathF.Max(maxSlide, MeasureRunSlide(soleBySample, strideMetres, sample, length));
            }
            return maxSlide;
        }

        static float MeasureRunSlide(Vector3[] soleBySample, float strideMetres, int start, int length)
        {
            if (length < 2) return 0f;

            int samples = soleBySample.Length;
            float anchorPhase = (float)start / samples;
            Vector3 anchor = soleBySample[start] + new Vector3(0f, 0f, anchorPhase * strideMetres);
            float maxSlideSquared = 0f;
            for (int offset = 1; offset < length; offset++)
            {
                int sample = (start + offset) % samples;
                float unwrappedPhase = (float)(start + offset) / samples;
                Vector3 travel = soleBySample[sample] + new Vector3(0f, 0f, unwrappedPhase * strideMetres);
                float x = travel.X - anchor.X;
                float z = travel.Z - anchor.Z;
                maxSlideSquared = MathF.Max(maxSlideSquared, (x * x) + (z * z));
            }
            return MathF.Sqrt(maxSlideSquared);
        }

        static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
