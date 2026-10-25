using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public sealed class ContactNavigationProofReuseWorkTests(ContactNavigationProofReuseWorkTests.Measurements measurements,
    ITestOutputHelper output) : IClassFixture<ContactNavigationProofReuseWorkTests.Measurements>
{
    internal sealed record Target(int Volumes, int Avoided, int EdgeSteps, int Holds, int Find, int FindMemoHits);

    internal static readonly IReadOnlyDictionary<string, Target> Targets = new Dictionary<string, Target>
    {
        ["low-lip-bank"] = new(8108, 2386, 4032, 328, 12200, 4092),
        ["deck-over-ground"] = new(16024, 7944, 8064, 656, 24208, 8184),
        ["wade-only-pool"] = new(24752, 12308, 12416, 548, 37276, 12524),
        ["rebased-origin"] = new(8108, 3940, 4032, 328, 12200, 4092),
        ["mixed-legacy-contact"] = new(8012, 3972, 4032, 328, 12104, 4092),
        ["stair-accepted-refused"] = new(4116, 1259, 1686, 17, 5827, 1711),
        ["multi-layer-row-bands"] = new(10379, 5286, 6076, 80, 20612, 10233),
        ["wide-lip-576"] = new(69532, 29234, 34992, 2888, 104984, 35452),
        ["single-flat"] = new(5, 0, 0, 0, 6, 1),
    };

    public static IEnumerable<object[]> Matrix => Targets.Keys.SelectMany(fixture =>
        new[] { 1, 2, 3, 8, (fixture == "wide-lip-576" ? 48 : fixture == "single-flat" ? 1 :
            ContactNavigationProofReuseBaselineTests.Rows(fixture)) + 1 }.Distinct().Select(degree => new object[] { fixture, degree }));

    [Theory]
    [MemberData(nameof(Matrix))]
    public void FootprintOnlyMeetsRecordedTargetsWithoutSkippingFinds(string fixture, int degree)
    {
        var measured = measurements.Get(fixture, degree);
        Write(fixture, degree, measured);
        Target target = Targets[fixture];
        var reference = measured[0];
        var footprints = measured[1];
        // Four lifted/standing pairs skip both seat reads when the raw wall sweep has no residual motion.
        // Each pair otherwise performs two Finds, with one interval memo hit. The isolated wall probe
        // selects that arithmetic path without running a bake or assuming a CPU/OS combination.
        int skippedPairs = fixture == "stair-accepted-refused" && measurements.StairWall.Repeat.Achieved == default ? 4 : 0;
        Assert.Equal(target.Volumes - skippedPairs, reference.Counts.Volumes);
        Assert.Equal(target.Find - 2 * skippedPairs, reference.Counts.Find);
        Assert.Equal(target.FindMemoHits - skippedPairs, reference.Counts.FindMemoHits);
        Assert.Equal(target.EdgeSteps, reference.Counts.EdgeSteps);
        Assert.Equal(reference.File, footprints.File);
        Assert.Equal(reference.Counts.Find, footprints.Counts.Find);
        Assert.Equal(reference.Counts.FindMemoHits, footprints.Counts.FindMemoHits);
        Assert.Equal(reference.FindTrace, footprints.FindTrace);
        if (degree == 1) Assert.Equal(reference.OrderedFindTrace, footprints.OrderedFindTrace);
        Assert.Equal(reference.Counts.AnalyticSamples, footprints.Counts.AnalyticSamples);
        Assert.Equal(reference.Counts.Bindings, footprints.Counts.Bindings);
        Assert.Equal(reference.Routes, footprints.Routes);
        Assert.Equal(reference.Counts.EdgeSteps, footprints.Counts.EdgeSteps);
        Assert.Equal(target.Avoided, reference.Counts.Volumes - footprints.Counts.Volumes);
        Assert.Equal(reference.Counts.Volumes - footprints.Counts.Volumes, footprints.Counts.FootprintReuseHits);
        Assert.Equal(0, footprints.Counts.InitialHoldReuseHits);
        Assert.Equal(reference.Counts.Find, reference.Counts.FindMemoHits + reference.Counts.Volumes);
        Assert.Equal(footprints.Counts.Find, footprints.Counts.FindMemoHits + footprints.Counts.Volumes + footprints.Counts.FootprintReuseHits);
        Assert.Equal(2 * footprints.Counts.Volumes, footprints.Counts.Proposals);
        Assert.Equal(0, reference.Counts.FootprintReuseHits);
        Assert.Equal(0, reference.Counts.FootprintReuseMisses);
        Assert.Equal(0, reference.Counts.FootprintReuseStores);
        Assert.Equal(0, reference.Counts.FootprintReuseBudgetSkips);
        Assert.Equal(0, reference.Counts.InitialHoldReuseHits);
        Assert.Equal(0, reference.Retention.PeakKeys);
        foreach (var result in measured)
        {
            Bounded(result.Retention);
            Assert.InRange(result.Counts.FootprintReuseStores, 0, result.Counts.FootprintReuseMisses);
            Assert.InRange(result.Counts.FootprintReuseBudgetSkips, 0, result.Counts.FootprintReuseMisses);
            Assert.True(result.Counts.FootprintReuseHits + result.Counts.FootprintReuseMisses <=
                result.Counts.Find - result.Counts.FindMemoHits);
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void EveryModePreservesSerialWorkAcrossWorkerAssignments(string fixture, int degree)
    {
        var serial = measurements.Get(fixture, 1);
        var measured = measurements.Get(fixture, degree);
        Write(fixture, degree, measured);
        for (int mode = 0; mode < measured.Length; mode++)
        {
            Assert.Equal(serial[mode].File, measured[mode].File);
            Assert.Equal(WorkCounts(serial[mode].Counts), WorkCounts(measured[mode].Counts));
            Assert.Equal(serial[mode].FindTrace, measured[mode].FindTrace);
            Assert.Equal(serial[mode].Routes, measured[mode].Routes);
            Assert.Equal(serial[mode].Retention, measured[mode].Retention);
        }
    }

    // Source sessions end before another source, band or phase. No work in this projection depends on which
    // worker claimed a source. First pins and shared world-form construction are deliberately outside it.
    static long[] WorkCounts(SupportQueryCounts c) => [c.Find, c.FindMemoHits, c.Volumes, c.Proposals,
        c.FootprintReuseHits, c.FootprintReuseMisses, c.FootprintReuseStores, c.FootprintReuseBudgetSkips,
        c.EdgeSteps, c.InitialHoldReuseHits, c.AnalyticSamples, c.Bindings];

    [Theory]
    [MemberData(nameof(Matrix))]
    public void CombinedModeSavesOnlyCertifiedInitialHolds(string fixture, int degree)
    {
        var measured = measurements.Get(fixture, degree);
        // Print before assertions too, so a failed target still leaves the honest measured counters.
        Write(fixture, degree, measured);
        Target target = Targets[fixture];
        var reference = measured[0];
        var combined = measured[2];
        Assert.Equal(reference.File, combined.File);
        Assert.Equal(target.EdgeSteps, reference.Counts.EdgeSteps);
        Assert.Equal(target.Holds, combined.Counts.InitialHoldReuseHits);
        Assert.Equal(reference.Counts.EdgeSteps - target.Holds, combined.Counts.EdgeSteps);
        Assert.Equal(combined.Counts.Find, combined.Counts.FindMemoHits + combined.Counts.Volumes + combined.Counts.FootprintReuseHits);
        Assert.Equal(2 * combined.Counts.Volumes, combined.Counts.Proposals);
        Assert.True(combined.Counts.Volumes <= measured[1].Counts.Volumes);
        foreach (var result in measured) Bounded(result.Retention);
        if (fixture == "single-flat")
        {
            Assert.All(measured, result => Assert.Equal(5, result.Counts.Volumes));
            Assert.All(measured, result => Assert.Equal(0, result.Counts.EdgeSteps));
            Assert.All(measured, result => Assert.Equal(0, result.Counts.InitialHoldReuseHits));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FootprintOnlyPreservesTheOrderedLiveProviderTrace(bool physicsWins)
    {
        using var fixture = new ContactNavigationReuseFixture(physicsWins);
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            byte[] reference;
            using (ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(ContactNavigationReuseMode.Off)))
                reference = fixture.Build();
            object[] trace = [.. fixture.Trace];
            fixture.Trace.Clear();
            SupportQueryDiagnostics.Reset();
            using (ContactNavigationBuildHooks.Install(ContactNavigationReuseFixture.Hooks(ContactNavigationReuseMode.Footprints)))
                Assert.Equal(reference, fixture.Build());
            Assert.Equal(trace, fixture.Trace.ToArray());
            Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseHits > 0);
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits);
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    static void Bounded(SupportProofReuseRetention retention)
    {
        Assert.InRange(retention.PeakKeys, 0, 512);
        Assert.InRange(retention.PeakBytes, 0, 2 * 1024 * 1024);
        Assert.Equal(0, retention.Keys);
        Assert.Equal(0, retention.Bytes);
    }

    void Write(string fixture, int degree, Result[] results)
    {
        if (fixture == "stair-accepted-refused")
            output.WriteLine($"WALL approach={measurements.StairWall.Approach.Achieved} repeat={measurements.StairWall.Repeat.Achieved}");
        for (int mode = 0; mode < results.Length; mode++)
        {
            var result = results[mode];
            var c = result.Counts;
            output.WriteLine($"WORK {fixture} degree={degree} mode={(ContactNavigationReuseMode)mode} " +
                $"Find={c.Find} ObservedFinds={result.ObservedFinds} FindMemoHits={c.FindMemoHits} Volumes={c.Volumes} Proposals={c.Proposals} " +
                $"Hits={c.FootprintReuseHits} Misses={c.FootprintReuseMisses} Stores={c.FootprintReuseStores} " +
                $"BudgetSkips={c.FootprintReuseBudgetSkips} EdgeSteps={c.EdgeSteps} Holds={c.InitialHoldReuseHits} " +
                $"Keys={result.Retention.Keys} Bytes={result.Retention.Bytes} " +
                $"PeakKeys={result.Retention.PeakKeys} PeakBytes={result.Retention.PeakBytes}");
        }
    }

    internal sealed record Result(byte[] File, SupportQueryCounts Counts, SupportProofReuseRetention Retention,
        int ObservedFinds, string FindTrace, string OrderedFindTrace, SupportRouteCounts Routes);

    // Share observations, never physics state or mutable reuse sessions. Each named configuration builds once.
    public sealed class Measurements
    {
        internal readonly (ShellSweep Approach, ShellSweep Repeat) StairWall = ContactNavigationReuseWorkFixtures.StairWallSweeps();
        readonly ConcurrentDictionary<(string Fixture, int Degree), Lazy<Result[]>> _results = new();
        internal Result[] Get(string fixture, int degree) => _results.GetOrAdd((fixture, degree), key =>
            new(() => Measure(key.Fixture, key.Degree), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        static Result[] Measure(string fixture, int degree)
        {
            var results = new Result[3];
            foreach (ContactNavigationReuseMode mode in Enum.GetValues<ContactNavigationReuseMode>())
            {
                var finds = new ConcurrentQueue<string>();
                Action<SupportFindObservation> observe = observation => finds.Enqueue(FindBits(observation));
                var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true) with { Find = observe });
                var priorWorkerObserver = WorkerDiagnostics.ExchangeFindObserver(observe);
                try
                {
                    var bake = ContactNavigationReuseFixture.Build(fixture, mode, degree);
                    SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
                    Assert.Equal(counts.Find, finds.Count);
                    results[(int)mode] = new(GroundNavigationBakeRoundTripTests.Write(bake.Bake), counts,
                        SupportQueryDiagnostics.Retention, finds.Count, Digest(finds.Order(StringComparer.Ordinal)), Digest(finds),
                        SupportQueryDiagnostics.Routes);
                }
                finally
                {
                    WorkerDiagnostics.ExchangeFindObserver(priorWorkerObserver);
                    SupportQueryDiagnostics.Exchange(prior);
                }
            }
            return results;
        }

        static string Digest(IEnumerable<string> lines)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> bytes = stackalloc byte[128];
            ReadOnlySpan<byte> separator = [10];
            foreach (string line in lines)
            {
                int count = Encoding.ASCII.GetBytes(line.AsSpan(), bytes);
                hash.AppendData(bytes[..count]);
                hash.AppendData(separator);
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        static string FindBits(SupportFindObservation value) => string.Join(",", new[] { value.Axis.X, value.Axis.Y,
            value.FeetY, value.Radius, value.ReachUp, value.ReachDown, value.CosMaxSlope, value.Height, value.HeightError }
            .Select(number => BitConverter.SingleToInt32Bits(number).ToString("x8"))) + "," + value.Status;
    }
}
