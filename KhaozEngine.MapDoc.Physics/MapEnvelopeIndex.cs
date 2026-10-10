using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapDoc.Physics;

/// <summary>A fixed grid of 16 m cells over interaction envelope bounds. Each cell lists the envelopes whose bounds
/// reach it, in envelope order. A ray walks the cells it crosses in order of entry through a 3D digital differential
/// analyser, so the candidates a pick inspects depend only on the ray and the world.</summary>
internal sealed class MapEnvelopeIndex
{
    /// <summary>The cell edge in metres.</summary>
    internal const double CellMetres = 16d;

    // Bounds grow by this much before they are binned, so a ray rounded onto a cell face still finds an envelope that
    // touches it.
    const double Padding = 1d / 1024d;

    readonly Dictionary<(int X, int Y, int Z), int[]> _cells;
    readonly double _minX, _minY, _minZ, _maxX, _maxY, _maxZ;

    internal MapEnvelopeIndex(IReadOnlyList<MapBox3> bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var cells = new Dictionary<(int X, int Y, int Z), List<int>>();
        _minX = _minY = _minZ = double.PositiveInfinity;
        _maxX = _maxY = _maxZ = double.NegativeInfinity;
        for (int i = 0; i < bounds.Count; i++)
        {
            MapBox3 b = bounds[i];
            _minX = Math.Min(_minX, b.MinX - Padding);
            _minY = Math.Min(_minY, b.MinY - Padding);
            _minZ = Math.Min(_minZ, b.MinZ - Padding);
            _maxX = Math.Max(_maxX, b.MaxX + Padding);
            _maxY = Math.Max(_maxY, b.MaxY + Padding);
            _maxZ = Math.Max(_maxZ, b.MaxZ + Padding);
            for (int x = Cell(b.MinX - Padding); x <= Cell(b.MaxX + Padding); x++)
                for (int y = Cell(b.MinY - Padding); y <= Cell(b.MaxY + Padding); y++)
                    for (int z = Cell(b.MinZ - Padding); z <= Cell(b.MaxZ + Padding); z++)
                    {
                        if (!cells.TryGetValue((x, y, z), out List<int>? list)) cells.Add((x, y, z), list = new List<int>());
                        list.Add(i);
                    }
        }
        _cells = new Dictionary<(int X, int Y, int Z), int[]>(cells.Count);
        foreach (KeyValuePair<(int X, int Y, int Z), List<int>> cell in cells) _cells.Add(cell.Key, cell.Value.ToArray());
    }

    /// <summary>The occupied cells that origin + t direction crosses for t in [<paramref name="tStart"/>,
    /// <paramref name="tEnd"/>], in order of entry, each with the parameter at which the ray enters it. The caller
    /// stops once an entry passes its nearest hit.</summary>
    internal IEnumerable<(double Entry, int[] Envelopes)> Cells(MapDouble3 origin, MapDouble3 direction, double tStart,
        double tEnd)
    {
        if (_cells.Count == 0) yield break;
        if (!Clip(origin.X, direction.X, _minX, _maxX, ref tStart, ref tEnd) ||
            !Clip(origin.Y, direction.Y, _minY, _maxY, ref tStart, ref tEnd) ||
            !Clip(origin.Z, direction.Z, _minZ, _maxZ, ref tStart, ref tEnd))
            yield break;

        int x = Cell(origin.X + tStart * direction.X);
        int y = Cell(origin.Y + tStart * direction.Y);
        int z = Cell(origin.Z + tStart * direction.Z);
        int stepX = Math.Sign(direction.X), stepY = Math.Sign(direction.Y), stepZ = Math.Sign(direction.Z);
        double entry = tStart;

        // Each step crosses one cell face, so the walk ends after at most the cells across the clipped bounds.
        long limit = 3L + Span(_minX, _maxX) + Span(_minY, _maxY) + Span(_minZ, _maxZ);
        for (long step = 0; step < limit; step++)
        {
            if (_cells.TryGetValue((x, y, z), out int[]? envelopes)) yield return (entry, envelopes);

            // Face parameters come from the origin each time rather than accumulating, so no drift builds up.
            double nextX = Face(origin.X, direction.X, x, stepX);
            double nextY = Face(origin.Y, direction.Y, y, stepY);
            double nextZ = Face(origin.Z, direction.Z, z, stepZ);
            if (nextX <= nextY && nextX <= nextZ)
            {
                entry = nextX;
                x += stepX;
            }
            else if (nextY <= nextZ)
            {
                entry = nextY;
                y += stepY;
            }
            else
            {
                entry = nextZ;
                z += stepZ;
            }
            if (!(entry <= tEnd)) yield break;
        }
    }

    static int Cell(double value) => (int)Math.Floor(value / CellMetres);

    static long Span(double min, double max) => (long)Cell(max) - Cell(min) + 1;

    // The parameter at which the ray leaves the cell along one axis, or infinity when it never does.
    static double Face(double origin, double direction, int cell, int step)
    {
        if (step == 0) return double.PositiveInfinity;
        double face = (step > 0 ? cell + 1 : cell) * CellMetres;
        return (face - origin) / direction;
    }

    static bool Clip(double origin, double direction, double min, double max, ref double tStart, ref double tEnd)
    {
        if (direction == 0d) return origin >= min && origin <= max;
        double a = (min - origin) / direction, b = (max - origin) / direction;
        tStart = Math.Max(tStart, Math.Min(a, b));
        tEnd = Math.Min(tEnd, Math.Max(a, b));
        return tStart <= tEnd;
    }
}
