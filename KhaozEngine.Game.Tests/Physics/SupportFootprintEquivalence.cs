using System;
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

    /// <summary>One ground substep both ways, including every support read it carries. Returns the optimized
    /// result.</summary>
    internal static GroundStepResult AssertMatchesReference(Func<GroundStepResult> substep, string context = "")
    {
        GroundStepResult optimized = substep();
        GroundStepResult reference;
        using (SupportReferenceEvaluation.Begin()) reference = substep();
        AssertSame(reference.Support, optimized.Support, context);
        AssertBits(reference.Feet, optimized.Feet, context, "feet");
        AssertBits(new Vector3(reference.Achieved, reference.Rise), new Vector3(optimized.Achieved, optimized.Rise),
            context, "achieved and rise");
        Assert.True(reference.Equals(optimized), $"{context}: substep {optimized} differs from reference {reference}.");
        return optimized;
    }

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
