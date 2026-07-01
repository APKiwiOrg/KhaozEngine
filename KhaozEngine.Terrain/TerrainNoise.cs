using System;

namespace KhaozEngine.Terrain
{
    /// <summary>
    /// Stateless coordinate-hash noise for the analytic terrain field. Every function depends only on its
    /// arguments (lattice coords / world position + seed), never on call order or loaded state, so the height
    /// at a world point is identical regardless of which chunks are streamed in. Plain float (authoritative
    /// server and visual client evaluate the same math; tiny cross-platform float differences are corrected by
    /// replication, per the terrain design decision).
    /// </summary>
    public static class TerrainNoise
    {
        /// <summary>Clamped Hermite smoothstep; forwards to <see cref="KhaozEngine.Primitives.MathUtil.SmoothStep"/> (single implementation, kept here so noise call sites read cohesively).</summary>
        public static float SmoothStep(float a, float b, float x)
            => KhaozEngine.Primitives.MathUtil.SmoothStep(a, b, x);

        /// <summary>Deterministic hash of an integer lattice point + seed to [-1, 1). Integer bit-mix (no Random).</summary>
        public static float Hash2(int gx, int gz, int seed)
        {
            unchecked
            {
                uint h = (uint)seed * 0x9E3779B1u;
                h ^= (uint)gx * 0x85EBCA77u;
                h = (h << 13) | (h >> 19);
                h ^= (uint)gz * 0xC2B2AE3Du;
                h *= 0x27D4EB2Fu;
                h ^= h >> 15;
                // map [0, 2^32) -> [-1, 1)
                return (h / 4294967295f) * 2f - 1f;
            }
        }

        /// <summary>Bilinearly-interpolated value noise in [-1, 1]. Smoothstep fade for C1 continuity at the lattice.</summary>
        public static float ValueNoise(float x, float z, int seed)
        {
            int x0 = (int)MathF.Floor(x);
            int z0 = (int)MathF.Floor(z);
            float fx = x - x0;
            float fz = z - z0;
            float u = fx * fx * (3f - 2f * fx);
            float v = fz * fz * (3f - 2f * fz);

            float n00 = Hash2(x0, z0, seed);
            float n10 = Hash2(x0 + 1, z0, seed);
            float n01 = Hash2(x0, z0 + 1, seed);
            float n11 = Hash2(x0 + 1, z0 + 1, seed);

            float nx0 = n00 + (n10 - n00) * u;
            float nx1 = n01 + (n11 - n01) * u;
            return nx0 + (nx1 - nx0) * v;
        }

        /// <summary>Signed fractional Brownian motion: summed octaves of value noise, normalized to ~[-1, 1].</summary>
        public static float Fbm(float x, float z, int seed, int octaves = 4, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * ValueNoise(x * freq, z * freq, seed + o * 1013);
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>Turbulence: summed |octaves|, normalized to ~[0, 1]. Non-negative, so it only raises terrain.</summary>
        public static float Turbulence(float x, float z, int seed, int octaves = 4, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * MathF.Abs(ValueNoise(x * freq, z * freq, seed + o * 1013));
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }
    }
}
