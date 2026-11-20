using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>Each corpus world is recorded and compared once, then read by every test of the class.</summary>
public sealed class WaterBoundaryParityRuns
{
    readonly ConcurrentDictionary<string, Lazy<WaterBoundaryParityReport>> _reports = new();
    internal WaterBoundaryParityReport Get(string name) =>
        _reports.GetOrAdd(name, key => new(() => WaterBoundaryParityRun.Run(key))).Value;
}

/// <summary>The capture certificate answers the water boundary question exactly. On every corpus question it gives
/// the full solve's status, normals and hit bits, except where the full solve refused and the certificate answered
/// Known with no constraint. Those are the listed refusal kinds and are reported, never hidden.</summary>
public class WaterBoundaryCertificateParityTests(WaterBoundaryParityRuns runs, ITestOutputHelper output)
    : IClassFixture<WaterBoundaryParityRuns>
{
    public static TheoryData<string> All => [.. WaterBoundaryParityCorpus.Certifying, .. WaterBoundaryParityCorpus.Declining];
    public static TheoryData<string> Certifying => [.. WaterBoundaryParityCorpus.Certifying];
    public static TheoryData<string> Declining => [.. WaterBoundaryParityCorpus.Declining];
    public static TheoryData<string> TwoLevelTile => ["weir", "region-seam"];

    [Theory]
    [MemberData(nameof(All))]
    public void TheCertifiedAnswerEqualsTheFullSolveOrAnswersAListedRefusal(string world)
    {
        WaterBoundaryParityReport report = Report(world);
        Assert.True(report.Questions > 0);
        Assert.Equal(report.Questions * 6, report.Comparisons);
        Assert.Empty(report.Unlisted);
        Assert.All(report.Listed.Keys, kind => Assert.Contains(kind, new[] { MovementAvailability.PrecisionExceeded,
            MovementAvailability.Unresolved, MovementAvailability.CapacityExceeded, MovementAvailability.Invalid }));
    }

    // The guard that the parity comparison is not the full path compared with itself.
    [Theory]
    [MemberData(nameof(Certifying))]
    public void TheCertificateAnswersInEveryWorldThatShouldUseIt(string world) =>
        Assert.True(Report(world).Certified > 0, $"{world}: the certificate never answered");

    [Theory]
    [MemberData(nameof(Declining))]
    public void TheCertificateDeclinesAcrossTwoLevelsAndForClosedWater(string world) =>
        Assert.Equal(0, Report(world).Certified);

    // A declining tile world must actually put two levels within every question's reach.
    [Theory]
    [MemberData(nameof(TwoLevelTile))]
    public void EveryQuestionOfATwoLevelWorldReachesBothLevels(string world)
    {
        WaterBoundaryParityReport report = Report(world);
        Assert.True(report.Questions > 0);
        Assert.Equal(report.Questions, report.TwoLevelQuestions);
    }

    [Theory]
    [MemberData(nameof(Certifying))]
    public void EveryKnownTraceContactLiesInACollectedColumnAtItsOwnLevel(string world)
    {
        WaterBoundaryParityReport report = Report(world);
        Assert.Empty(report.UncollectedContacts);
    }

    [Fact]
    public void TheCorpusExercisesContactsAndBothCertifiedPolicies()
    {
        var reports = WaterBoundaryParityCorpus.Certifying.Select(Report).ToArray();
        Assert.True(reports.Sum(r => r.KnownContacts) > 0);
        Assert.True(reports.Sum(r => r.CertifiedByMode.GetValueOrDefault(WaterTraversalMode.SurfaceSwimmer)) > 0);
        Assert.True(reports.Sum(r => r.CertifiedByMode.GetValueOrDefault(WaterTraversalMode.WadeOnly)) > 0);
    }

    WaterBoundaryParityReport Report(string world)
    {
        WaterBoundaryParityReport report = runs.Get(world);
        output.WriteLine(report.ToString());
        foreach (string line in report.Unlisted.Take(8).Concat(report.UncollectedContacts.Take(8))) output.WriteLine(line);
        return report;
    }
}
