using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Coverage of complete curved surfaces against a closed admission column.
/// The reusable live cell pool certifies absence by outward boxes and presence by attained points.</summary>
internal sealed class SupportCurvedSurfaceAdmission
{
    internal const int MaximumLiveCells = 1024;
    readonly record struct Cell(double A0, double A1, double B0, double B1, byte Kind);
    Cell[] _live = new Cell[MaximumLiveCells], _next = new Cell[MaximumLiveCells];
    internal int LiveHighWater { get; private set; }
    internal int EvaluatedCells { get; private set; }
    internal bool CapacityRefused { get; private set; }
    internal long TotalCells { get; private set; }
    internal int FrontierHighWater { get; private set; }

    internal void ResetStatistics()
    {
        TotalCells = FrontierHighWater = 0;
        CapacityRefused = false;
    }

    internal GeometrySign Measure(SupportTiltedCurvedGeometry geometry, SupportCurvedShape shape, int capKind,
        double x, double z, GeometryInterval radius, double bottom, double top, bool requireUpward = false)
    {
        bool uniformlyUp = !requireUpward || geometry.CapUniformlyUp(capKind);
        int live = 0;
        if (shape == SupportCurvedShape.Cylinder && capKind >= 0) _live[live++] = new(-1, 1, -1, 1, (byte)capKind);
        else
        {
            _live[live++] = new(-1, 1, -1, 1, 0);
            _live[live++] = new(-1, 1, -1, 1, 1);
            if (shape == SupportCurvedShape.Capsule)
                for (byte kind = 4; kind < 8; kind++) _live[live++] = new(-1, 1, -1, 1, kind);
        }
        LiveHighWater = live; EvaluatedCells = 0; CapacityRefused = false;
        FrontierHighWater = Math.Max(FrontierHighWater, live);
        GeometryInterval r2 = radius.Square();
        while (live > 0)
        {
            int next = 0;
            for (int i = 0; i < live; i++)
            {
                Cell cell = _live[i];
                EvaluatedCells++;
                TotalCells++;
                if (requireUpward && geometry.CapUpward(cell.Kind).Upper < 0) continue;
                GeometryInterval a = GeometryInterval.Enclose(cell.A0, cell.A1), b = GeometryInterval.Enclose(cell.B0, cell.B1);
                if (cell.Kind is 2 or 3 && a.Square().Add(b.Square()).Lower > 1) continue;
                GeometryVector bounds = geometry.Surface(cell.Kind, a, b);
                if (!bounds.IsResolved) return GeometrySign.Unresolved;
                GeometryInterval distance = bounds.X.Subtract(GeometryInterval.Exact(x)).Square()
                    .Add(bounds.Z.Subtract(GeometryInterval.Exact(z)).Square());
                if (bounds.Y.Lower > top || bounds.Y.Upper < bottom || distance.Lower > r2.Upper) continue;
                double am = cell.A0 + (cell.A1 - cell.A0) * 0.5, bm = cell.B0 + (cell.B1 - cell.B0) * 0.5;
                if (uniformlyUp && geometry.SurfacePointInside(cell.Kind, am, bm, x, z, radius.Lower, bottom, top)) return GeometrySign.Positive;
                bool splitA = cell.A1 - cell.A0 >= cell.B1 - cell.B0;
                if (splitA && !(am > cell.A0 && am < cell.A1) || !splitA && !(bm > cell.B0 && bm < cell.B1))
                    return GeometrySign.Unresolved;
                if (next + 2 > MaximumLiveCells)
                {
                    LiveHighWater = Math.Max(LiveHighWater, next);
                    FrontierHighWater = Math.Max(FrontierHighWater, next);
                    CapacityRefused = true;
                    return GeometrySign.Unresolved;
                }
                if (splitA)
                {
                    _next[next++] = cell with { A1 = am };
                    _next[next++] = cell with { A0 = am };
                }
                else
                {
                    _next[next++] = cell with { B1 = bm };
                    _next[next++] = cell with { B0 = bm };
                }
            }
            LiveHighWater = Math.Max(LiveHighWater, next);
            FrontierHighWater = Math.Max(FrontierHighWater, next);
            (_live, _next) = (_next, _live); live = next;
        }
        return GeometrySign.Negative;
    }
}
