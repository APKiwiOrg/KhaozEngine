using System;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The full-size textures the visibility equivalence oracle reads, laid out as 8 by 8 tiles. Each tile has one kind,
/// and its site texel (3, 3) holds the tile's base depth exactly, so a case centred on a site meets the relationships
/// the kind designs. The view depth is the stored value itself (the probe's depth parameters are orthographic 0 to 1).
/// <list type="bullet">
/// <item>Plain: one depth with a few rejected texels, uniform or near-uniform visibility.</item>
/// <item>Boundary: half the texels at the base, the rest exactly at, or a few ulps either side of, the base plus or
/// minus its tolerance, current and previous alike, random visibility.</item>
/// <item>Edge: two-level visibility edges that move one or two texels between the previous and current frames.</item>
/// <item>Threshold: c accepted current texels whose levels make the current mean minus a uniform previous level the
/// R8 lattice value nearest 0.1 or the R4 margin <c>0.1 - 2^-13</c>, on either side and either sign.</item>
/// <item>Hostile: NaN, both infinities, zero, negative and tiny depths, and the background motion sentinel.</item>
/// </list>
/// </summary>
internal sealed class TemporalVisibilityEquivalenceInputs
{
    internal const int Tile = 8, Site = 3;
    internal const float Tolerance = 0.02f;

    internal enum Kind { Plain, Boundary, Edge, Threshold, Hostile }

    internal readonly int Width, Height;
    internal readonly float[] Depth, Motion, PreviousDepth;
    internal readonly byte[] Current, Previous;
    internal readonly int[] KindCounts = new int[5];

    ulong _state;

    internal TemporalVisibilityEquivalenceInputs(int width, int height, ulong seed)
    {
        Width = width;
        Height = height;
        Depth = new float[width * height];
        Motion = new float[width * height * 2];
        PreviousDepth = new float[width * height];
        Current = new byte[width * height];
        Previous = new byte[width * height];
        _state = seed * 0x9E3779B97F4A7C15UL + 1;
        for (int ty = 0; ty * Tile < height; ty++)
            for (int tx = 0; tx * Tile < width; tx++)
                FillTile(tx * Tile, ty * Tile);
    }

    void FillTile(int x0, int y0)
    {
        int roll = Next(100);
        Kind kind = roll < 30 ? Kind.Plain : roll < 50 ? Kind.Boundary : roll < 70 ? Kind.Edge
            : roll < 90 ? Kind.Threshold : Kind.Hostile;
        KindCounts[(int)kind]++;
        float current = ExactBase(LogUniform(0.3f, 200f));
        float previous = ExactBase(LogUniform(0.3f, 200f));
        float currentTolerance = Tolerance * current, previousTolerance = Tolerance * previous;
        int level = Next(256), other = Next(256);
        bool vertical = Next(2) == 0;
        int edge = 1 + Next(6), shift = Next(2) == 0 ? -1 - Next(2) : 1 + Next(2);
        int lo = Next(2) == 0 ? 0 : Next(120), hi = Next(2) == 0 ? 255 : 135 + Next(121);
        (int[] levels, bool[] accepted, int previousLevel) = kind == Kind.Threshold ? ThresholdSite() : (null!, null!, 0);
        for (int y = y0; y < Math.Min(y0 + Tile, Height); y++)
            for (int x = x0; x < Math.Min(x0 + Tile, Width); x++)
            {
                int i = y * Width + x, lx = x - x0, ly = y - y0;
                bool site = lx == Site && ly == Site;
                float d = current, p = previous;
                byte c = (byte)level, v = (byte)level;
                switch (kind)
                {
                    case Kind.Plain:
                        if (!site && Next(10) == 0) d = current * 1.5f;
                        else if (!site && Next(10) == 0) d = current * (1f + 0.005f * Unit());
                        if (!site && Next(10) == 0) p = previous * 1.5f;
                        c = Next(10) < 3 ? (byte)255 : (byte)level;
                        int r = Next(10);
                        v = r < 4 ? c : r < 7 ? (byte)Math.Clamp(c + Next(81) - 40, 0, 255) : (byte)other;
                        break;
                    case Kind.Boundary:
                        if (!site && Next(2) == 0) d = Near(current, currentTolerance);
                        if (!site && Next(2) == 0) p = Near(previous, previousTolerance);
                        c = (byte)Next(256);
                        v = (byte)Next(256);
                        break;
                    case Kind.Edge:
                        if (!site && Next(16) == 0) d = current * 1.5f;
                        int along = vertical ? lx : ly;
                        c = (byte)(along >= edge ? hi : lo);
                        v = (byte)(along >= edge + shift ? hi : lo);
                        break;
                    case Kind.Threshold:
                        v = (byte)previousLevel;
                        c = (byte)previousLevel;
                        int k = (ly - Site + 1) * 3 + (lx - Site + 1);
                        if (Math.Abs(lx - Site) <= 1 && Math.Abs(ly - Site) <= 1)
                        {
                            d = accepted[k] ? current : current * 1.5f;
                            c = accepted[k] ? (byte)levels[k] : (byte)Next(256);
                        }
                        break;
                    case Kind.Hostile:
                        if (!site) d = Hostile(current);
                        if (!site) p = Hostile(previous);
                        if (!site && Next(6) == 0) Motion[i * 2] = 65000f;
                        c = (byte)Next(256);
                        v = (byte)Next(256);
                        break;
                }
                Depth[i] = d;
                PreviousDepth[i] = p;
                Current[i] = c;
                Previous[i] = v;
            }
    }

