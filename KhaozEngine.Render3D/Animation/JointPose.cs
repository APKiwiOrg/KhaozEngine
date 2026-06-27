using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>A joint's local transform as a decomposed translation/rotation/scale (TRS), the unit animation
    /// keyframes interpolate and clips blend at. Composing a clip pose to a matrix uses the glTF / SharpGLTF order
    /// (scale, then rotation, then translation), which in the engine's row-vector System.Numerics convention is
    /// <c>CreateScale * CreateFromQuaternion * CreateTranslation</c>. Pure presentation: never feed a pose into
    /// simulation/RNG/netcode.</summary>
    public struct JointPose
    {
        public Vector3 Translation;
        public Quaternion Rotation;
        public Vector3 Scale;

        /// <summary>The no-op pose: zero translation, identity rotation, unit scale (<see cref="ToMatrix"/> == identity).</summary>
        public static JointPose Identity => new JointPose
        {
            Translation = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };

        /// <summary>The local transform matrix for this pose (scale, then rotate, then translate).</summary>
        public readonly Matrix4x4 ToMatrix() =>
            Matrix4x4.CreateScale(Scale)
            * Matrix4x4.CreateFromQuaternion(Rotation)
            * Matrix4x4.CreateTranslation(Translation);

        /// <summary>Per-channel interpolation between two poses by <paramref name="t"/> in [0,1]: translation and
        /// scale lerp componentwise, rotation slerps (shortest arc, re-normalized). The blend basis for crossfades
        /// and keyframe segments.</summary>
        public static JointPose Lerp(in JointPose a, in JointPose b, float t) => new JointPose
        {
            Translation = Vector3.Lerp(a.Translation, b.Translation, t),
            Rotation = Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, t)),
            Scale = Vector3.Lerp(a.Scale, b.Scale, t),
        };

        /// <summary>Decompose a local transform matrix into a pose. A non-decomposable (degenerate) matrix falls back
        /// to unit scale + identity rotation, keeping only the translation.</summary>
        public static JointPose FromMatrix(in Matrix4x4 m)
        {
            if (Matrix4x4.Decompose(m, out Vector3 scale, out Quaternion rot, out Vector3 trans))
                return new JointPose { Translation = trans, Rotation = rot, Scale = scale };
            return new JointPose { Translation = m.Translation, Rotation = Quaternion.Identity, Scale = Vector3.One };
        }
    }
}
