using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace KhaozEngine.Render3D.Animation.Inspection
{
    /// <summary>Writes deterministic, device-free text snapshots of sampled clip rotations and positions.</summary>
    public static class ClipReport
    {
        /// <summary>Write the canonical clip report in caller clip, phase, and position-node order.</summary>
        public static string Write(
            Skeleton skeleton,
            IReadOnlyList<AnimationClip> clips,
            IReadOnlyList<float> phases,
            IReadOnlyList<string> positionNodes)
        {
            if (skeleton is null) throw new ArgumentNullException(nameof(skeleton));
            if (clips is null) throw new ArgumentNullException(nameof(clips));
            if (phases is null) throw new ArgumentNullException(nameof(phases));
            if (positionNodes is null) throw new ArgumentNullException(nameof(positionNodes));

            var positionIndices = new int[positionNodes.Count];
            for (int i = 0; i < positionNodes.Count; i++)
                positionIndices[i] = skeleton.IndexOf(positionNodes[i]);

            var report = new StringBuilder("KhaozEngine ClipReport v1\n");
            var locals = new JointPose[skeleton.NodeCount];
            var probe = new PoseProbe(skeleton);
            for (int clipIndex = 0; clipIndex < clips.Count; clipIndex++)
            {
                AnimationClip clip = clips[clipIndex]
                    ?? throw new ArgumentException("Clips must not contain null.", nameof(clips));
                if (!float.IsFinite(clip.Duration) || clip.Duration < 0f)
                    throw new ArgumentException("Clip duration must be finite and non-negative.", nameof(clips));

                report.Append("clip\t").Append(Escape(clip.Name)).Append('\n');
                for (int phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
                {
                    float phase = phases[phaseIndex];
                    if (!float.IsFinite(phase) || phase < 0f || phase > 1f)
                        throw new ArgumentOutOfRangeException(
                            nameof(phases),
                            phase,
                            "Phases must be finite and in the closed range [0, 1].");

                    AnimationSampler.SampleInto(clip, skeleton, phase * clip.Duration, locals);
                    report.Append("phase\t").Append(Float(phase)).Append('\n');
                    AppendRotations(report, skeleton, locals);

                    probe.SetLocals(locals);
                    AppendPositions(report, skeleton, probe, positionNodes, positionIndices);
                }
            }

            return report.ToString();
        }

        static void AppendRotations(StringBuilder report, Skeleton skeleton, JointPose[] locals)
        {
            for (int node = 0; node < skeleton.NodeCount; node++)
            {
                Quaternion rotation = locals[node].Rotation;
                EnsureFinite(rotation, "Sampled rotation contains a non-finite component.");
                report.Append("rotation\t")
                    .Append(Escape(skeleton.NodeNames[node]))
                    .Append('\t').Append(Float(rotation.X))
                    .Append('\t').Append(Float(rotation.Y))
                    .Append('\t').Append(Float(rotation.Z))
                    .Append('\t').Append(Float(rotation.W))
                    .Append('\n');
            }
        }

        static void AppendPositions(
            StringBuilder report,
            Skeleton skeleton,
            PoseProbe probe,
            IReadOnlyList<string> positionNodes,
            int[] positionIndices)
        {
            for (int i = 0; i < positionNodes.Count; i++)
            {
                Vector3 position = probe.JointModel(positionIndices[i]).Translation;
                EnsureFinite(position, "Sampled position contains a non-finite component.");
                report.Append("position\t")
                    .Append(Escape(skeleton.NodeNames[positionIndices[i]]))
                    .Append('\t').Append(Float(position.X))
                    .Append('\t').Append(Float(position.Y))
                    .Append('\t').Append(Float(position.Z))
                    .Append('\n');
            }
        }

        static string Escape(string value)
        {
            var escaped = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '\t': escaped.Append("\\t"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\n': escaped.Append("\\n"); break;
                    default: escaped.Append(value[i]); break;
                }
            }
            return escaped.ToString();
        }

        static string Float(float value)
        {
            if (!float.IsFinite(value))
                throw new ArgumentException("Clip reports cannot contain non-finite values.");
            string formatted = value.ToString("F6", CultureInfo.InvariantCulture);
            return formatted == "-0.000000" ? "0.000000" : formatted;
        }

        static void EnsureFinite(Quaternion value, string message)
        {
            if (!float.IsFinite(value.X)
                || !float.IsFinite(value.Y)
                || !float.IsFinite(value.Z)
                || !float.IsFinite(value.W))
                throw new ArgumentException(message);
        }

        static void EnsureFinite(Vector3 value, string message)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                throw new ArgumentException(message);
        }
    }
}
