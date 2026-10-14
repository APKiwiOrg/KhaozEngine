using System;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportTiltedCurvedGeometry
{
    internal long RootCellCount { get; private set; }
    internal int RootLiveHighWater { get; private set; }
    internal int RootTerminalHighWater { get; private set; }

    internal void ResetStatistics()
    {
        RootCellCount = 0; RootLiveHighWater = RootTerminalHighWater = 0;
        _roots.ResetStatistics();
    }

    bool SolveRoots(ReadOnlySpan<Number> polynomial, double low, double high)
    {
        bool complete = _roots.Solve(_arithmetic, polynomial, low, high);
        ObserveRoots();
        return complete;
    }

    bool SolveRoots(double low, double high, int maximumRoots = SupportCurvedRootRegions.MaximumTerminalCells)
    {
        bool complete = _roots.Solve(this, low, high, maximumRoots);
        ObserveRoots();
        return complete;
    }

    void ObserveRoots()
    {
        RootCellCount += _roots.EvaluatedCells;
        RootLiveHighWater = Math.Max(RootLiveHighWater, _roots.LiveHighWater);
        RootTerminalHighWater = Math.Max(RootTerminalHighWater, _roots.TerminalCells);
    }
}
