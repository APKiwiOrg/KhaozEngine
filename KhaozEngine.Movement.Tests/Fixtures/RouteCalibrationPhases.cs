using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;
using Phase = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;

namespace KhaozEngine.Tests.Movement;

/// <summary>Positions production cursors at the operations sampled by the phase rows.</summary>
internal static class RouteCalibrationPhases
{
    internal const int ShortCrossingBudget = 768;
    internal const int LongCrossingBudget = 1536;

    internal static readonly ReconstructionSlice[] Reconstruction =
    [
        new(Phase.Parent, Phase.Reverse, 108567, 2049),
        new(Phase.Reverse, Phase.Decode, 110616, 1024),
        new(Phase.Decode, Phase.Allocate, 111640, 4096),
        new(Phase.Copy, Phase.Done, 115738, 2048),
    ];

    internal readonly record struct ReconstructionSlice(Phase Phase, Phase Next, int PrefixUnits, int Units)
    {
        internal PathWorkBudget Budget => new(Units);

        internal void Prepare(GridPathPlanner.GridSearchRequest request)
        {
            request.Advance(new(PrefixUnits));
            if (request.State != PathPlanState.Pending || request.NextPhaseForTests != Phase ||
                request.WorkSpent != PrefixUnits)
                throw new InvalidOperationException($"The reconstruction cursor did not reach the start of {Phase}.");
        }
    }

    internal static NavSegmentRequest[] ProjectedRows()
    {
        var cursor = new NavSegmentRequest(Flat(12, 132, 0f), Feet(2, 2), Feet(8, 130));
        Reach(ref cursor, NavSegmentOperation.SegmentStart);
        cursor.Step();

        // Each copy preserves the actual projected row's geometry and the sweep's position in the segment.
        var rows = new List<NavSegmentRequest>();
        while (cursor.NextOperation is NavSegmentOperation.FootprintRow or NavSegmentOperation.FootprintColumn)
        {
            if (cursor.NextOperation == NavSegmentOperation.FootprintRow) rows.Add(cursor);
            cursor.Step();
        }
        if (cursor.NextOperation != NavSegmentOperation.CellCrossing)
            throw new InvalidOperationException("The projected footprint did not finish its sweep.");
        return rows.ToArray();
    }

    internal static NavSegmentRequest Crossings()
    {
        var cursor = new NavSegmentRequest(Flat(515, 3, 0f), Feet(1, 1), Feet(513, 1));
        Reach(ref cursor, NavSegmentOperation.CellCrossing);
        return cursor;
    }

    static void Reach(ref NavSegmentRequest cursor, NavSegmentOperation operation)
    {
        while (cursor.NextOperation != operation && cursor.NextOperation != NavSegmentOperation.Done)
            cursor.Step();
        if (cursor.NextOperation != operation)
            throw new InvalidOperationException($"The segment did not reach {operation}.");
    }

    static Vector3 Feet(int x, int z) => new(x + 0.5f, 0f, z + 0.5f);
}