    // c accepted texels of the site's 3x3, the site always among them, whose levels sum to c * L + delta. delta is
    // the integer nearest 25.5 c (0.1 exactly for even c) or a step either side, or straddles the R4 margin.
    (int[] Levels, bool[] Accepted, int PreviousLevel) ThresholdSite()
    {
        int count, delta;
        int pick = Next(10);
        if (pick < 5)
        {
            count = 2 * (1 + Next(4));
            delta = count * 51 / 2;
        }
        else if (pick < 7)
        {
            count = 2 * (1 + Next(4));
            delta = count * 51 / 2 + (Next(2) == 0 ? -1 : 1);
        }
        else
        {
            count = 1 + 2 * Next(5);
            int below = (int)Math.Floor((0.1 - 1.0 / 8192.0) * 255.0 * count);
            delta = below + Next(2);
        }
        if (Next(2) == 0) delta = -delta;
        int previousLevel = 40 + Next(161);
        var accepted = new bool[9];
        accepted[4] = true;
        int[] order = [0, 1, 2, 3, 5, 6, 7, 8];
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        for (int i = 0; i < count - 1; i++) accepted[order[i]] = true;
        int total = count * previousLevel + delta, low = (int)Math.Floor(total / (double)count);
        int extra = total - low * count;
        var levels = new int[9];
        int given = 0;
        for (int k = 0; k < 9; k++)
            if (accepted[k]) levels[k] = low + (given++ < extra ? 1 : 0);
        return (levels, accepted, previousLevel);
    }

    // A depth exactly at the base plus or minus its tolerance, or within two ulps of it.
    float Near(float baseDepth, float tolerance)
    {
        float edge = Next(2) == 0 ? baseDepth + tolerance : baseDepth - tolerance;
        return Ulps(edge, Next(5) - 2);
    }

    float Hostile(float baseDepth) => Next(12) switch
    {
        0 => float.NaN,
        1 => float.PositiveInfinity,
        2 => float.NegativeInfinity,
        3 => 0f,
        4 => -baseDepth,
        5 => 1e-6f,
        6 => 1e30f,
        _ => baseDepth,
    };

    // A depth whose base plus and minus its tolerance are both exact floats, so a texel can sit exactly on the
    // acceptance boundary of a reference at the base.
    static float ExactBase(float start)
    {
        float candidate = start;
        for (int attempt = 0; attempt < 4096; attempt++, candidate = MathF.BitIncrement(candidate))
        {
            float tolerance = Tolerance * candidate;
            double up = (double)candidate + tolerance, down = (double)candidate - tolerance;
            if ((float)up == up && (float)down == down) return candidate;
        }
        throw new InvalidOperationException($"no exact boundary base near {start}");
    }

    internal static float Ulps(float value, int steps) => BitConverter.Int32BitsToSingle(
        BitConverter.SingleToInt32Bits(value) + (value < 0 ? -steps : steps));

    float LogUniform(float low, float high) => low * MathF.Pow(high / low, Unit());

    float Unit() => (Next64() >> 40) * (1f / (1 << 24));

    int Next(int n) => (int)(Next64() % (ulong)n);

    ulong Next64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
