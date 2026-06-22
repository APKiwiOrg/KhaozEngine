using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Pure, GPU-free mirror of the model fragment shader's PBR-lite surface math: decoding a
    /// tangent-space normal sample, perturbing the geometric normal through a TBN built from an interpolated
    /// tangent, and modulating Blinn-Phong specular by roughness. This documents the intended math and makes
    /// it headless-unit-testable; ModelFrag (Internal/ShaderSources.cs) MUST mirror it. Presentation only.</summary>
    public static class SurfaceShading
    {
        /// <summary>Specular exponent at full roughness (the broad-highlight floor). The exponent eases from
        /// the per-instance shininess (roughness 0) down to this (roughness 1).</summary>
        public const float MinSpecExponent = 8f;

        /// <summary>Decode an RGB normal-map sample (each channel 0..1) to a tangent-space normal (-1..1).</summary>
        public static Vector3 DecodeNormalSample(Vector3 rgb) => rgb * 2f - Vector3.One;

        /// <summary>Perturb <paramref name="geoNormal"/> by a tangent-space normal using
        /// <paramref name="tangent"/> (xyz = model-space tangent, w = +/-1 handedness). A zero/degenerate
        /// tangent returns the (normalized) geometric normal unchanged - the no-TBN fallback. With a flat
        /// sample (0,0,1) the result is the geometric normal.</summary>
        public static Vector3 PerturbNormal(Vector3 geoNormal, Vector4 tangent, Vector3 tangentSpaceNormal)
        {
            Vector3 N = SafeNormalize(geoNormal);
            var t = new Vector3(tangent.X, tangent.Y, tangent.Z);
            if (t.LengthSquared() <= 1e-10f) return N;
            Vector3 T = SafeNormalize(t);
            T = SafeNormalize(T - N * Vector3.Dot(N, T));     // Gram-Schmidt
            Vector3 B = Vector3.Cross(N, T) * tangent.W;       // handedness
            // mat3(T,B,N) * nTS  (columns are T, B, N).
            Vector3 perturbed = T * tangentSpaceNormal.X + B * tangentSpaceNormal.Y + N * tangentSpaceNormal.Z;
            return SafeNormalize(perturbed);
        }

        /// <summary>Modulate the per-instance Blinn-Phong spec by roughness (0..1): strength scales by
        /// (1 - rough); the exponent eases from <paramref name="baseExponent"/> to
        /// <see cref="MinSpecExponent"/>, clamped to at least 1. Roughness 0 returns the inputs unchanged.</summary>
        public static (float strength, float exponent) ApplyRoughness(float baseStrength, float baseExponent, float rough)
        {
            float strength = baseStrength * (1f - rough);
            float exponent = MathF.Max(baseExponent + (MinSpecExponent - baseExponent) * rough, 1f);
            return (strength, exponent);
        }

        static Vector3 SafeNormalize(Vector3 v)
        {
            float len = v.Length();
            return len > 1e-8f ? v / len : v;
        }
    }
}
