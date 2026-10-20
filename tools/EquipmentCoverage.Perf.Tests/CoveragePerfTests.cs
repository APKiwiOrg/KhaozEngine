using System;
using System.Diagnostics;
using System.IO;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace EquipmentCoverage.Perf.Tests;

public sealed class CoveragePerfTests
{
    [CoveragePerfFact]
    [Trait("Category", "CoveragePerformance")]
    public void Measure_authorized_unit()
    {
        string root = CoveragePerfDispatch.RepositoryRoot(AppContext.BaseDirectory);
        var request = CoveragePerfDispatch.Load(Path.Combine(AppContext.BaseDirectory, "authorized"),
            Environment.GetEnvironmentVariable, (unit, workload) => CoveragePerfAuthorizationCommit.Commit(root, workload, unit));
        var identity = CoveragePerfDispatch.Identity(request, Environment.GetEnvironmentVariable);
        string directory = Environment.GetEnvironmentVariable("KE_COVERAGE_PERF_REPORTS")
            ?? throw new InvalidDataException("KE_COVERAGE_PERF_REPORTS must name the retained output directory.");
        Assert.False(string.IsNullOrWhiteSpace(directory), "KE_COVERAGE_PERF_REPORTS must not be empty.");
        using var publication = new CoveragePerfPublication(directory, request.Manifest.UnitId);
        var clock = new CoveragePerfClock(Stopwatch.GetTimestamp, Stopwatch.Frequency);
        long start = clock.Now();
        var report = CoveragePerfDriver.Run(request.Bytes, identity,
            (manifest, block, time) => new CoveragePerfNativeSession(manifest, block, CoveragePerfNativeHost.Open, time),
            clock, _ => { }, publication.StageCandidate);
        // Only the returned envelope contains publication and deadline failures. The candidate is never promoted.
        report = publication.PublishReturned(report, clock, start);
        Assert.True(report.Status == CoveragePerfRunStatus.Completed && report.FinalUnitPublished, report.Reason);
    }
}

public sealed class CoveragePerfFactAttribute : FactAttribute
{
    public CoveragePerfFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_COVERAGE_PERF_UNIT")))
            Skip = "KE_COVERAGE_PERF_UNIT is unset. A separately owner-approved measurement dispatch is required.";
    }
}
