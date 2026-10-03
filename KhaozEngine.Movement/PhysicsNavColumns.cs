using System;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal readonly record struct PhysicsNavSurface(float Height, float Headroom, uint Areas);

/// <summary>Owned immutable absolute surface data in canonical Z/X column order.</summary>
internal sealed class PhysicsNavColumns : INavColumnProvider
{
    private readonly PhysicsNavBakeOptions _options;
    private readonly int[] _starts;
    private readonly PhysicsNavSurface[] _surfaces;

    internal PhysicsNavColumns(PhysicsNavBakeOptions options, int width, int height,
        ReadOnlySpan<int> starts, ReadOnlySpan<PhysicsNavSurface> surfaces)
        : this(options, width, height, starts.ToArray(), surfaces.ToArray())
    {
    }

    private PhysicsNavColumns(PhysicsNavBakeOptions options, int width, int height,
        int[] starts, PhysicsNavSurface[] surfaces)
    {
        _options = options;
        Width = width;
        Height = height;
        _starts = starts;
        _surfaces = surfaces;
    }

    /// <summary>Stores both arrays without copying. The caller hands over ownership and must never write to or expose
    /// them afterwards.</summary>
    internal static PhysicsNavColumns Own(PhysicsNavBakeOptions options, int width, int height,
        int[] starts, PhysicsNavSurface[] surfaces) => new(options, width, height, starts, surfaces);

    internal int Width { get; }
    internal int Height { get; }
    internal int SurfaceCount => _surfaces.Length;

    internal ReadOnlySpan<PhysicsNavSurface> GetColumn(int x, int z)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)z >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(z));
        int cell = z * Width + x;
        return _surfaces.AsSpan(_starts[cell], _starts[cell + 1] - _starts[cell]);
    }

    public int SampleColumn(float x, float z, Span<NavSurfaceSample> surfaces)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) ||
            x < _options.MinX || x >= _options.MaxX || z < _options.MinZ || z >= _options.MaxZ)
            return 0;
        int cx = (int)MathF.Floor((x - _options.MinX) / _options.CellSize);
        int cz = (int)MathF.Floor((z - _options.MinZ) / _options.CellSize);
        if ((uint)cx >= (uint)Width || (uint)cz >= (uint)Height) return 0;
        ReadOnlySpan<PhysicsNavSurface> column = GetColumn(cx, cz);
        int count = Math.Min(column.Length, surfaces.Length);
        for (int i = 0; i < count; i++)
            surfaces[i] = new NavSurfaceSample(true, column[i].Height, column[i].Headroom);
        return count;
    }
}
