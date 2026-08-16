using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>Turns catalog colours into the vertex colours the ground mesher writes: hex parsing, a
/// deterministic per-tile brightness jitter (the soft OSRS ground variation) and the corner blend that
/// averages the tiles meeting at a lattice corner.</summary>
public static class TileColors
{
    /// <summary>The colour of a void tile, which contributes nothing to a corner blend.</summary>
    public static readonly Vector4 Void = Vector4.Zero;

    /// <summary>The default jitter amplitude, plus or minus 4 percent of the material colour.</summary>
    public const float DefaultJitterAmplitude = 0.04f;

    /// <summary>Parses <c>#rrggbb</c> or <c>#rrggbbaa</c> into 0..1 RGBA, alpha 1 when the string omits it.</summary>
    public static Vector4 Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if ((hex.Length != 7 && hex.Length != 9) || hex[0] != '#')
            throw new TileWorldException($"'{hex}' is not a colour: expected #rrggbb or #rrggbbaa");

        int channels = (hex.Length - 1) / 2;
        Span<float> rgba = stackalloc float[4] { 0f, 0f, 0f, 1f };
        for (int i = 0; i < channels; i++)
            rgba[i] = ((Nibble(hex, 1 + i * 2) << 4) | Nibble(hex, 2 + i * 2)) / 255f;
        return new Vector4(rgba[0], rgba[1], rgba[2], rgba[3]);
    }

    /// <summary>Parses the material's authored colour.</summary>
    public static Vector4 Parse(GroundMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return Parse(material.Color);
    }

    /// <summary>A deterministic brightness multiplier in [1 - amplitude, 1 + amplitude] for one tile of one
    /// plane, hashed from the world tile coordinate so every rebuild and every machine agrees.</summary>
    public static float Jitter(int worldX, int worldZ, int plane, float amplitude = DefaultJitterAmplitude)
    {
        uint h;
        unchecked
        {
            h = (uint)worldX * 73856093u ^ (uint)worldZ * 19349663u ^ (uint)plane * 83492791u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
        }
        return 1f + amplitude * ((h & 0xFFFF) / 65535f * 2f - 1f);
    }

    /// <summary>The average of the colours, at full alpha. An empty span blends to <see cref="Void"/>.</summary>
    public static Vector4 Blend(ReadOnlySpan<Vector4> colors)
    {
        if (colors.Length == 0) return Void;
        Vector4 sum = Vector4.Zero;
        for (int i = 0; i < colors.Length; i++) sum += colors[i];
        Vector4 average = sum / colors.Length;
        return new Vector4(average.X, average.Y, average.Z, 1f);
    }

    static int Nibble(string hex, int index)
    {
        char c = hex[index];
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        throw new TileWorldException($"'{hex}' is not a colour: '{c}' is not a hex digit");
    }
}
