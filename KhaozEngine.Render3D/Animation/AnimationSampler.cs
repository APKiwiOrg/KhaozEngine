using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Pure, GPU-free clip sampling + hierarchy composition. Samples an <see cref="AnimationClip"/> at a
    /// time into per-skeleton-node local poses, composes those up the hierarchy into the joint-WORLD bone palette
    /// <see cref="Scene3D.DrawSkinned(SkinnedMeshHandle, ReadOnlySpan{Matrix4x4}, Matrix4x4, Primitives.Color)"/>
    /// consumes, and blends two pose sets at the local-TRS level (the correct basis for a crossfade: blend locals,
    /// compose once). Presentation only; never feed a sampled pose into simulation/RNG/netcode.</summary>
    public static class AnimationSampler
    {
        /// <summary>One local pose per skeleton node: a node animated by <paramref name="clip"/> is sampled at
        /// <paramref name="time"/>; an un-animated node keeps its <see cref="Skeleton.RestLocal"/>. The clip channels
        /// are keyed by glTF logical node index, resolved through <see cref="Skeleton.NodeForLogicalIndex"/>.</summary>
        public static JointPose[] SamplePose(AnimationClip clip, Skeleton skel, float time)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (skel is null) throw new ArgumentNullException(nameof(skel));
            var poses = new JointPose[skel.NodeCount];
            SampleInto(clip, skel, time, poses);
            return poses;
        }

        /// <summary>Allocation-free variant of <see cref="SamplePose"/>: writes the per-node local poses into
        /// <paramref name="into"/> (length must equal <see cref="Skeleton.NodeCount"/>). Reuse one buffer across
        /// frames in the per-frame sample path (the layered animator does this per layer).</summary>
        public static void SampleInto(AnimationClip clip, Skeleton skel, float time, JointPose[] into)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (skel is null) throw new ArgumentNullException(nameof(skel));
            if (into is null) throw new ArgumentNullException(nameof(into));
            if (into.Length != skel.NodeCount)
                throw new ArgumentException($"into length {into.Length} must equal node count {skel.NodeCount}.", nameof(into));
            for (int n = 0; n < skel.NodeCount; n++) into[n] = skel.RestLocal[n];
            int count = clip.Tracks.Count;
            for (int k = 0; k < count; k++)
            {
                JointTrack track = clip.Tracks[k];
                int node = skel.NodeForLogicalIndex(track.TargetNode);
                if (node < 0) continue;   // a channel targeting a node outside the skeleton does not pose anything
                into[node] = track.SampleLocal(skel.RestLocal[node], time);
            }
        }

        /// <summary>Samples a weighted set of clips into one per-node local pose, a running normalised lerp through
        /// <see cref="JointPose.Lerp"/>: each further sample is lerped in by its weight over the weight so far, so the
        /// result does not depend on the weights summing to one. A single positive-weight sample is bit-identical to
        /// <see cref="SampleInto"/>. Zero-weight samples are skipped, and when no sample has weight
        /// <paramref name="into"/> is left untouched. <paramref name="into"/> and <paramref name="scratch"/> are
        /// distinct buffers of <see cref="Skeleton.NodeCount"/> entries. Every sample is validated before anything is
        /// written, so a refused call leaves <paramref name="into"/> as it was. Allocation-free.</summary>
        public static void SampleBlendInto(Skeleton skel, ReadOnlySpan<ClipSample> samples, JointPose[] into, JointPose[] scratch)
        {
            if (skel is null) throw new ArgumentNullException(nameof(skel));
            if (into is null) throw new ArgumentNullException(nameof(into));
            if (scratch is null) throw new ArgumentNullException(nameof(scratch));
            if (into.Length != skel.NodeCount)
                throw new ArgumentException($"into length {into.Length} must equal node count {skel.NodeCount}.", nameof(into));
            if (scratch.Length != skel.NodeCount)
                throw new ArgumentException($"scratch length {scratch.Length} must equal node count {skel.NodeCount}.", nameof(scratch));
            if (ReferenceEquals(into, scratch))
                throw new ArgumentException("into and scratch must be distinct buffers.", nameof(scratch));
            for (int i = 0; i < samples.Length; i++)
            {
                float weight = samples[i].Weight;
                if (!float.IsFinite(weight) || weight < 0f)
                    throw new ArgumentOutOfRangeException(nameof(samples), weight, $"sample {i} weight must be finite and not negative.");
                if (weight > 0f && samples[i].Clip is null)
                    throw new ArgumentException($"sample {i} has a positive weight and no clip.", nameof(samples));
            }

            float total = 0f;
            foreach (ClipSample s in samples)
            {
                if (s.Weight == 0f) continue;
                if (total == 0f)
                {
                    SampleInto(s.Clip, skel, s.Time, into);
                    total = s.Weight;
                    continue;
                }
                SampleInto(s.Clip, skel, s.Time, scratch);
                total += s.Weight;
                for (int n = 0; n < into.Length; n++) into[n] = JointPose.Lerp(into[n], scratch[n], s.Weight / total);
            }
        }

        /// <summary>Compose per-node local poses up the skeleton hierarchy into the per-bone joint-WORLD palette.</summary>
        public static void Compose(Skeleton skel, ReadOnlySpan<JointPose> localByNode, Matrix4x4[] bonePaletteOut)
        {
            if (skel is null) throw new ArgumentNullException(nameof(skel));
            skel.ComposeInto(localByNode, bonePaletteOut);
        }

        /// <summary>Sample <paramref name="clip"/> at <paramref name="time"/> and compose into a fresh joint-WORLD
        /// bone palette (length <see cref="Skeleton.BoneCount"/>).</summary>
        public static Matrix4x4[] SampleToBonePalette(AnimationClip clip, Skeleton skel, float time)
        {
            JointPose[] local = SamplePose(clip, skel, time);
            var palette = new Matrix4x4[skel.BoneCount];
            skel.ComposeInto(local, palette);
            return palette;
        }

        /// <summary>Per-node interpolation of two pose sets by <paramref name="weight"/> in [0,1] (0 == all
        /// <paramref name="a"/>, 1 == all <paramref name="b"/>). Both spans must have one pose per skeleton node.</summary>
        public static JointPose[] BlendPoses(ReadOnlySpan<JointPose> a, ReadOnlySpan<JointPose> b, float weight)
        {
            if (a.Length != b.Length) throw new ArgumentException("pose sets must have equal length.");
            var outp = new JointPose[a.Length];
            for (int i = 0; i < a.Length; i++) outp[i] = JointPose.Lerp(a[i], b[i], weight);
            return outp;
        }

        /// <summary>Wrap <paramref name="time"/> into <c>[0, duration)</c> for a looping clip; a non-positive
        /// duration returns 0.</summary>
        public static float Wrap(float time, float duration)
        {
            if (duration <= 0f) return 0f;
            float w = time - MathF.Floor(time / duration) * duration;
            // Guard the boundary so a value landing exactly on duration (FP) wraps to 0.
            if (w >= duration) w -= duration;
            if (w < 0f) w += duration;
            return w;
        }
    }
}
