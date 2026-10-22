using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Prepares the version 1 rigid DQ palette with a pinned scalar binary32 operation graph.</summary>
    public static class DualQuaternionPalette
    {
        /// <summary>Composes inverse bind then joint world and validates the entire palette before publishing
        /// any destination element. Destination elements beyond the joint count are left untouched.</summary>
        public static void Prepare(ReadOnlySpan<Matrix4x4> jointWorld, ReadOnlySpan<Matrix4x4> inverseBind,
            Span<RigidDualQuaternion> destination, in SkinningValidationContext context)
        {
            if (jointWorld.Length != inverseBind.Length)
                throw SkinningValidation.InvalidPose(context, -1,
                    "Joint-world and inverse-bind counts must match.", nameof(inverseBind));
            if (jointWorld.Length > SkinningMath.MaxBonesPerDraw)
                throw SkinningValidation.InvalidPose(context, -1,
                    $"Joint count must be at most {SkinningMath.MaxBonesPerDraw}.", nameof(jointWorld));
            if (destination.Length < jointWorld.Length)
                throw SkinningValidation.InvalidPose(context, -1,
                    "Destination must hold the complete palette.", nameof(destination));

            // At most 128 * 32 bytes. A late bad joint or conversion overflow cannot publish a prefix.
            Span<RigidDualQuaternion> prepared = stackalloc RigidDualQuaternion[jointWorld.Length];
            for (int joint = 0; joint < jointWorld.Length; joint++)
            {
                Matrix4x4 composed = Compose(inverseBind[joint], jointWorld[joint]);
                prepared[joint] = FromComposed(composed, context, joint);
            }
            prepared.CopyTo(destination);
        }

        /// <summary>Validates a composed row-vector rigid transform, extracts and normalizes its real part,
        /// forms the dual part, then canonicalizes both halves by the first nonzero W/X/Y/Z component.</summary>
        public static RigidDualQuaternion FromComposed(in Matrix4x4 composed,
            in SkinningValidationContext context, int joint)
        {
            SkinningValidation.ValidateComposed(composed, context, joint);
            if (composed.IsIdentity)
                return new RigidDualQuaternion(new Vector4(0f, 0f, 0f, 1f), Vector4.Zero);

            // Graph step 2: each candidate has exactly three scalar adds/subtracts in this order.
            // Strict updates select W, X, Y, Z on ties. One root and 0.5/h extract the four components.
            float cw = ((1f + composed.M11) + composed.M22) + composed.M33;
            float cx = ((1f + composed.M11) - composed.M22) - composed.M33;
            float cy = ((1f - composed.M11) + composed.M22) - composed.M33;
            float cz = ((1f - composed.M11) - composed.M22) + composed.M33;
            float candidate = cw;
            int branch = 0;
            if (cx > candidate) { candidate = cx; branch = 1; }
            if (cy > candidate) { candidate = cy; branch = 2; }
            if (cz > candidate) { candidate = cz; branch = 3; }
            float h = MathF.Sqrt(candidate);
            float selected = 0.5f * h;
            float s = 0.5f / h;
            float x, y, z, w;
            switch (branch)
            {
                case 0:
                    w = selected;
                    x = (composed.M23 - composed.M32) * s;
                    y = (composed.M31 - composed.M13) * s;
                    z = (composed.M12 - composed.M21) * s;
                    break;
                case 1:
                    x = selected;
                    w = (composed.M23 - composed.M32) * s;
                    y = (composed.M12 + composed.M21) * s;
                    z = (composed.M13 + composed.M31) * s;
                    break;
                case 2:
                    y = selected;
                    w = (composed.M31 - composed.M13) * s;
                    x = (composed.M12 + composed.M21) * s;
                    z = (composed.M23 + composed.M32) * s;
                    break;
                default:
                    z = selected;
                    w = (composed.M12 - composed.M21) * s;
                    x = (composed.M13 + composed.M31) * s;
                    y = (composed.M23 + composed.M32) * s;
                    break;
            }

            // Graph step 3: four squares, three left-associated adds, one root, one reciprocal,
            // then four multiplies. Do not replace with vector normalization or fused arithmetic.
            float norm = (((x * x) + (y * y)) + (z * z)) + (w * w);
            float reciprocal = 1f / MathF.Sqrt(norm);
            x *= reciprocal;
            y *= reciprocal;
            z *= reciprocal;
            w *= reciprocal;

            // d=0.5*(t,0)*r: three products, two adds/subtracts and one half multiply per component.
            float tx = composed.M41, ty = composed.M42, tz = composed.M43;
            float dx = 0.5f * (((tx * w) + (ty * z)) - (tz * y));
            float dy = 0.5f * (((ty * w) + (tz * x)) - (tx * z));
            float dz = 0.5f * (((tz * w) + (tx * y)) - (ty * x));
            float dw = -0.5f * (((tx * x) + (ty * y)) + (tz * z));
            if (w < 0f || (w == 0f && (x < 0f || (x == 0f && (y < 0f || (y == 0f && z < 0f))))))
            {
                x = -x; y = -y; z = -z; w = -w;
                dx = -dx; dy = -dy; dz = -dz; dw = -dw;
            }

            var result = new RigidDualQuaternion(new Vector4(x, y, z, w), new Vector4(dx, dy, dz, dw));
            SkinningValidation.ValidatePreparedFinite(result, context, joint);
            return result;
        }

        // Graph step 1: all 16 coefficients use four scalar products and three left-associated adds.
        // No Matrix4x4/vector multiplication or implicit FMA substitutes. LBS Compose stays unchanged.
        static Matrix4x4 Compose(in Matrix4x4 a, in Matrix4x4 b) => new(
            Dot4(a.M11, a.M12, a.M13, a.M14, b.M11, b.M21, b.M31, b.M41),
            Dot4(a.M11, a.M12, a.M13, a.M14, b.M12, b.M22, b.M32, b.M42),
            Dot4(a.M11, a.M12, a.M13, a.M14, b.M13, b.M23, b.M33, b.M43),
            Dot4(a.M11, a.M12, a.M13, a.M14, b.M14, b.M24, b.M34, b.M44),
            Dot4(a.M21, a.M22, a.M23, a.M24, b.M11, b.M21, b.M31, b.M41),
            Dot4(a.M21, a.M22, a.M23, a.M24, b.M12, b.M22, b.M32, b.M42),
            Dot4(a.M21, a.M22, a.M23, a.M24, b.M13, b.M23, b.M33, b.M43),
            Dot4(a.M21, a.M22, a.M23, a.M24, b.M14, b.M24, b.M34, b.M44),
            Dot4(a.M31, a.M32, a.M33, a.M34, b.M11, b.M21, b.M31, b.M41),
            Dot4(a.M31, a.M32, a.M33, a.M34, b.M12, b.M22, b.M32, b.M42),
            Dot4(a.M31, a.M32, a.M33, a.M34, b.M13, b.M23, b.M33, b.M43),
            Dot4(a.M31, a.M32, a.M33, a.M34, b.M14, b.M24, b.M34, b.M44),
            Dot4(a.M41, a.M42, a.M43, a.M44, b.M11, b.M21, b.M31, b.M41),
            Dot4(a.M41, a.M42, a.M43, a.M44, b.M12, b.M22, b.M32, b.M42),
            Dot4(a.M41, a.M42, a.M43, a.M44, b.M13, b.M23, b.M33, b.M43),
            Dot4(a.M41, a.M42, a.M43, a.M44, b.M14, b.M24, b.M34, b.M44));

        static float Dot4(float a0, float a1, float a2, float a3, float b0, float b1, float b2, float b3) =>
            (((a0 * b0) + (a1 * b1)) + (a2 * b2)) + (a3 * b3);
    }
}
