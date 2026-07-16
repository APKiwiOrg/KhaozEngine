using System;
using System.Numerics;

namespace KhaozEngine.Particles;

/// <summary>
/// Deterministic, allocation-free curl-flavoured noise for particle turbulence. Pure float math driven by a
/// polynomial integer hash (no trig-based hashing, no lookup tables, no wall clock), so the field is
/// reproducible across builds for a fixed position, time and seed.
/// </summary>
public static class ParticleNoise
{
    /// <summary>
    /// A divergence-poor turbulence vector at <paramref name="p"/> and time <paramref name="t"/>, decorrelated
    /// per particle by <paramref name="seed"/>. Built as the finite-difference curl of a three-channel value-noise
    /// vector potential, so the flow swirls rather than sources or sinks.
    /// </summary>
    public static Vector3 Curl(Vector3 p, float t, float seed)
    {
        const float e = 0.1f;

        uint sb = BitConverter.SingleToUInt32Bits(seed);
        uint c0 = Mix(sb, 0u);
        uint c1 = Mix(sb, 1u);
        uint c2 = Mix(sb, 2u);

        // Flow the sampling domain with time so the field animates at a fixed point. The shift is uniform across
        // the finite-difference neighbourhood, so the curl still approximates the spatial derivative.
        Vector3 flow = new Vector3(0.16f, 0.11f, 0.13f) * t;
        Vector3 q = p - flow;

        Vector3 dx = new(e, 0f, 0f);
        Vector3 dy = new(0f, e, 0f);
        Vector3 dz = new(0f, 0f, e);

        Vector3 px0 = Potential(q - dx, c0, c1, c2);
        Vector3 px1 = Potential(q + dx, c0, c1, c2);
        Vector3 py0 = Potential(q - dy, c0, c1, c2);
        Vector3 py1 = Potential(q + dy, c0, c1, c2);
        Vector3 pz0 = Potential(q - dz, c0, c1, c2);
        Vector3 pz1 = Potential(q + dz, c0, c1, c2);

        float cx = (py1.Z - py0.Z) - (pz1.Y - pz0.Y);
        float cy = (pz1.X - pz0.X) - (px1.Z - px0.Z);
        float cz = (px1.Y - px0.Y) - (py1.X - py0.X);

        return new Vector3(cx, cy, cz) * (1f / (2f * e));
    }

    /// <summary>The three-channel potential (one value-noise field per output component) at a point.</summary>
    private static Vector3 Potential(Vector3 q, uint c0, uint c1, uint c2)
        => new(ValueNoise(q, c0), ValueNoise(q, c1), ValueNoise(q, c2));

    /// <summary>Trilinearly interpolated integer-hash value noise in [-1, 1] for the given channel.</summary>
    private static float ValueNoise(Vector3 q, uint channel)
    {
        float fx = MathF.Floor(q.X);
        float fy = MathF.Floor(q.Y);
        float fz = MathF.Floor(q.Z);
        int xi = (int)fx;
        int yi = (int)fy;
        int zi = (int)fz;

        float u = Fade(q.X - fx);
        float v = Fade(q.Y - fy);
        float w = Fade(q.Z - fz);

        float c000 = Corner(xi, yi, zi, channel);
        float c100 = Corner(xi + 1, yi, zi, channel);
        float c010 = Corner(xi, yi + 1, zi, channel);
        float c110 = Corner(xi + 1, yi + 1, zi, channel);
        float c001 = Corner(xi, yi, zi + 1, channel);
        float c101 = Corner(xi + 1, yi, zi + 1, channel);
        float c011 = Corner(xi, yi + 1, zi + 1, channel);
        float c111 = Corner(xi + 1, yi + 1, zi + 1, channel);

        float x00 = Lerp(c000, c100, u);
        float x10 = Lerp(c010, c110, u);
        float x01 = Lerp(c001, c101, u);
        float x11 = Lerp(c011, c111, u);
        float y0 = Lerp(x00, x10, v);
        float y1 = Lerp(x01, x11, v);
        return Lerp(y0, y1, w);
    }

    /// <summary>Quintic fade (smootherstep) for continuous first derivatives across cell boundaries.</summary>
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Hash an integer lattice corner + channel to a value in [-1, 1).</summary>
    private static float Corner(int x, int y, int z, uint channel)
    {
        uint h = (uint)x * 0x8DA6B343u;
        h += (uint)y * 0xD8163841u;
        h += (uint)z * 0xCB1AB31Fu;
        h += channel * 0x165667B1u;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return (h >> 8) * (1f / 16777216.0f) * 2f - 1f;
    }

    /// <summary>Decorrelate the three potential channels from a per-particle seed.</summary>
    private static uint Mix(uint seedBits, uint component)
    {
        uint h = seedBits * 0x9E3779B9u + component * 0x85EBCA6Bu + 0x165667B1u;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h | 1u;
    }
}
