using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.TileWorld.Physics;

// Rectangular boundary patches captured once. Opposite faces subtract their covered
// planar and vertical rectangle, including partial neighbors with a different bed.
internal readonly record struct TileWaterBoundary(int Column, Vector3 Minimum, Vector3 Maximum, Vector3 Normal, string Id)
{
    readonly record struct Edge(int Axis, float Plane);
    readonly record struct Patch(float Low, float High, float Bottom, float Top);

    internal static TileWaterBoundary[] Build(ReadOnlySpan<TileWaterColumn> columns, bool sameDomainOnly)
    {
        var edges = new Dictionary<Edge, List<(int Column, int Face)>>();
        for (int i = 0; i < columns.Length; i++)
            for (int face = 0; face < 4; face++)
            {
                Edge key = Key(columns[i], face);
                if (!edges.TryGetValue(key, out var owners)) edges.Add(key, owners = []);
                owners.Add((i, face));
            }
        var result = new List<TileWaterBoundary>();
        for (int i = 0; i < columns.Length; i++)
        {
            TileWaterColumn column = columns[i];
            for (int face = 0; face < 4; face++)
            {
                Patch full = Rectangle(column, face);
                var patches = new List<Patch> { full };
                foreach (var neighbor in edges[Key(column, face)])
                {
                    if (neighbor.Face != (face ^ 1)) continue;
                    TileWaterColumn other = columns[neighbor.Column];
                    if (sameDomainOnly && other.Domain != column.Domain) continue;
                    Subtract(patches, Rectangle(other, neighbor.Face));
                }
                foreach (Patch patch in patches)
                {
                    Vector3 min = column.Minimum, max = column.Maximum;
                    min.Y = patch.Bottom;
                    max.Y = patch.Top;
                    Vector3 normal;
                    string id;
                    if (face < 2) { min.Z = patch.Low; max.Z = patch.High; }
                    else { min.X = patch.Low; max.X = patch.High; }
                    switch (face)
                    {
                        case 0: max.X = min.X; normal = -Vector3.UnitX; id = column.MinXBoundaryId; break;
                        case 1: min.X = max.X; normal = Vector3.UnitX; id = column.MaxXBoundaryId; break;
                        case 2: max.Z = min.Z; normal = -Vector3.UnitZ; id = column.MinZBoundaryId; break;
                        default: min.Z = max.Z; normal = Vector3.UnitZ; id = column.MaxZBoundaryId; break;
                    }
                    result.Add(new(i, min, max, normal, id));
                }
            }
            result.Add(new(i, column.Minimum, new(column.Maximum.X, column.Minimum.Y, column.Maximum.Z), -Vector3.UnitY, column.BedBoundaryId));
            result.Add(new(i, new(column.Minimum.X, column.Maximum.Y, column.Minimum.Z), column.Maximum, Vector3.UnitY, column.SurfaceBoundaryId));
        }
        return result.ToArray();
    }

    static Edge Key(TileWaterColumn column, int face) => face switch
    {
        0 => new(0, column.Minimum.X),
        1 => new(0, column.Maximum.X),
        2 => new(2, column.Minimum.Z),
        _ => new(2, column.Maximum.Z)
    };

    static Patch Rectangle(TileWaterColumn c, int face) => face < 2
        ? new(c.Minimum.Z, c.Maximum.Z, c.Minimum.Y, c.Maximum.Y)
        : new(c.Minimum.X, c.Maximum.X, c.Minimum.Y, c.Maximum.Y);

    static void Subtract(List<Patch> patches, Patch cover)
    {
        for (int i = patches.Count - 1; i >= 0; i--)
        {
            Patch p = patches[i];
            float low = Math.Max(p.Low, cover.Low), high = Math.Min(p.High, cover.High);
            float bottom = Math.Max(p.Bottom, cover.Bottom), top = Math.Min(p.Top, cover.Top);
            if (low >= high || bottom >= top) continue;
            patches.RemoveAt(i);
            if (bottom > p.Bottom) patches.Add(new(p.Low, p.High, p.Bottom, bottom));
            if (top < p.Top) patches.Add(new(p.Low, p.High, top, p.Top));
            if (low > p.Low) patches.Add(new(p.Low, low, bottom, top));
            if (high < p.High) patches.Add(new(high, p.High, bottom, top));
        }
    }
}
