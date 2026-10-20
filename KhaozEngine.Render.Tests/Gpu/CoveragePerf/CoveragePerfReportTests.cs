using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfReportTests
{
    public static IEnumerable<object[]> Refusals() => new[]
    {
        "synthetic", "workload", "device", "driver", "route", "schedule", "pair identity", "configuration",
        "dropped", "growth", "production wait", "unsupported", "invalid", "disjoint", "fallback",
        "publication", "final unit", "tail", "period", "actor", "outcome", "legacy", "identity",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Refusals))]
    public void InvalidEvidenceAlwaysHasASpecificUnprovedReason(string refusal)
    {
        var report = CoveragePerfReportVectors.Create("G1");
        report = refusal switch
        {
            "synthetic" => Info(report, i => i with { Synthetic = true }),
            "workload" => report with { Identity = report.Identity with { WorkloadSha = new string('f', 40) } },
            "device" => Info(report, i => i with { Device = "other device" }),
            "driver" => Info(report, i => i with { Driver = "other driver" }),
            "route" => Info(report, i => i with { CoveredSurfaceRoute = "cpu" }),
            "schedule" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { PassFingerprint = "wrong schedule" }),
            "pair identity" => CoveragePerfReportVectors.ChangeBlock(report, 5, b => b with
            { RunIdentity = b.RunIdentity! with { ProcessId = 999 } }),
            "configuration" => Info(report, i => i with { ProfileFingerprint = "other profile" }),
            "dropped" => CoveragePerfReportVectors.ChangeBlock(report, 5, b => b with { Samples = b.Samples.RemoveAt(0).RemoveAt(0) }),
            "growth" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { ResourceGrowth = 1 }),
            "production wait" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { ProductionWaits = 1 }),
            "unsupported" => Info(report, i => i with { TimestampSupported = false }),
            "invalid" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { Gpu = s.Gpu! with { Valid = false } }),
            "disjoint" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { Gpu = s.Gpu! with { Disjoint = true } }),
            "fallback" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { Fallback = true }),
            "publication" => report with { Status = CoveragePerfRunStatus.Failed, Errors = ["partial publication"] },
            "final unit" => report with { FinalUnitPublished = false },
            "tail" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { CompletedFrameTail = false }),
            "period" => Info(report, i => i with { TimestampPeriodNanoseconds = null }),
            "actor" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { MainVisibleActors = 31 }),
            "outcome" => CoveragePerfReportVectors.ChangeSample(report, 5, s => s with { DeviceComplete = false }),
            "legacy" => report with { Identity = report.Identity with { Execution = report.Identity.Execution with { LegacyBaselineSha = "wrong" } } },
            "identity" => report with { Identity = report.Identity with { Execution = report.Identity.Execution with { ClockFrequency = 0 } } },
            _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
        };
        var verdict = CoveragePerfBudgets.Evaluate(report);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, verdict.Gpu.Status);
        Assert.Contains(verdict.Gpu.Reasons, r => r.Contains(refusal, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(verdict.Gpu.Pairs, p => p.Status == CoveragePerfBudgetStatus.Pass);
    }

    [Fact]
    public void WarmupWaitsAndGrowthRemainRawAndAreDisclosedWithoutInvalidatingMeasuredFrames()
    {
        var report = CoveragePerfReportVectors.ChangeBlock(CoveragePerfReportVectors.Create(), 2, b => b with
        { Warmup = b.Warmup.SetItem(0, b.Warmup[0] with { ProductionWaits = 3, ResourceGrowth = 7 }) });
        Assert.Equal(CoveragePerfBudgetStatus.Pass, CoveragePerfBudgets.Evaluate(report).Cpu.Status);
        var summary = CoveragePerfStatistics.Summarize(report.Blocks[2], 1000000000);
        Assert.Equal(3, summary.WarmupProductionWaits);
        Assert.Equal(7, summary.WarmupResourceGrowth);
        Assert.Equal(64, summary.WarmupCpu!.Count);
    }

    [Fact]
    public void AllocationObservationsCannotUpgradeTheIndependentProof()
    {
        var report = CoveragePerfReportVectors.Create();
        var proof = CoveragePerfBudgets.Evaluate(report).AllocationProof;
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, proof.Status);
        Assert.False(proof.Reconciled);
        report = report with { AllocationProof = new("native-proof-sha", true, CoveragePerfBudgetStatus.Pass) };
        Assert.Equal(report.AllocationProof, CoveragePerfBudgets.Evaluate(report).AllocationProof);
    }

    [Fact]
    public void JsonRoundTripsIdentityManifestBytesAndEveryRawSample()
    {
        var report = CoveragePerfReportVectors.Create("G2");
        var json = CoveragePerfReportJson.Serialize(report);
        var copy = CoveragePerfReportJson.Deserialize(json);
        Assert.Equal(CoveragePerfReport.Schema, JsonDocument.Parse(json).RootElement.GetProperty("schema").GetString());
        Assert.Equal(report.Identity.WorkloadSha, copy.Identity.WorkloadSha);
        Assert.Equal(report.Identity.WorkflowSha, copy.Identity.WorkflowSha);
        Assert.Equal(report.Identity.Execution, copy.Identity.Execution);
        Assert.Equal(report.Manifest!.Utf8.ToArray(), copy.Manifest!.Utf8.ToArray());
        Assert.Equal(report.Blocks.Length, copy.Blocks.Length);
        for (int i = 0; i < report.Blocks.Length; i++)
        {
            Assert.Equal(report.Blocks[i].Session!.Driver, copy.Blocks[i].Session!.Driver);
            Assert.Equal(report.Blocks[i].Session!.Environment.ToArray(), copy.Blocks[i].Session!.Environment.ToArray());
            Assert.Equal(report.Blocks[i].Warmup.Length, copy.Blocks[i].Warmup.Length);
            Assert.Equal(report.Blocks[i].Samples.Length, copy.Blocks[i].Samples.Length);
            Assert.Equal(report.Blocks[i].Samples[0].Gpu!.Ticks.ToArray(), copy.Blocks[i].Samples[0].Gpu!.Ticks.ToArray());
            Assert.Equal(report.Blocks[i].Samples[0].CpuFramePreparationTicks, copy.Blocks[i].Samples[0].CpuFramePreparationTicks);
        }
        Assert.Equal(json, CoveragePerfReportJson.Serialize(copy));
    }

    [Fact]
    public void MarkdownCarriesVerdictsNumbersAndExplicitUnprovedReasons()
    {
        var report = CoveragePerfReportVectors.Cpu(CoveragePerfReportVectors.Create(), 2, 13);
        var markdown = CoveragePerfReportJson.Markdown(report);
        Assert.Contains("Fail", markdown);
        Assert.Contains("2 ms", markdown);
        Assert.Contains("Unproved", markdown);
        Assert.Contains("owner acceptance", markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allocation", markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DurablePublicationUsesReturnedReportNeverTheCandidate()
    {
        var run = new CoveragePerfFakeRun();
        run.Publisher.OnUnit = _ => throw new IOException("final publication failed");
        var returned = run.Run();
        var candidate = Assert.Single(run.Publisher.Units);
        Assert.False(candidate.ReturnedByRun);
        Assert.True(returned.ReturnedByRun);
        Assert.False(returned.FinalUnitPublished);
        Assert.Throws<InvalidOperationException>(() => CoveragePerfReportJson.WriteReturnedReport(candidate, "unused.json", "unused.md"));
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, CoveragePerfBudgets.Evaluate(returned).Cpu.Status);
        Assert.Contains("final publication failed", CoveragePerfReportJson.Serialize(returned));
        var successfulFake = new CoveragePerfFakeRun().Run();
        string directory = OperatingSystem.IsWindows() ? Path.Combine(Path.GetTempPath(), "khaozengine-ec-perf-7")
            : "/tmp/khaozengine-ec-perf-7";
        Directory.CreateDirectory(directory);
        string jsonPath = Path.Combine(directory, "representative-fake.json");
        string markdownPath = Path.Combine(directory, "representative-fake.md");
        CoveragePerfReportJson.WriteReturnedReport(successfulFake, jsonPath, markdownPath);
        var restored = CoveragePerfReportJson.Deserialize(File.ReadAllText(jsonPath));
        Assert.True(restored.ReturnedByRun);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, CoveragePerfBudgets.Evaluate(restored).Cpu.Status);
        Assert.Contains("Unproved", File.ReadAllText(markdownPath));
    }

    [Fact]
    public void AnEarlyFrameFailureStillSerializesItsPartialRawSampleAndFirstError()
    {
        var run = new CoveragePerfFakeRun();
        run.Setup = session => session.OnFrame = (CoveragePerfFakeSession _, int _, ref CoveragePerfFrameSample sample) =>
        {
            sample = default;
            sample.Error = "early frame failure";
            throw new InvalidOperationException("early frame failure");
        };
        var report = run.Run();
        var copy = CoveragePerfReportJson.Deserialize(CoveragePerfReportJson.Serialize(report));
        Assert.Equal(CoveragePerfRunStatus.Failed, copy.Status);
        Assert.Contains("early frame failure", copy.Reason);
        var partial = Assert.Single(Assert.Single(copy.Blocks).Warmup);
        Assert.True(partial.CpuChildren.IsDefault);
        Assert.Equal("early frame failure", partial.Error);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, CoveragePerfBudgets.Evaluate(copy).Cpu.Status);
    }

    static CoveragePerfReport Info(CoveragePerfReport report, Func<CoveragePerfSessionInfo, CoveragePerfSessionInfo> change) =>
        CoveragePerfReportVectors.ChangeBlock(report, 5, b => b with { Session = change(b.Session!) });
}
