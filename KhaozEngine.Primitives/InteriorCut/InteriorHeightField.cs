using System;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Primitives;

/// <summary>An immutable packed RGBA8 height field with floor-inclusive, top-exclusive code spans.</summary>
public sealed partial class InteriorHeightField
{
    static long _nextVersion;
    readonly byte[] _texels;
    readonly InteriorOverlapSummary _overlap;

    internal InteriorHeightField(InteriorWindow window, InteriorEncoding encoding, byte[] texels, int mergedCells, int outOfRangeCells)
    {
        Window = window;
        BaseY = encoding.BaseY;
        StepY = encoding.StepY;
        _texels = texels;
        _overlap = new InteriorOverlapSummary(window.Cells, texels);
        MergedCells = mergedCells;
        OutOfRangeCells = outOfRangeCells;
        Version = Interlocked.Increment(ref _nextVersion);
    }

    public InteriorWindow Window { get; }
    public float BaseY { get; }
    public float StepY { get; }
    public int MergedCells { get; }
    /// <summary>Distinct covered cells whose contributing heights exceeded the supplied encoding.</summary>
    public int OutOfRangeCells { get; }
    public ReadOnlyMemory<byte> Texels => _texels;
    public long Version { get; }
    /// <summary>Read approximate decoded world heights. Membership is tested in packed-code space.</summary>
    public bool TryGetSpan(int cellX, int cellZ, out float floorY, out float topY)
    {
        floorY = topY = 0f;
        if (!TryGetCodes(cellX, cellZ, out int floor, out int top)) return false;
        floorY = BaseY + StepY * floor;
        topY = BaseY + StepY * top;
        return topY > floorY;
    }

    bool TryGetCodes(int cellX, int cellZ, out int floor, out int top)
    {
        floor = top = 0;
        if ((uint)cellX >= (uint)Window.Cells || (uint)cellZ >= (uint)Window.Cells)
            return false;
        int offset = (cellZ * Window.Cells + cellX) * 4;
        floor = (_texels[offset] << 8) | _texels[offset + 1];
        top = (_texels[offset + 2] << 8) | _texels[offset + 3];
        return top > floor;
    }

    /// <summary>Test the same texel and float32 code-space expression as the shader.</summary>
    public bool Contains(Vector3 world) => Contains(world, default);

    /// <summary>Test membership in the same render frame as the visible scene.</summary>
    public bool Contains(Vector3 world, InteriorCutFrame frame)
    {
        var (window, heights) = frame.Uniforms(this);
        return ContainsPoint(frame.ToRender(world), window, heights, out _, out _, out _, out _);
    }

    bool ContainsPoint(Vector3 relative, Vector4 window, Vector4 heights, out int x, out int z, out int lo, out int hi)
    {
        lo = hi = 0;
        return TryCell(relative, window, out x, out z) && TryGetCodes(x, z, out lo, out hi) && ContainsHeight(relative.Y, heights, lo, hi);
    }

    static bool ContainsHeight(float y, Vector4 heights, int lo, int hi)
    {
        float q = (y - heights.X) * heights.Y;
        return hi > lo && q >= lo && q < hi;
    }

    static bool TryCell(Vector3 relative, Vector4 window, out int x, out int z)
    {
        x = z = 0;
        if (!Finite(relative)) return false;
        float cx = (relative.X - window.X) * window.Z;
        float cz = (relative.Z - window.Y) * window.Z;
        if (!(cx >= 0f && cz >= 0f && cx < window.W && cz < window.W)) return false;
        x = (int)MathF.Floor(cx);
        z = (int)MathF.Floor(cz);
        return true;
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
