using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Where a posed skin meets the floor: the lowest world height of its DEFORMED vertices, not of its rest
    /// box, so a body lowered into a crouch or lifted on a stride can be set down on the ground it stands on.</summary>
    /// <remarks>The LBS path folds each bone's skinning matrix with the model once and reduces it to the column
    /// that writes world y. The DQ path evaluates source positions through the selected profile before applying
    /// model placement. Called per body per frame, so it allocates nothing. Pure presentation. GPU-free.</remarks>
    public static class SkinnedGrounding
    {
        /// <summary>The lowest world y of a skin deformed by <paramref name="palette"/> and drawn through
        /// <paramref name="model"/>, or positive infinity for a skin with no vertices.</summary>
        /// <param name="vertices">The skin's vertices. Each one's four bone indices must be valid palette positions,
        /// as <see cref="SkinningMath.BlendSkinMatrix"/> requires. A vertex whose weights total less than 1e-8 is
        /// unweighted and draws undeformed, through the model alone, the shader's own fallback.</param>
        /// <param name="inverseBind">The skin's inverse-bind matrix per bone, one per palette entry.</param>
        /// <param name="palette">The joint-WORLD matrix per bone, as <see cref="Skeleton.ComposeInto"/> writes it and
        /// the skinned draw takes it.</param>
        /// <param name="model">The model matrix the skin is drawn through.</param>
        /// <param name="scratch">At least one entry per bone. Overwritten.</param>
        /// <exception cref="ArgumentException"><paramref name="inverseBind"/> does not pair with
        /// <paramref name="palette"/>, or <paramref name="scratch"/> is shorter than it.</exception>
        public static float MinimumY(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<Matrix4x4> inverseBind,
            ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, Span<Vector4> scratch)
        {
            if (inverseBind.Length != palette.Length)
                throw new ArgumentException($"inverseBind has {inverseBind.Length} bones and palette has"
                    + $" {palette.Length}.", nameof(inverseBind));
            if (scratch.Length < palette.Length)
                throw new ArgumentException($"scratch holds {scratch.Length} bones and palette has {palette.Length}.",
                    nameof(scratch));

            // Sliced to the palette, so a bone index past it fails rather than reading a stale column.
            Span<Vector4> heights = scratch[..palette.Length];
            for (int bone = 0; bone < palette.Length; bone++)
            {
                Matrix4x4 skin = inverseBind[bone] * palette[bone] * model;
                heights[bone] = new Vector4(skin.M12, skin.M22, skin.M32, skin.M42);
            }
            return MinimumY(vertices, heights, model);
        }

        /// <summary>The lowest world y using the skin's profile and its already prepared palette, or positive
        /// infinity for a skin with no vertices. Model placement may include nonuniform scale.</summary>
        /// <param name="vertices">Validated source vertices for this skin and palette.</param>
        /// <param name="profile">The skin's immutable deformation profile.</param>
        /// <param name="composedLbs">Inverse-bind times joint-world matrices for LBS, unused for DQ.</param>
        /// <param name="preparedDq">Validated, prepared rigid DQ palette, unused for LBS.</param>
        /// <param name="model">The separate model placement transform.</param>
        public static float MinimumY(ReadOnlySpan<SkinnedVertex> vertices, SkinningProfile profile,
            ReadOnlySpan<Matrix4x4> composedLbs, ReadOnlySpan<RigidDualQuaternion> preparedDq, in Matrix4x4 model)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (profile.Method == SkinningMethod.LinearBlend)
            {
                Span<Vector4> heights = stackalloc Vector4[composedLbs.Length];
                for (int bone = 0; bone < composedLbs.Length; bone++)
                {
                    Matrix4x4 skin = composedLbs[bone] * model;
                    heights[bone] = new Vector4(skin.M12, skin.M22, skin.M32, skin.M42);
                }
                return MinimumY(vertices, heights, model);
            }

            float minimum = float.PositiveInfinity;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 position = SkinningEvaluation.Position(vertices[i], profile, composedLbs, preparedDq);
                minimum = MathF.Min(minimum, Vector3.Transform(position, model).Y);
            }
            return minimum;
        }

        static float MinimumY(ReadOnlySpan<SkinnedVertex> vertices, ReadOnlySpan<Vector4> heights,
            in Matrix4x4 model)
        {
            var fromModel = new Vector4(model.M12, model.M22, model.M32, model.M42);
            float minimum = float.PositiveInfinity;
            for (int i = 0; i < vertices.Length; i++)
            {
                ref readonly SkinnedVertex vertex = ref vertices[i];
                var position = new Vector4(vertex.Position, 1f);
                Vector4 weights = vertex.BoneWeights;
                Vector4 bones = vertex.BoneIndices;
                float y = weights.X + weights.Y + weights.Z + weights.W < UnweightedTotal
                    ? Vector4.Dot(position, fromModel)
                    : weights.X * Vector4.Dot(position, heights[(int)bones.X])
                      + weights.Y * Vector4.Dot(position, heights[(int)bones.Y])
                      + weights.Z * Vector4.Dot(position, heights[(int)bones.Z])
                      + weights.W * Vector4.Dot(position, heights[(int)bones.W]);
                minimum = MathF.Min(minimum, y);
            }
            return minimum;
        }

        // The weight total below which SkinningMath.BlendSkinMatrix and the skinned shader leave a vertex undeformed.
        const float UnweightedTotal = 1e-8f;
    }
}
