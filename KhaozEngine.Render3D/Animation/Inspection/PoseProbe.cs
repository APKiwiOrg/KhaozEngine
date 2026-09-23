using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Animation.Inspection
{
    /// <summary>Pure, device-free sampling and model-space inspection for every node in a
    /// <see cref="Skeleton"/> hierarchy, including nodes outside its skin palette.</summary>
    public sealed class PoseProbe
    {
        readonly Skeleton _skeleton;
        readonly JointPose[] _locals;
        readonly Matrix4x4[] _modelByNode;

        public PoseProbe(Skeleton skeleton)
        {
            _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            _locals = new JointPose[skeleton.NodeCount];
            _modelByNode = new Matrix4x4[skeleton.NodeCount];
            SetLocals(skeleton.RestLocal);
        }

        /// <summary>Sample a clip at a normalised phase in the closed range [0, 1]. Phase 1 samples
        /// the authored end key and does not wrap to phase 0.</summary>
        public void SampleClip(AnimationClip clip, float normalisedPhase)
        {
            ValidateClip(clip);
            if (!float.IsFinite(normalisedPhase) || normalisedPhase < 0f || normalisedPhase > 1f)
                throw new ArgumentOutOfRangeException(nameof(normalisedPhase), normalisedPhase,
                    "Normalised phase must be finite and in the closed range [0, 1].");
            SampleClipAtValidatedSeconds(clip, normalisedPhase * clip.Duration);
        }

        /// <summary>Sample a clip at finite seconds, clamped to its closed authored time range.</summary>
        public void SampleClipAtSeconds(AnimationClip clip, float seconds)
        {
            ValidateClip(clip);
            if (!float.IsFinite(seconds))
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Seconds must be finite.");
            SampleClipAtValidatedSeconds(clip, Math.Clamp(seconds, 0f, clip.Duration));
        }

        /// <summary>Set one local pose per skeleton node and compose the complete node hierarchy.</summary>
        public void SetLocals(ReadOnlySpan<JointPose> localByNode)
        {
            if (localByNode.Length != _skeleton.NodeCount)
                throw new ArgumentException(
                    $"localByNode length {localByNode.Length} must equal node count {_skeleton.NodeCount}.",
                    nameof(localByNode));
            localByNode.CopyTo(_locals);
            ComposeModels();
        }

        /// <summary>Return a named node's current model-space matrix.</summary>
        public Matrix4x4 JointModel(string nodeName) => JointModel(_skeleton.IndexOf(nodeName));

        /// <summary>Return a node's current model-space matrix.</summary>
        public Matrix4x4 JointModel(int node)
        {
            if ((uint)node >= (uint)_modelByNode.Length)
                throw new ArgumentOutOfRangeException(nameof(node), node,
                    $"Node index must be in range [0, {_modelByNode.Length}).");
            return _modelByNode[node];
        }

        /// <summary>Return a named node's current model-space position.</summary>
        public Vector3 JointPosition(string nodeName) => JointModel(nodeName).Translation;

        void SampleClipAtValidatedSeconds(AnimationClip clip, float seconds)
        {
            AnimationSampler.SampleInto(clip, _skeleton, seconds, _locals);
            ComposeModels();
        }

        void ComposeModels()
        {
            for (int node = 0; node < _skeleton.NodeCount; node++)
            {
                Matrix4x4 local = _locals[node].ToMatrix();
                int parent = _skeleton.ParentIndices[node];
                _modelByNode[node] = parent < 0 ? local : local * _modelByNode[parent];
            }
        }

        static void ValidateClip(AnimationClip clip)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));
            if (!float.IsFinite(clip.Duration) || clip.Duration < 0f)
                throw new ArgumentException("Clip duration must be finite and non-negative.", nameof(clip));
        }
    }
}
