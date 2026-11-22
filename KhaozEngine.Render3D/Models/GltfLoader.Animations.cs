using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D
{
    public static partial class GltfLoader
    {
        /// <summary>Read every glTF animation as an <see cref="AnimationClip"/> (additive: leaves
        /// <see cref="Load"/> / <see cref="LoadSkinned(string)"/> unchanged). Each animation's channels are grouped by
        /// target node into a <see cref="JointTrack"/> (translation / rotation / scale), keyed by the node's glTF
        /// logical index so the clip resolves against the mesh <see cref="Skeleton"/>; per channel the sampler keys
        /// are read with their interpolation (STEP held, LINEAR interpolated, CUBICSPLINE reduced to its value keys
        /// and treated as linear). Clip duration is the latest key across all channels. A glb with no animations
        /// returns an empty list.</summary>
        public static IReadOnlyList<AnimationClip> LoadAnimations(string path) => ReadAnimations(LoadModel(path));

        /// <summary>Read the same clips as the path overload from binary glTF bytes.
        /// <paramref name="identity"/> names the asset in diagnostics.</summary>
        /// <exception cref="GltfImportDataException">The GLB is malformed or any image URI names an external
        /// resource, including an unused image. An external image refusal carries the asset identity and the
        /// zero-based image index.</exception>
        public static IReadOnlyList<AnimationClip> LoadAnimations(ReadOnlyMemory<byte> glb, string identity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            return TranslateModelErrors(identity, () => ReadAnimations(LoadModel(glb, identity, requireEmbeddedImages: true)));
        }

        static IReadOnlyList<AnimationClip> ReadAnimations(ModelRoot root)
        {
            var clips = new List<AnimationClip>(root.LogicalAnimations.Count);
            foreach (SharpGLTF.Schema2.Animation anim in root.LogicalAnimations)
            {
                var byNode = new Dictionary<int, JointTrack>();
                float duration = 0f;
                foreach (AnimationChannel channel in anim.Channels)
                {
                    Node? target = channel.TargetNode;
                    if (target == null) continue;
                    int logical = target.LogicalIndex;
                    if (!byNode.TryGetValue(logical, out JointTrack? track))
                    {
                        track = new JointTrack(logical);
                        byNode[logical] = track;
                    }

                    var ts = channel.GetTranslationSampler();
                    if (ts != null) { track.Translation = ReadVector3Track(ts, ref duration); continue; }
                    var rs = channel.GetRotationSampler();
                    if (rs != null) { track.Rotation = ReadQuaternionTrack(rs, ref duration); continue; }
                    var ss = channel.GetScaleSampler();
                    if (ss != null) { track.Scale = ReadVector3Track(ss, ref duration); continue; }
                    // A morph-weights channel (or any other path) is out of scope for skeletal playback: skip it.
                }

                // Drop a node whose only channels were skipped (no TRS track).
                var tracks = byNode.Values.Where(t => t.Translation != null || t.Rotation != null || t.Scale != null).ToList();
                clips.Add(new AnimationClip(anim.Name ?? string.Empty, duration, tracks));
            }
            return clips;
        }

        static Vector3Track ReadVector3Track(IAnimationSampler<Vector3> s, ref float duration)
        {
            var times = new List<float>();
            var values = new List<Vector3>();
            if (s.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
                foreach (var (key, value) in s.GetCubicKeys()) { times.Add(key); values.Add(value.Value); }
            else
                foreach (var (key, value) in s.GetLinearKeys()) { times.Add(key); values.Add(value); }
            if (times.Count > 0) duration = MathF.Max(duration, times[times.Count - 1]);
            return new Vector3Track(times.ToArray(), values.ToArray(), MapInterp(s.InterpolationMode));
        }

        static QuaternionTrack ReadQuaternionTrack(IAnimationSampler<Quaternion> s, ref float duration)
        {
            var times = new List<float>();
            var values = new List<Quaternion>();
            if (s.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
                foreach (var (key, value) in s.GetCubicKeys()) { times.Add(key); values.Add(value.Value); }
            else
                foreach (var (key, value) in s.GetLinearKeys()) { times.Add(key); values.Add(value); }
            if (times.Count > 0) duration = MathF.Max(duration, times[times.Count - 1]);
            return new QuaternionTrack(times.ToArray(), values.ToArray(), MapInterp(s.InterpolationMode));
        }

        static InterpolationMode MapInterp(AnimationInterpolationMode m) =>
            m == AnimationInterpolationMode.STEP ? InterpolationMode.Step : InterpolationMode.Linear;
    }
}
