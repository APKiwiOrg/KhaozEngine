using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Runs one support operation optimized, then again under <see cref="SupportReferenceEvaluation"/>, and
/// asserts bit-identical results: status, height, height error, raw feature provenance, owner binding and XZ. The
/// operation must be a pure function of immutable input, so both runs see the same request. The optimized run goes
/// first, so a cold optimized run stays cold.</summary>
internal static class SupportFootprintEquivalence
{
    /// <summary>One foot support Find both ways. Returns the optimized sample.</summary>
    internal static SupportSample AssertMatchesReference(Func<SupportSample> find, string context = "")
    {
        SupportSample optimized = find();
        SupportSample reference;
        using (SupportReferenceEvaluation.Begin()) reference = find();
        AssertSame(reference, optimized, context);
        return optimized;
    }

    /// <summary>One ground substep both ways. Compares, bit for bit, every support read the substep made in order
    /// (each <see cref="SupportFindObservation"/>: axis, feet, radius, reaches, slope cosine, status, height and
    /// height error), the final support sample as the Find overload does, and every float of the result: feet,
    /// rise, achieved, remaining time, climb budget, step rise, move rise and carried lag. The remaining footing,
    /// flags and enums compare by record equality. Each run records its reads under its own enabled diagnostics
    /// state, and its counters and routes are then merged into the caller's recorder, so caller callbacks do not
    /// run inside the substep. Returns the optimized result.</summary>
    internal static GroundStepResult AssertMatchesReference(Func<GroundStepResult> substep, string context = "")
    {
        GroundStepResult optimized = Observed(substep, out List<SupportFindObservation> optimizedReads);
        GroundStepResult reference;
        List<SupportFindObservation> referenceReads;
        using (SupportReferenceEvaluation.Begin()) reference = Observed(substep, out referenceReads);
        Assert.True(optimizedReads.Count > 0, $"{context}: the substep made no support read.");
        Assert.True(referenceReads.Count == optimizedReads.Count,
            $"{context}: {optimizedReads.Count} support reads differ from the reference's {referenceReads.Count}.");
        for (int i = 0; i < referenceReads.Count; i++)
            Assert.True(ReadBits(referenceReads[i]) == ReadBits(optimizedReads[i]),
                $"{context}: support read {i} {optimizedReads[i]} differs from reference {referenceReads[i]}.");
        AssertSame(reference.Support, optimized.Support, context);
        AssertBits(reference.Feet, optimized.Feet, context, "feet");
        AssertBits(new Vector3(reference.Achieved, reference.Rise), new Vector3(optimized.Achieved, optimized.Rise),
            context, "achieved and rise");
        AssertBits(new Vector3(reference.RemainingTime, reference.StepRise, reference.MoveRise),
            new Vector3(optimized.RemainingTime, optimized.StepRise, optimized.MoveRise), context,
            "remaining time, step rise and move rise");
        Assert.True(Bits(reference.CarriedLag) == Bits(optimized.CarriedLag) &&
            BitConverter.DoubleToInt64Bits(reference.ClimbBudget) == BitConverter.DoubleToInt64Bits(optimized.ClimbBudget),
            $"{context}: carried lag or climb budget of {optimized} differs from reference {reference}.");
        Assert.True(reference.Equals(optimized), $"{context}: substep {optimized} differs from reference {reference}.");
        return optimized;
    }

    static GroundStepResult Observed(Func<GroundStepResult> substep, out List<SupportFindObservation> reads)
    {
        var recorded = new List<SupportFindObservation>();
        reads = recorded;
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true) with { Find = recorded.Add });
        GroundStepResult result;
        SupportQueryCounts counts;
        SupportRouteCounts routes;
        try
        {
            result = substep();
            counts = SupportQueryDiagnostics.Snapshot();
            routes = SupportQueryDiagnostics.Routes;
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
        SupportQueryDiagnostics.Merge(counts, routes);
        return result;
    }

    static (int, int, int, int, int, int, int, byte, int, int) ReadBits(in SupportFindObservation read) =>
        (Bits(read.Axis.X), Bits(read.Axis.Y), Bits(read.FeetY), Bits(read.Radius), Bits(read.ReachUp),
            Bits(read.ReachDown), Bits(read.CosMaxSlope), read.Status, Bits(read.Height), Bits(read.HeightError));

    /// <summary>One raw complete footprint query both ways, comparing every member and join word. Returns the
    /// optimized result.</summary>
    internal static SupportFootprintResult AssertMatchesReference(IPhysicsSupportFootprint footprint,
        IPhysicsQueryLease lease, SupportFootprintRequest request, string context = "")
    {
        var members = new SupportFootprintMember[SupportFootprintResult.MaximumElements];
        var joins = new ulong[SupportFootprintResult.MaximumElements * SupportFootprintResult.JoinWordsFor(
            SupportFootprintResult.MaximumElements)];
        SupportFootprintResult optimized = footprint.QuerySupportFootprint(lease, request, members, joins);
        string optimizedText = Describe(optimized, members, joins);
        SupportFootprintMember[] optimizedMembers = members.Take(optimized.Members).ToArray();
        SupportFootprintResult reference;
        using (SupportReferenceEvaluation.Begin())
            reference = footprint.QuerySupportFootprint(lease, request, members, joins);
        Assert.Equal(Describe(reference, members, joins), optimizedText);
        Assert.Equal(members.Take(reference.Members), optimizedMembers);
        return optimized;
    }

    static void AssertSame(in SupportSample reference, in SupportSample actual, string context)
    {
        string message = $"{context}: sample {actual} differs from reference {reference}.";
        Assert.True(reference.Status == actual.Status && reference.Static == actual.Static &&
            reference.FeatureId == actual.FeatureId && Bits(reference.Height) == Bits(actual.Height) &&
            Bits(reference.HeightError) == Bits(actual.HeightError), message);
        AssertBits(reference.Normal, actual.Normal, context, "normal");
        AssertBits(reference.Witness, actual.Witness, context, "witness");
    }

    static void AssertBits(Vector3 reference, Vector3 actual, string context, string field) =>
        Assert.True(Bits(reference.X) == Bits(actual.X) && Bits(reference.Y) == Bits(actual.Y) &&
            Bits(reference.Z) == Bits(actual.Z), $"{context}: {field} {actual} differs from reference {reference}.");

    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    // Round-trip text exposes signed zeros that record equality treats as equal.
    static string Describe(in SupportFootprintResult result, SupportFootprintMember[] members, ulong[] joins) =>
        $"{result.Status} {result.Members} {result.Witnesses} {result.RequiredElements} {result.JoinWordsPerRow} " +
        $"{result.AxisVerdict} {result.FootVerdict} " + string.Join(";", members.Take(result.Members)) + " " +
        string.Join(",", joins.Take(result.Members * result.JoinWordsPerRow));
}
