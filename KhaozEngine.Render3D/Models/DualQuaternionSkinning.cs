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
            Vector4 weights = source.BoneWeights;
            float total = ((weights.X + weights.Y) + weights.Z) + weights.W;
            if (total < 1e-8f || ContributingPaletteIsIdentity(source.BoneIndices, weights, palette))
                return source.Position;

            Blend(source.BoneIndices, weights, palette, out Vector4 real, out Vector4 dual);
            float x = real.X, y = real.Y, z = real.Z, w = real.W;
            // Step 4 norm: four squares and three left-associated adds. This is a tiny-arithmetic guard,
            // not the proposed 1/8 domain floor. That floor remains subject to the S2.6 proof.
            float xx = x * x, yy = y * y, zz = z * z, ww = w * w;
            float n = ((xx + yy) + zz) + ww;
            if (!float.IsFinite(n) || n < 1e-8f)
                throw new InvalidOperationException("Rigid DQ evaluation requires a finite, non-tiny real norm.");
            float inverseNorm = 1f / n;

            // Step 5: six shared products, three diagonals (three adds/subtracts each), six off-diagonals
            // (one add/subtract and one doubling each). These are unnormalized Hamilton numerators.
            // Do not use unit-quaternion shortcuts, vector arithmetic or fused multiply-add substitutes.
            float xy = x * y, xz = x * z, yz = y * z;
            float wx = w * x, wy = w * y, wz = w * z;
            float m11 = ((ww + xx) - yy) - zz;
            float m12 = 2f * (xy - wz);
            float m13 = 2f * (xz + wy);
            float m21 = 2f * (xy + wz);
            float m22 = ((ww - xx) + yy) - zz;
            float m23 = 2f * (yz - wx);
            float m31 = 2f * (xz - wy);
            float m32 = 2f * (yz + wx);
            float m33 = ((ww - xx) - yy) + zz;
            Vector3 p = source.Position;
            // Three products and two left-associated adds per row.
            float rx = ((m11 * p.X) + (m12 * p.Y)) + (m13 * p.Z);
            float ry = ((m21 * p.X) + (m22 * p.Y)) + (m23 * p.Z);
            float rz = ((m31 * p.X) + (m32 * p.Y)) + (m33 * p.Z);
            // Four products, three ordered adds/subtracts and one doubling per translation component.
            float dx = dual.X, dy = dual.Y, dz = dual.Z, dw = dual.W;
            float tx = 2f * ((((dx * w) - (dw * x)) - (dy * z)) + (dz * y));
            float ty = 2f * ((((dy * w) - (dw * y)) - (dz * x)) + (dx * z));
            float tz = 2f * ((((dz * w) - (dw * z)) - (dx * y)) + (dy * x));
            // One reciprocal in total. Rotation and translation each multiply it before their final add.
            float px = (rx * inverseNorm) + (tx * inverseNorm);
            float py = (ry * inverseNorm) + (ty * inverseNorm);
            float pz = (rz * inverseNorm) + (tz * inverseNorm);
            if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz))
                throw new InvalidOperationException("Rigid DQ position arithmetic must remain finite.");
            return new Vector3(px, py, pz);
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
