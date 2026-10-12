using System;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>An axis-aligned, half-open rectangle in map space, measured in metres.</summary>
/// <param name="X">The west edge.</param>
/// <param name="Y">The south edge.</param>
/// <param name="Width">The eastward extent. A non-positive extent is empty.</param>
/// <param name="Height">The northward extent. A non-positive extent is empty.</param>
public readonly record struct MapRect(float X, float Y, float Width, float Height)
{
    /// <summary>The excluded east edge.</summary>
    public float Right => X + Width;

    /// <summary>The excluded north edge.</summary>
    public float Top => Y + Height;

    /// <summary>The centre in map space.</summary>
    public Vector2 Center => new(X + Width * 0.5f, Y + Height * 0.5f);

    /// <summary>Whether a point lies inside the south/west-inclusive, north/east-exclusive bounds.</summary>
    public bool Contains(Vector2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Top;

    /// <summary>Whether two non-empty rectangles overlap. Touching edges do not intersect.</summary>
    public bool Intersects(MapRect other) =>
        Width > 0f && Height > 0f && other.Width > 0f && other.Height > 0f &&
        X < other.Right && Right > other.X && Y < other.Top && Top > other.Y;

    /// <summary>Moves each edge outward by the given distance. A negative distance contracts it.</summary>
    public MapRect Expand(float metres) => new(X - metres, Y - metres, Width + 2f * metres, Height + 2f * metres);

    /// <summary>The smallest rectangle enclosing both rectangles' edges.</summary>
    public MapRect Union(MapRect other)
    {
        float x = MathF.Min(X, other.X);
        float y = MathF.Min(Y, other.Y);
        return new MapRect(x, y, MathF.Max(Right, other.Right) - x, MathF.Max(Top, other.Top) - y);
    }
}
