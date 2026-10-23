using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Profile-aware, GPU-free evaluation of already validated source vertices and prepared palettes.
    /// The linear profile retains the existing matrix blend and vertex evaluation unchanged.</summary>
    public static class SkinningEvaluation
    {
        public static Vector3 Position(in SkinnedVertex source, SkinningProfile profile,
            ReadOnlySpan<Matrix4x4> lbs, ReadOnlySpan<RigidDualQuaternion> dq)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return profile.Method switch
            {
                SkinningMethod.LinearBlend => Vector3.Transform(source.Position,
                    SkinningMath.BlendSkinMatrix(lbs, source.BoneIndices, source.BoneWeights)),
                SkinningMethod.DualQuaternionRigid => DualQuaternionSkinning.Position(source, dq),
                _ => throw new ArgumentOutOfRangeException(nameof(profile)),
            };
        }

        public static ModelVertex Vertex(in SkinnedVertex source, SkinningProfile profile,
            ReadOnlySpan<Matrix4x4> lbs, ReadOnlySpan<RigidDualQuaternion> dq)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return profile.Method switch
            {
                SkinningMethod.LinearBlend => SkinningMath.SkinVertex(source, lbs),
                SkinningMethod.DualQuaternionRigid => DualQuaternionSkinning.Vertex(source, dq),
                _ => throw new ArgumentOutOfRangeException(nameof(profile)),
            };
        }
    }
}
