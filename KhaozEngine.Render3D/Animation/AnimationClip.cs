using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>How a keyframe track interpolates between its keys. CUBICSPLINE is reduced to the value keys and
    /// treated as <see cref="Linear"/> (the locomotion clips do not author it; the tangents are dropped).</summary>
    public enum InterpolationMode { Linear, Step }

    /// <summary>A scalar-vector keyframe channel (translation or scale): sorted key times + a value per key. Sampling
    /// clamps to the end keys (the loop wrap is the caller's, via <see cref="AnimationSampler.Wrap"/>).</summary>
    public sealed class Vector3Track
    {
        public float[] Times { get; }
        public Vector3[] Values { get; }
        public InterpolationMode Mode { get; }
        public float Duration => Times.Length == 0 ? 0f : Times[Times.Length - 1];

        public Vector3Track(float[] times, Vector3[] values, InterpolationMode mode)
        {
            Times = times ?? throw new ArgumentNullException(nameof(times));
            Values = values ?? throw new ArgumentNullException(nameof(values));
            if (times.Length != values.Length) throw new ArgumentException("times and values must have equal length.");
            Mode = mode;
        }

        public Vector3 Sample(float t)
        {
            int n = Times.Length;
            if (n == 0) return Vector3.Zero;
            if (n == 1 || t <= Times[0]) return Values[0];
            if (t >= Times[n - 1]) return Values[n - 1];
            int i = KeyframeMath.SegmentIndex(Times, t);
            if (Mode == InterpolationMode.Step) return Values[i];
            float f = KeyframeMath.SegmentFraction(Times, i, t);
            return Vector3.Lerp(Values[i], Values[i + 1], f);
        }
    }

    /// <summary>A rotation keyframe channel: sorted key times + a unit quaternion per key. Linear sampling slerps
    /// (re-normalized); Step holds the left key. Clamps to the end keys.</summary>
    public sealed class QuaternionTrack
    {
        public float[] Times { get; }
        public Quaternion[] Values { get; }
        public InterpolationMode Mode { get; }
        public float Duration => Times.Length == 0 ? 0f : Times[Times.Length - 1];

        public QuaternionTrack(float[] times, Quaternion[] values, InterpolationMode mode)
        {
            Times = times ?? throw new ArgumentNullException(nameof(times));
            Values = values ?? throw new ArgumentNullException(nameof(values));
            if (times.Length != values.Length) throw new ArgumentException("times and values must have equal length.");
            Mode = mode;
        }

        public Quaternion Sample(float t)
        {
            int n = Times.Length;
            if (n == 0) return Quaternion.Identity;
            if (n == 1 || t <= Times[0]) return Values[0];
            if (t >= Times[n - 1]) return Values[n - 1];
            int i = KeyframeMath.SegmentIndex(Times, t);
            if (Mode == InterpolationMode.Step) return Values[i];
            float f = KeyframeMath.SegmentFraction(Times, i, t);
            return Quaternion.Normalize(Quaternion.Slerp(Values[i], Values[i + 1], f));
        }
    }

    /// <summary>The keyframe tracks for one animated joint (glTF node), any subset of translation / rotation / scale.
    /// Sampling overlays only the present channels onto the joint's rest pose, so a track that only animates
    /// rotation keeps the rest translation + scale.</summary>
    public sealed class JointTrack
    {
        /// <summary>The glTF logical node index this track targets (resolved to a skeleton node by
        /// <see cref="Skeleton.NodeForLogicalIndex"/>).</summary>
        public int TargetNode { get; }
        public Vector3Track? Translation { get; set; }
        public QuaternionTrack? Rotation { get; set; }
        public Vector3Track? Scale { get; set; }

        public JointTrack(int targetNode) { TargetNode = targetNode; }

        /// <summary>This joint's local pose at time <paramref name="t"/>: each present channel sampled, each absent
        /// channel taken from <paramref name="rest"/>.</summary>
        public JointPose SampleLocal(in JointPose rest, float t) => new JointPose
        {
            Translation = Translation is { } tr ? tr.Sample(t) : rest.Translation,
            Rotation = Rotation is { } ro ? ro.Sample(t) : rest.Rotation,
            Scale = Scale is { } sc ? sc.Sample(t) : rest.Scale,
        };

        public float Duration => MathF.Max(MathF.Max(Translation?.Duration ?? 0f, Rotation?.Duration ?? 0f), Scale?.Duration ?? 0f);
    }

    /// <summary>A named animation clip: per-joint TRS keyframe tracks + a duration (the longest channel). Read from a
    /// glTF by <see cref="GltfLoader.LoadAnimations"/>; sampled by <see cref="AnimationSampler"/>; advanced + blended
    /// by <see cref="AnimationPlayer"/>. Tracks are keyed by glTF logical node index via
    /// <see cref="JointTrack.TargetNode"/>.</summary>
    public sealed class AnimationClip
    {
        public string Name { get; }
        public float Duration { get; }
        public IReadOnlyList<JointTrack> Tracks { get; }

        public AnimationClip(string name, float duration, IReadOnlyList<JointTrack> tracks)
        {
            Name = name ?? string.Empty;
            Duration = duration;
            Tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
        }
    }

    static class KeyframeMath
    {
        /// <summary>Index <c>i</c> of the segment <c>[Times[i], Times[i+1])</c> containing <paramref name="t"/>.
        /// Assumes <c>Times[0] &lt; t &lt; Times[^1]</c> (the end clamps are handled by the caller).</summary>
        public static int SegmentIndex(float[] times, float t)
        {
            // Linear scan: keyframe tracks are short; avoids a binary-search off-by-one.
            int i = 0;
            while (i < times.Length - 2 && times[i + 1] <= t) i++;
            return i;
        }

        public static float SegmentFraction(float[] times, int i, float t)
        {
            float span = times[i + 1] - times[i];
            return span > 1e-9f ? (t - times[i]) / span : 0f;
        }
    }
}
