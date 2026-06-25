using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Pure, GPU-free skinning math: composing a per-bone skin matrix from a joint world transform and
    /// the bone's inverse-bind, blending the 4-bone palette by weights, and normalizing weights. Scene3D skins
    /// meshes on the CPU through this, so the deform is headless-unit-testable. Presentation only: never feed
    /// skinning results into simulation/RNG/netcode.</summary>
    public static class SkinningMath
    {
        /// <summary>Max bones in one skinned mesh's palette (the per-skin bone cap). A skinned mesh
        /// (tentacle / limb / creature) must have at most this many joints: <see cref="BlendSkinMatrix"/>
        /// and the CPU bone-palette packing in <c>Scene3D</c> slot meshes at this stride, and the glTF
        /// loader rejects skins with more joints. (Was on the now-removed GPU SkinnedModelRenderer.)</summary>
        public const int MaxBonesPerDraw = 128;

        /// <summary>The model-space skinning matrix for one bone: <c>inverseBind * jointWorld</c>. When
        /// <paramref name="jointWorld"/> equals the bone's rest world transform this is the identity (no deform).</summary>
        public static Matrix4x4 Compose(Matrix4x4 jointWorld, Matrix4x4 inverseBind) => inverseBind * jointWorld;

        /// <summary>Blend up to 4 composed bone matrices by <paramref name="weights"/>, indexing
        /// <paramref name="composedBones"/> with the (float-encoded) <paramref name="indices"/>. A zero total
        /// weight returns the identity so an unrigged vertex is left in place. All four entries of
        /// <paramref name="indices"/> must be valid positions within <paramref name="composedBones"/> regardless
        /// of their weight because the reads are unconditional.</summary>
        public static Matrix4x4 BlendSkinMatrix(ReadOnlySpan<Matrix4x4> composedBones, Vector4 indices, Vector4 weights)
        {
            float total = weights.X + weights.Y + weights.Z + weights.W;
            if (total < 1e-8f) return Matrix4x4.Identity;
            Matrix4x4 m = default;
            m += composedBones[(int)indices.X] * weights.X;
            m += composedBones[(int)indices.Y] * weights.Y;
            m += composedBones[(int)indices.Z] * weights.Z;
            m += composedBones[(int)indices.W] * weights.W;
            return m;
        }

        /// <summary>CPU mirror of the skinned vertex shader's deform: blend the 4-bone skin matrix from
        /// <paramref name="composedBones"/> (this draw's palette, already inverseBind*jointWorld) and apply it to the
        /// vertex - position as a point, normal AND tangent as directions (re-normalized) - producing a rigid
        /// <see cref="ModelVertex"/> that the no-bone model pipeline draws. <c>Vector3.Transform</c> /
        /// <c>TransformNormal</c> reproduce the shader's <c>skin * pos</c> / <c>mat3(skin) * normal</c> exactly (the
        /// System.Numerics row-vector transform equals the std140 column-major read of the same uploaded matrix), so
        /// the CPU-skinned result is pixel-equal to correct GPU skinning. The tangent rides through the same skin
        /// rotation as the normal (so the deformed mesh's TBN tracks the pose) and keeps its handedness
        /// <c>w</c>. A zero source tangent stays zero (the no-TBN fallback), so a tangent-less skinned mesh yields a
        /// <see cref="ModelVertex"/> byte-identical to the pre-tangent path. Presentation only.</summary>
        public static ModelVertex SkinVertex(in SkinnedVertex v, ReadOnlySpan<Matrix4x4> composedBones)
        {
            Matrix4x4 skin = BlendSkinMatrix(composedBones, v.BoneIndices, v.BoneWeights);
            Vector3 p = Vector3.Transform(v.Position, skin);
            Vector3 n = Vector3.TransformNormal(v.Normal, skin);
            float len = n.Length();
            n = len > 1e-8f ? n / len : v.Normal;
            // Tangent: rotate the xyz by the same skin matrix (direction transform), preserve handedness w.
            // A zero source tangent (the common case: no normal map) stays zero, so the produced ModelVertex
            // carries Vector4.Zero exactly - the no-TBN fallback, bit-identical to the pre-tangent path.
            var tDir = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
            Vector4 tangent = Vector4.Zero;
            if (tDir.LengthSquared() > 1e-12f)
            {
                Vector3 td = Vector3.TransformNormal(tDir, skin);
                float tl = td.Length();
                td = tl > 1e-8f ? td / tl : tDir;
                tangent = new Vector4(td, v.Tangent.W);
            }
            return new ModelVertex(p, n, v.Color, v.Uv, tangent);
        }

        /// <summary>Normalize a 4-weight vector to sum to 1; an all-zero input stays zero (identity fallback).</summary>
        public static Vector4 NormalizeWeights(Vector4 w)
        {
            float total = w.X + w.Y + w.Z + w.W;
            return total < 1e-8f ? Vector4.Zero : w / total;
        }

        /// <summary>True only if every component of <paramref name="indices"/> (the float-encoded JOINTS_0
        /// 4-tuple) is an integer in <c>[0, boneCount)</c>. <see cref="BlendSkinMatrix"/> reads the palette
        /// unconditionally for all four bones, so a glTF whose JOINTS_0 carries an out-of-range index would
        /// index past the per-draw palette window at draw time (a crash mid-frame). A skinned-mesh loader
        /// validates each vertex with this so a malformed/malicious rig is rejected at load instead.</summary>
        public static bool AreBoneIndicesValid(Vector4 indices, int boneCount)
        {
            return InRange(indices.X) && InRange(indices.Y) && InRange(indices.Z) && InRange(indices.W);

            bool InRange(float f)
            {
                int i = (int)f;
                return i >= 0 && i < boneCount;
            }
        }

    }
}
