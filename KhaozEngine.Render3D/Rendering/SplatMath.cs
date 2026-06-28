using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>CPU mirror of the SplatFrag math (triplanar blend weights, planar mode, and the five-weight
    /// reconstruction from the packed vertex colour). Keep in sync with SplatFrag.</summary>
    public static class SplatMath
    {
        /// <summary>Triplanar blend weights from a surface normal, normalized to sum 1. Higher sharpness biases
        /// toward the dominant axis. Mirrors the shader's pow(abs(N), sharpness) normalize.</summary>
        public static Vector3 TriplanarBlend(Vector3 normal, float sharpness)
        {
            float s = MathF.Max(sharpness, 0.001f);
            var b = new Vector3(
                MathF.Pow(MathF.Abs(normal.X), s),
                MathF.Pow(MathF.Abs(normal.Y), s),
                MathF.Pow(MathF.Abs(normal.Z), s));
            float sum = b.X + b.Y + b.Z;
            return sum > 1e-5f ? b / sum : new Vector3(0f, 1f, 0f);
        }

        /// <summary>Planar (XZ-only) blend weights: project straight down.</summary>
        public static Vector3 PlanarBlend() => new(0f, 1f, 0f);

        /// <summary>Reconstruct the five normalized splat weights from a packed vertex colour (grass/dirt/rock/sand
        /// in rgba, snow = 1 - sum), renormalizing to guard interpolation drift. All-zero -> all grass.</summary>
        public static (float g, float d, float r, float s, float snow) UnpackWeights(Vector4 packed)
        {
            float g = packed.X, d = packed.Y, r = packed.Z, s = packed.W;
            float snow = Math.Clamp(1f - (g + d + r + s), 0f, 1f);
            float sum = g + d + r + s + snow;
            if (sum > 1e-5f) { g /= sum; d /= sum; r /= sum; s /= sum; snow /= sum; }
            else { g = 1f; d = r = s = snow = 0f; }
            return (g, d, r, s, snow);
        }
    }
}
