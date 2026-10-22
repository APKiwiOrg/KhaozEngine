using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Version 1 ordered rigid DQ evaluation. Sources and palettes must already be validated.</summary>
    public static class DualQuaternionSkinning
    {
        /// <summary>Transforms a model-space position using a prepared rigid palette. Unweighted and exact
        /// identity inputs preserve the source words. Invalid arithmetic refuses presentation.</summary>
        public static Vector3 Position(in SkinnedVertex source, ReadOnlySpan<RigidDualQuaternion> palette)
        {
            if (!TryBlend(source, palette, out Vector4 real, out Vector4 dual)) return source.Position;
            var rotation = new BlendedRotation(real);
            return TransformPosition(source.Position, rotation, real, dual);
        }

        /// <summary>Transforms a complete vertex with one ordered blend. Directions use only the rigid
        /// rotation, with the existing small-direction and absent-tangent fallback semantics.</summary>
        public static ModelVertex Vertex(in SkinnedVertex source, ReadOnlySpan<RigidDualQuaternion> palette)
        {
            bool transformed = TryBlend(source, palette, out Vector4 real, out Vector4 dual);
            var rotation = transformed ? new BlendedRotation(real) : default;
            Vector3 position = transformed ? TransformPosition(source.Position, rotation, real, dual) : source.Position;
            Vector3 normal = NormalizeDirection(transformed ? rotation.Apply(source.Normal) : source.Normal, source.Normal);
            var sourceTangent = new Vector3(source.Tangent.X, source.Tangent.Y, source.Tangent.Z);
            Vector4 tangent = Vector4.Zero;
            // Three squares and two left-associated adds. The strict source threshold keeps all four
            // tangent words at positive zero when no usable tangent is present, regardless of source W.
            float tangentSquared = ((sourceTangent.X * sourceTangent.X) + (sourceTangent.Y * sourceTangent.Y))
                + (sourceTangent.Z * sourceTangent.Z);
            if (!float.IsFinite(tangentSquared))
                throw new InvalidOperationException("Rigid DQ tangent arithmetic must remain finite.");
            if (tangentSquared > 1e-12f)
            {
                Vector3 direction = NormalizeDirection(transformed ? rotation.Apply(sourceTangent) : sourceTangent, sourceTangent);
                tangent = new Vector4(direction, source.Tangent.W);
            }
            return new ModelVertex(position, normal, source.Color, source.Uv, tangent);
        }

        static Vector3 NormalizeDirection(Vector3 direction, Vector3 fallback)
        {
            // Three squares, two left-associated adds, one root and three divisions. Directions never
            // use the translation numerator. Match SkinningMath's strict length > 1e-8 fallback.
            float squared = ((direction.X * direction.X) + (direction.Y * direction.Y)) + (direction.Z * direction.Z);
            if (!float.IsFinite(squared))
                throw new InvalidOperationException("Rigid DQ direction arithmetic must remain finite.");
            float length = MathF.Sqrt(squared);
            return length > 1e-8f ? new Vector3(direction.X / length, direction.Y / length, direction.Z / length) : fallback;
        }

        static bool TryBlend(in SkinnedVertex source, ReadOnlySpan<RigidDualQuaternion> palette,
            out Vector4 real, out Vector4 dual)
        {
            real = dual = Vector4.Zero;
            Vector4 weights = source.BoneWeights;
            float total = ((weights.X + weights.Y) + weights.Z) + weights.W;
            if (total < 1e-8f || ContributingPaletteIsIdentity(source.BoneIndices, weights, palette))
                return false;
            Blend(source.BoneIndices, weights, palette, out real, out dual);
            return true;
        }

        static Vector3 TransformPosition(Vector3 p, BlendedRotation rotation, Vector4 real, Vector4 dual)
        {
            Vector3 rotated = rotation.Apply(p);
            // Four products, three ordered adds/subtracts and one doubling per translation component.
            float x = real.X, y = real.Y, z = real.Z, w = real.W;
            float dx = dual.X, dy = dual.Y, dz = dual.Z, dw = dual.W;
            float tx = 2f * ((((dx * w) - (dw * x)) - (dy * z)) + (dz * y));
            float ty = 2f * ((((dy * w) - (dw * y)) - (dz * x)) + (dx * z));
            float tz = 2f * ((((dz * w) - (dw * z)) - (dx * y)) + (dy * x));
            // One reciprocal in total. Rotation and translation each multiply it before their final add.
            float px = rotated.X + (tx * rotation.InverseNorm);
            float py = rotated.Y + (ty * rotation.InverseNorm);
            float pz = rotated.Z + (tz * rotation.InverseNorm);
            if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz))
                throw new InvalidOperationException("Rigid DQ position arithmetic must remain finite.");
            return new Vector3(px, py, pz);
        }

        readonly struct BlendedRotation
        {
            readonly float _m11, _m12, _m13, _m21, _m22, _m23, _m31, _m32, _m33;
            public float InverseNorm { get; }

            public BlendedRotation(Vector4 real)
            {
                float x = real.X, y = real.Y, z = real.Z, w = real.W;
                // Step 4 norm: four squares and three left-associated adds. This is a tiny-arithmetic guard,
                // not the proposed 1/8 domain floor. That floor remains subject to the S2.6 proof.
                float xx = x * x, yy = y * y, zz = z * z, ww = w * w;
                float n = ((xx + yy) + zz) + ww;
                if (!float.IsFinite(n) || n < 1e-8f)
                    throw new InvalidOperationException("Rigid DQ evaluation requires a finite, non-tiny real norm.");
                InverseNorm = 1f / n;
                // Step 5: six shared products, three diagonals (three adds/subtracts each), six off-diagonals
                // (one add/subtract and one doubling each). These are unnormalized Hamilton numerators.
                // Do not use unit-quaternion shortcuts, vector arithmetic or fused multiply-add substitutes.
                float xy = x * y, xz = x * z, yz = y * z;
                float wx = w * x, wy = w * y, wz = w * z;
                _m11 = ((ww + xx) - yy) - zz;
                _m12 = 2f * (xy - wz);
                _m13 = 2f * (xz + wy);
                _m21 = 2f * (xy + wz);
                _m22 = ((ww - xx) + yy) - zz;
                _m23 = 2f * (yz - wx);
                _m31 = 2f * (xz - wy);
                _m32 = 2f * (yz + wx);
                _m33 = ((ww - xx) - yy) + zz;
            }

            public Vector3 Apply(Vector3 p)
            {
                // Three products, two left-associated adds and one inverse-norm multiplication per row.
                float rx = ((_m11 * p.X) + (_m12 * p.Y)) + (_m13 * p.Z);
                float ry = ((_m21 * p.X) + (_m22 * p.Y)) + (_m23 * p.Z);
                float rz = ((_m31 * p.X) + (_m32 * p.Y)) + (_m33 * p.Z);
                return new Vector3(rx * InverseNorm, ry * InverseNorm, rz * InverseNorm);
            }
        }

        internal static void Blend(Vector4 indices, Vector4 weights, ReadOnlySpan<RigidDualQuaternion> palette,
            out Vector4 real, out Vector4 dual)
        {
            real = Vector4.Zero;
            dual = Vector4.Zero;
            // Step 4: stored X/Y/Z/W order, without sorting or coalescing duplicate indices.
            for (int slot = 0; slot < 4; slot++)
            {
                float weight = weights[slot];
                if (weight <= 0f) continue;
                RigidDualQuaternion bone = palette[(int)indices[slot]];
                float x = bone.Real.X, y = bone.Real.Y, z = bone.Real.Z, w = bone.Real.W;
                float dx = bone.Dual.X, dy = bone.Dual.Y, dz = bone.Dual.Z, dw = bone.Dual.W;
                // Four products and three left-associated adds against the current running real sum.
                // A zero dot retains both halves, including on the first contributing slot.
                float dot = (((x * real.X) + (y * real.Y)) + (z * real.Z)) + (w * real.W);
                if (dot < 0f)
                {
                    x = -x; y = -y; z = -z; w = -w;
                    dx = -dx; dy = -dy; dz = -dz; dw = -dw;
                }
                // Eight separate weighted products and eight separate scalar adds per positive slot.
                real.X += x * weight;
                real.Y += y * weight;
                real.Z += z * weight;
                real.W += w * weight;
                dual.X += dx * weight;
                dual.Y += dy * weight;
                dual.Z += dz * weight;
                dual.W += dw * weight;
            }
        }

        static bool ContributingPaletteIsIdentity(Vector4 indices, Vector4 weights,
            ReadOnlySpan<RigidDualQuaternion> palette)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                if (weights[slot] <= 0f) continue;
                RigidDualQuaternion bone = palette[(int)indices[slot]];
                Vector4 r = bone.Real, d = bone.Dual;
                if (r.X != 0f || r.Y != 0f || r.Z != 0f || (r.W != 1f && r.W != -1f) ||
                    d.X != 0f || d.Y != 0f || d.Z != 0f || d.W != 0f)
                    return false;
            }
            return true;
        }
    }
}
