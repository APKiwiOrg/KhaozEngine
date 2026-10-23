using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>An independent open detail segment in normalized local footprint coordinates.</summary>
/// <param name="Start">The first endpoint in [-1, 1] on both axes.</param>
/// <param name="End">The second endpoint in [-1, 1] on both axes.</param>
public readonly record struct WorldMapSymbolStroke(Vector2 Start, Vector2 End);

/// <summary>An immutable normalized polygon silhouette and independent detail segments.</summary>
public sealed class WorldMapSymbolGeometry
{
    /// <summary>Snapshots a simple closed outline and optional open segments in [-1, 1] on both axes.</summary>
    /// <param name="outline">At least three vertices, without a repeated closing endpoint. Either winding is accepted.</param>
    /// <param name="strokes">Independent detail segments, or null for none.</param>
    /// <exception cref="ArgumentNullException">The outline is null.</exception>
    /// <exception cref="ArgumentException">Coordinates are invalid or the outline is degenerate or not simple.</exception>
    public WorldMapSymbolGeometry(IEnumerable<Vector2> outline, IEnumerable<WorldMapSymbolStroke>? strokes = null)
    {
        ArgumentNullException.ThrowIfNull(outline);
        Vector2[] vertices = new List<Vector2>(outline).ToArray();
        WorldMapSymbolStroke[] segments = strokes is null ? [] : new List<WorldMapSymbolStroke>(strokes).ToArray();
        ValidateOutline(vertices);
        foreach (WorldMapSymbolStroke segment in segments)
        {
            ValidatePoint(segment.Start, nameof(strokes));
            ValidatePoint(segment.End, nameof(strokes));
        }
        Outline = Array.AsReadOnly(vertices);
        Strokes = Array.AsReadOnly(segments);
    }

    /// <summary>The closed silhouette's vertices, with the last connected to the first.</summary>
    public IReadOnlyList<Vector2> Outline { get; }

    /// <summary>Independent open detail segments. These do not contribute to building shadows.</summary>
    public IReadOnlyList<WorldMapSymbolStroke> Strokes { get; }

    static void ValidateOutline(Vector2[] vertices)
    {
        if (vertices.Length < 3) throw InvalidOutline();
        double area = 0d;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector2 a = vertices[i], b = vertices[(i + 1) % vertices.Length];
            ValidatePoint(a, "outline");
            if (a == b) throw InvalidOutline();
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }
        if (area == 0d) throw InvalidOutline();
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector2 a = vertices[i], b = vertices[(i + 1) % vertices.Length];
            Vector2 previous = vertices[(i + vertices.Length - 1) % vertices.Length];
            // Adjacent edges may continue straight, but cannot overlap by reversing direction.
            if (Cross(previous, a, b) == 0d && (OnSegment(previous, a, b) || OnSegment(a, b, previous)))
                throw InvalidOutline();
            for (int j = i + 1; j < vertices.Length; j++)
            {
                if (j == i + 1 || (i == 0 && j == vertices.Length - 1)) continue;
                if (Intersects(a, b, vertices[j], vertices[(j + 1) % vertices.Length])) throw InvalidOutline();
            }
        }
    }

    static void ValidatePoint(Vector2 point, string parameter)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) ||
            point.X < -1f || point.X > 1f || point.Y < -1f || point.Y > 1f)
            throw new ArgumentException("Geometry coordinates must be finite and in [-1, 1].", parameter);
    }

    static bool Intersects(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        double abC = Cross(a, b, c), abD = Cross(a, b, d);
        double cdA = Cross(c, d, a), cdB = Cross(c, d, b);
        if (abC == 0d && OnSegment(a, b, c) || abD == 0d && OnSegment(a, b, d) ||
            cdA == 0d && OnSegment(c, d, a) || cdB == 0d && OnSegment(c, d, b)) return true;
        return (abC > 0d) != (abD > 0d) && (cdA > 0d) != (cdB > 0d);
    }

    static double Cross(Vector2 a, Vector2 b, Vector2 c) =>
        ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);

    static bool OnSegment(Vector2 a, Vector2 b, Vector2 point) =>
        point.X >= MathF.Min(a.X, b.X) && point.X <= MathF.Max(a.X, b.X) &&
        point.Y >= MathF.Min(a.Y, b.Y) && point.Y <= MathF.Max(a.Y, b.Y);

    static ArgumentException InvalidOutline() => new("The outline must be a nondegenerate simple closed polygon.", "outline");
}
