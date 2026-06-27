using System;
using System.IO;

namespace KhaozEngine.Collision;

/// <summary>
/// A unit-scale (no placement transform) top-down max-height grid baked from a prop mesh: for each cell the
/// highest surface Y above it, or <see cref="float.NaN"/> where the prop does not cover that cell. Single-valued
/// (no overhangs). Render-free; the headless server reads the same binary the client does. Scale + yaw are applied
/// at query time by <see cref="WorldSurface"/>.
/// </summary>
public sealed class PropSurface
{
    const uint Magic = 0x4B455053; // "KEPS"
    const ushort FormatVersion = 1;

    readonly float[] heights;

    /// <summary>Grid columns.</summary>
    public int Width { get; }
    /// <summary>Grid rows.</summary>
    public int Height { get; }
    /// <summary>Local-space cell edge (metres).</summary>
    public float CellSize { get; }
    /// <summary>Local X of the grid's min corner (cell (0,0)).</summary>
    public float OriginX { get; }
    /// <summary>Local Z of the grid's min corner.</summary>
    public float OriginZ { get; }
    /// <summary>The maximum covered (non-NaN) height; 0 when fully empty.</summary>
    public float MaxHeight { get; }

    public PropSurface(int width, int height, float cellSize, float originX, float originZ, float[] heights)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("PropSurface dimensions must be positive.");
        if (heights is null || heights.Length != width * height)
            throw new ArgumentException("PropSurface heights length must equal width*height.");
        Width = width; Height = height; CellSize = MathF.Max(1e-4f, cellSize);
        OriginX = originX; OriginZ = originZ; this.heights = heights;

        float max = 0f; bool any = false;
        foreach (float v in heights)
            if (!float.IsNaN(v) && (!any || v > max)) { max = v; any = true; }
        MaxHeight = any ? max : 0f;
    }

    /// <summary>Bilinear sample of the covered cells at local (lx, lz); null when outside the grid or when the
    /// four surrounding cells are all empty.</summary>
    public float? SampleLocal(float lx, float lz)
    {
        float fx = (lx - OriginX) / CellSize;
        float fz = (lz - OriginZ) / CellSize;
        if (fx < 0f || fz < 0f || fx > Width - 1 || fz > Height - 1) return null;

        int i0 = (int)MathF.Floor(fx), j0 = (int)MathF.Floor(fz);
        int i1 = Math.Min(i0 + 1, Width - 1), j1 = Math.Min(j0 + 1, Height - 1);
        float tx = fx - i0, tz = fz - j0;

        // Average of the covered corners weighted bilinearly; ignore empty (NaN) corners.
        float sum = 0f, wsum = 0f;
        Accumulate(i0, j0, (1 - tx) * (1 - tz), ref sum, ref wsum);
        Accumulate(i1, j0, tx * (1 - tz), ref sum, ref wsum);
        Accumulate(i0, j1, (1 - tx) * tz, ref sum, ref wsum);
        Accumulate(i1, j1, tx * tz, ref sum, ref wsum);
        return wsum > 1e-6f ? sum / wsum : (float?)null;
    }

    void Accumulate(int i, int j, float w, ref float sum, ref float wsum)
    {
        float v = heights[j * Width + i];
        if (!float.IsNaN(v) && w > 0f) { sum += v * w; wsum += w; }
    }

    /// <summary>Versioned binary write (magic, version, dims, extent, then width*height little-endian floats).</summary>
    public void Write(Stream stream)
    {
        var w = new BinaryWriter(stream);
        w.Write(Magic); w.Write(FormatVersion);
        w.Write(Width); w.Write(Height); w.Write(CellSize); w.Write(OriginX); w.Write(OriginZ);
        foreach (float v in heights) w.Write(v);
        w.Flush();
    }

    /// <summary>Reads a surface written by <see cref="Write"/>. Throws <see cref="InvalidDataException"/> on a bad
    /// magic/version.</summary>
    public static PropSurface Read(Stream stream)
    {
        var r = new BinaryReader(stream);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("PropSurface: bad magic.");
        if (r.ReadUInt16() != FormatVersion) throw new InvalidDataException("PropSurface: unsupported version.");
        int width = r.ReadInt32(), height = r.ReadInt32();
        float cell = r.ReadSingle(), ox = r.ReadSingle(), oz = r.ReadSingle();
        var h = new float[width * height];
        for (int k = 0; k < h.Length; k++) h[k] = r.ReadSingle();
        return new PropSurface(width, height, cell, ox, oz, h);
    }
}
