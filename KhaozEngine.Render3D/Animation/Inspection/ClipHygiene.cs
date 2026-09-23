using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Render3D.Animation.Inspection
{
    /// <summary>Policy used to inspect one animation clip's authored tracks and optional loop seam.</summary>
    public sealed record ClipHygieneOptions(
        IReadOnlySet<string> TranslationAllowed,
        IReadOnlySet<string>? AllowedNodes,
        bool Looping,
        float MinKeysPerSecond);

    /// <summary>One deterministic clip-policy finding.</summary>
    public readonly record struct ClipHygieneFinding(string Rule, string NodeName, string Detail);

    /// <summary>Pure, device-free checks over raw animation keys and sampled loop endpoints.</summary>
    public static class ClipHygiene
    {
        const float UnitLengthSquaredTolerance = 0.0001f;
        const float LoopComponentTolerance = 0.0001f;
        const float LoopRotationDotMinimum = 0.99999f;

        /// <summary>Check a clip against node, channel, key, density, and loop policy.</summary>
        public static IReadOnlyList<ClipHygieneFinding> Check(
            AnimationClip clip,
            Skeleton skeleton,
            ClipHygieneOptions options)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (skeleton is null) throw new ArgumentNullException(nameof(skeleton));
            if (options is null) throw new ArgumentNullException(nameof(options));
            if (options.TranslationAllowed is null)
                throw new ArgumentException("TranslationAllowed must not be null.", nameof(options));
            if (!float.IsFinite(options.MinKeysPerSecond) || options.MinKeysPerSecond < 0f)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.MinKeysPerSecond,
                    "MinKeysPerSecond must be finite and non-negative.");

            var findings = new List<ClipHygieneFinding>();
            var tracks = OrderedTracks(clip, skeleton);
            for (int i = 0; i < tracks.Count; i++)
                CheckTrack(tracks[i], clip.Duration, skeleton, options, findings);

            if (options.Looping)
                CheckLoop(clip, skeleton, findings);

            return findings;
        }

        static List<TrackEntry> OrderedTracks(AnimationClip clip, Skeleton skeleton)
        {
            var tracks = new List<TrackEntry>(clip.Tracks.Count);
            for (int sourceIndex = 0; sourceIndex < clip.Tracks.Count; sourceIndex++)
            {
                JointTrack track = clip.Tracks[sourceIndex];
                tracks.Add(new TrackEntry(
                    track,
                    sourceIndex,
                    skeleton.NodeForLogicalIndex(track.TargetNode)));
            }

            tracks.Sort(CompareTracks);
            return tracks;
        }

        static int CompareTracks(TrackEntry left, TrackEntry right)
        {
            bool leftResolved = left.Node >= 0;
            bool rightResolved = right.Node >= 0;
            if (leftResolved != rightResolved) return leftResolved ? -1 : 1;

            int position = leftResolved
                ? left.Node.CompareTo(right.Node)
                : left.Track.TargetNode.CompareTo(right.Track.TargetNode);
            return position != 0 ? position : left.SourceIndex.CompareTo(right.SourceIndex);
        }

        static void CheckTrack(
            TrackEntry entry,
            float duration,
            Skeleton skeleton,
            ClipHygieneOptions options,
            List<ClipHygieneFinding> findings)
        {
            JointTrack track = entry.Track;
            string nodeName = entry.Node >= 0 ? skeleton.NodeNames[entry.Node] : string.Empty;

            if (entry.Node < 0)
            {
                findings.Add(new ClipHygieneFinding(
                    "unknown-node",
                    string.Empty,
                    "logical-index=" + Integer(track.TargetNode)));
            }
            else
            {
                if (options.AllowedNodes is not null && !ContainsOrdinal(options.AllowedNodes, nodeName))
                    findings.Add(new ClipHygieneFinding("allowed-node", nodeName, "track outside AllowedNodes"));
                if (track.Translation is not null && !ContainsOrdinal(options.TranslationAllowed, nodeName))
                    findings.Add(new ClipHygieneFinding(
                        "translation",
                        nodeName,
                        "translation outside TranslationAllowed"));
            }

            if (track.Scale is not null)
                findings.Add(new ClipHygieneFinding("scale", nodeName, "scale channel present"));

            if (track.Rotation is not null)
                CheckRotationUnits(track.Rotation, nodeName, findings);

            if (track.Translation is not null)
                CheckTimes("translation", track.Translation.Times, nodeName, findings);
            if (track.Rotation is not null)
                CheckTimes("rotation", track.Rotation.Times, nodeName, findings);
            if (track.Scale is not null)
                CheckTimes("scale", track.Scale.Times, nodeName, findings);

            if (track.Translation is not null)
                CheckDensity("translation", track.Translation.Times.Length, duration, options, nodeName, findings);
            if (track.Rotation is not null)
                CheckDensity("rotation", track.Rotation.Times.Length, duration, options, nodeName, findings);
            if (track.Scale is not null)
                CheckDensity("scale", track.Scale.Times.Length, duration, options, nodeName, findings);
        }

        static void CheckRotationUnits(
            QuaternionTrack rotation,
            string nodeName,
            List<ClipHygieneFinding> findings)
        {
            for (int key = 0; key < rotation.Values.Length; key++)
            {
                Quaternion value = rotation.Values[key];
                float lengthSquared = value.LengthSquared();
                if (IsFinite(value) && MathF.Abs(lengthSquared - 1f) <= UnitLengthSquaredTolerance) continue;

                findings.Add(new ClipHygieneFinding(
                    "rotation-unit",
                    nodeName,
                    "rotation key=" + Integer(key)
                    + " time=" + Float(rotation.Times[key])
                    + " length-squared=" + Float(lengthSquared)));
            }
        }

        static void CheckTimes(
            string channel,
            float[] times,
            string nodeName,
            List<ClipHygieneFinding> findings)
        {
            for (int key = 0; key < times.Length; key++)
            {
                float time = times[key];
                if (!float.IsFinite(time))
                {
                    findings.Add(new ClipHygieneFinding(
                        "key-times",
                        nodeName,
                        channel + " key=" + Integer(key) + " time=" + Float(time) + " non-finite"));
                    continue;
                }

                if (key == 0 || !float.IsFinite(times[key - 1]) || time > times[key - 1]) continue;
                findings.Add(new ClipHygieneFinding(
                    "key-times",
                    nodeName,
                    channel + " key=" + Integer(key)
                    + " time=" + Float(time)
                    + " previous=" + Float(times[key - 1])
                    + " not-increasing"));
            }
        }

        static void CheckDensity(
            string channel,
            int keyCount,
            float duration,
            ClipHygieneOptions options,
            string nodeName,
            List<ClipHygieneFinding> findings)
        {
            if (!float.IsFinite(duration) || duration <= 0f)
            {
                findings.Add(new ClipHygieneFinding(
                    "key-density",
                    nodeName,
                    channel + " keys-per-second=undefined duration=" + Float(duration)
                    + " minimum=" + Float(options.MinKeysPerSecond)));
                return;
            }

            float actual = (keyCount - 1) / duration;
            if (actual >= options.MinKeysPerSecond) return;
            findings.Add(new ClipHygieneFinding(
                "key-density",
                nodeName,
                channel + " keys-per-second=" + Float(actual)
                + " minimum=" + Float(options.MinKeysPerSecond)));
        }

        static void CheckLoop(
            AnimationClip clip,
            Skeleton skeleton,
            List<ClipHygieneFinding> findings)
        {
            JointPose[] start = AnimationSampler.SamplePose(clip, skeleton, 0f);
            JointPose[] end = AnimationSampler.SamplePose(clip, skeleton, clip.Duration);
            for (int node = 0; node < skeleton.NodeCount; node++)
            {
                var mismatches = new List<string>(3);
                if (!Near(start[node].Translation, end[node].Translation)) mismatches.Add("translation");
                float rotationDot = MathF.Abs(Quaternion.Dot(start[node].Rotation, end[node].Rotation));
                if (!(rotationDot >= LoopRotationDotMinimum)) mismatches.Add("rotation");
                if (!Near(start[node].Scale, end[node].Scale)) mismatches.Add("scale");
                if (mismatches.Count == 0) continue;

                findings.Add(new ClipHygieneFinding(
                    "loop",
                    skeleton.NodeNames[node],
                    "mismatch=" + string.Join(",", mismatches)));
            }
        }

        static bool ContainsOrdinal(IReadOnlySet<string> values, string expected)
        {
            foreach (string value in values)
                if (string.Equals(value, expected, StringComparison.Ordinal)) return true;
            return false;
        }

        static bool Near(Vector3 left, Vector3 right) =>
            Near(left.X, right.X) && Near(left.Y, right.Y) && Near(left.Z, right.Z);

        static bool Near(float left, float right) => MathF.Abs(left - right) <= LoopComponentTolerance;

        static bool IsFinite(Quaternion value) =>
            float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W);

        static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);

        static string Float(float value)
        {
            if (float.IsNaN(value)) return "NaN";
            if (float.IsPositiveInfinity(value)) return "Infinity";
            if (float.IsNegativeInfinity(value)) return "-Infinity";
            string formatted = value.ToString("F6", CultureInfo.InvariantCulture);
            return formatted == "-0.000000" ? "0.000000" : formatted;
        }

        readonly record struct TrackEntry(JointTrack Track, int SourceIndex, int Node);
    }
}
