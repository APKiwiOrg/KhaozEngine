using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Actual selected device words and decoded outcomes, with separate independently built expectations.</summary>
internal sealed record CoverageBatchObservation(ulong Actor, ulong ExpectedFrame, ulong ExpectedView, ulong ExpectedPreparation,
    string Host, int SourceVertices, int RefinedVertices, int CommandCount, int MaterialSlices, int ActorSlot, uint CurrentBase, uint[] Header,
    uint[] ExpectedWords, uint[] ActualWords, CoverageFailure ExpectedFailure, CoverageGpuOutcome Outcome,
    int WordMismatches, int IdentityMismatches)
{
    internal static CoverageBatchObservation Capture(CoverageBatchActor actor, uint[] words, CoverageGpuOutcome outcome)
    {
        var reservation = actor.Reservation;
        int start = (int)reservation.CurrentBase;
        uint[] header = words.AsSpan(start, 16).ToArray();
        bool source = header[14] != 0;
        int offset = start + 16 + (source ? 0 : reservation.SourceCount * 16);
        int length = (source ? reservation.SourceCount : reservation.VertexCount) * 16;
        uint[] actual = words.AsSpan(offset, length).ToArray();
        int mismatches = Math.Abs(actual.Length - actor.ExpectedWords.Length);
        for (int i = 0; i < Math.Min(actual.Length, actor.ExpectedWords.Length); i++)
            if (actual[i] != actor.ExpectedWords[i]) mismatches++;
        int identities = 0;
        if (Pair(header, 0) != actor.Generation) identities++;
        if (Pair(header, 2) != actor.View) identities++;
        if (Pair(header, 4) != actor.Key.Value) identities++;
        if (Pair(header, 6) != actor.Preparation) identities++;
        if (outcome.FrameId != actor.Generation || outcome.ViewId != actor.View || outcome.Actor != actor.Key) identities++;
        if (outcome.Outcome.Failure != actor.ExpectedFailure) identities++;
        if (header[8] != (uint)actor.ExpectedFailure.Code || Pair(header, 9) != actor.ExpectedFailure.EquipmentInstanceId
            || header[11] != actor.ExpectedFailure.CommandId || header[12] != actor.ExpectedFailure.Count
            || header[13] != actor.ExpectedFailure.Limit || source != !actor.ExpectedFailure.IsSuccess) identities++;
        return new(actor.Key.Value, actor.Generation, actor.View, actor.Preparation, actor.Topology.HostIdentity,
            reservation.SourceCount, reservation.VertexCount, actor.Frame.Preparation!.CommandCount,
            actor.Frame.Preparation.Draws.Length, reservation.ActorSlot, reservation.CurrentBase, header,
            actor.ExpectedWords, actual, actor.ExpectedFailure, outcome, mismatches, identities);
    }

    internal void AssertExact()
    {
        Assert.Equal(0, WordMismatches); Assert.Equal(0, IdentityMismatches);
        Assert.Equal(ExpectedWords, ActualWords);
        Assert.Equal(ExpectedFailure.IsSuccess ? CoveragePresentation.Covered : CoveragePresentation.UncoveredFallback, Outcome.Outcome.Presentation);
    }

    static ulong Pair(uint[] words, int index) => words[index] | (ulong)words[index + 1] << 32;
}
