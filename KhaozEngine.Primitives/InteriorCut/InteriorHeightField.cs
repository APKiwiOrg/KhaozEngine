using System;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Primitives;

/// <summary>An immutable packed RGBA8 height field with floor-inclusive, top-exclusive spans.</summary>
public sealed partial class InteriorHeightField
{
    static long _nextVersion;
    readonly byte[] _texels;

    internal InteriorHeightField(InteriorWindow window, InteriorEncoding encoding, byte[] texels, int mergedCells, int outOfRangeCells)
    {
        Window = window;
        BaseY = encoding.BaseY;
        StepY = encoding.StepY;
        _texels = texels;
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
    public bool TryGetSpan(int cellX, int cellZ, out float floorY, out float topY)
    {
        floorY = topY = 0f;
        if ((uint)cellX >= (uint)Window.Cells || (uint)cellZ >= (uint)Window.Cells)
            return false;
        int offset = (cellZ * Window.Cells + cellX) * 4;
        int floor = (_texels[offset] << 8) | _texels[offset + 1];
        int top = (_texels[offset + 2] << 8) | _texels[offset + 3];
        if (top <= floor)
            return false;
        floorY = BaseY + StepY * floor;
        topY = BaseY + StepY * top;
        return topY > floorY;
    }

    /// <summary>Decode the same texel and half-open height comparison as the shader.</summary>
    public bool Contains(Vector3 world) =>
        TryCell(world, out int x, out int z) && TryGetSpan(x, z, out float floor, out float top) && world.Y >= floor && world.Y < top;

    bool TryCell(Vector3 world, out int x, out int z)
    {
        x = z = 0;
        if (!Finite(world)) return false;
        float cx = (world.X - Window.Min.X) / Window.CellSize;
        float cz = (world.Z - Window.Min.Y) / Window.CellSize;
        if (!(cx >= 0f && cz >= 0f && cx < Window.Cells && cz < Window.Cells)) return false;
        x = (int)MathF.Floor(cx);
        z = (int)MathF.Floor(cz);
        return true;
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
