using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// The added-cost budget only exists for one authorized on/off unit run in order in one process. Fake sessions, a fake
// clock and an injected environment exercise every validity rule without a fixture, a device or elapsed time.
public sealed class CoverageCostPairTests
{
    internal const string Pair = """
        {
          "schema": "khaozengine.coverage-cost-pair/1",
          "unit": "typical-a32-cpuskin-full",
          "order": ["off", "on"],
          "actors": 32,
          "gpuSkinning": false,
          "fixture": "typical",
          "passes": "full",
          "device": { "backend": "recording-cpu", "rejectSoftwareAdapter": false, "adapterContains": null },
          "warmupFrames": 4,
          "measuredFrames": 3,
          "limits": { "maxWallSeconds": 60, "maxFrameMilliseconds": 50 },
          "gpuExecutionMetric": "unavailable",
          "authorization": { "identity": "unit-test-only", "approvedBy": "nobody", "reference": "fake-session" }
        }
        """;

    static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
    static string With(string from, string to)
    {
        Assert.Contains(from, Pair);
        return Pair.Replace(from, to, StringComparison.Ordinal);
    }

    // On is all covered and costs prepare 12 + submission 2 + reserve 1 + resolution 9 + skinned preparation + motion
    // commit. Off costs offPrepare + 2 + 1 + 9 + its skinned preparation + its commit. Children never enter the sum.
    static CoverageCostPairReport Run(string json = Pair, long offPrepare = 12, long onPreparation = 4, long onCommit = 1,
        bool synthetic = false, Func<CoverageCostManifest, CoverageCostFakeSession>? session = null, List<string>? opened = null,
        Func<int, IReadOnlyList<KeyValuePair<string, string>>>? environment = null, CoverageCostClock? clock = null,
        Action<CoverageCostReport>? publish = null)
    {
        int index = 0;
        return CoverageCostDriver.RunPair(Bytes(json), (manifest, _) =>
        {
            opened?.Add(manifest.Case);
            return session?.Invoke(manifest) ?? new CoverageCostFakeSession
            {
                Coverage = manifest.Coverage,
                Synthetic = synthetic,
                AllCovered = true,
                PrepareTicks = manifest.Coverage ? 12 : offPrepare,
                SkinnedPreparationTicks = manifest.Coverage ? onPreparation : 4,
                MotionCommitTicks = manifest.Coverage ? onCommit : 1,
            };
        }, clock ?? CoverageCostFakes.Clock(), () => environment?.Invoke(index++) ?? CoverageCostFakes.Environment(), publish);
    }

    [Fact]
    public void Pair_parses_both_conditions_and_runs_them_in_the_authorized_order()
    {
        var pair = CoverageCostPairManifest.Parse(Bytes(Pair));
        Assert.Equal("typical-a32-cpuskin-full", pair.Unit);
        Assert.Equal(new[] { false, true }, pair.Conditions.Select(c => c.Coverage));
        Assert.Equal(new[] { "typical-a32-cpuskin-full-off", "typical-a32-cpuskin-full-on" }, pair.Conditions.Select(c => c.Case));
        Assert.All(pair.Conditions, c => Assert.Equal((32, 4, 3, pair.Sha256), (c.Actors, c.WarmupFrames, c.MeasuredFrames, c.Sha256)));

        var opened = new List<string>();
        var report = Run(With("\"order\": [\"off\", \"on\"]", "\"order\": [\"on\", \"off\"]"), opened: opened);
        Assert.Equal(new[] { "typical-a32-cpuskin-full-on", "typical-a32-cpuskin-full-off" }, opened);
        Assert.Equal(CoverageCostRunStatus.Completed, report.Status);
        Assert.Equal(new[] { "on", "off" }, report.Order);
        Assert.Equal(new[] { true, false }, report.Reports.Select(r => r!.Manifest!.Coverage));
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal(2, json.RootElement.GetProperty("reports").GetArrayLength());
    }

    public static TheoryData<string, string> InvalidPairs => new()
    {
        { "\"order\": [\"off\", \"on\"]", "\"order\": [\"on\", \"on\"]" },
        { "\"order\": [\"off\", \"on\"]", "\"order\": [\"off\"]" },
        { "\"order\": [\"off\", \"on\"]", "\"order\": \"off,on\"" },
        { "\"unit\": \"typical-a32-cpuskin-full\"", "\"unit\": \"Typical pair\"" },
        { "\"actors\": 32,", "\"actors\": 32, \"coverage\": \"on\"," },
        { "\"identity\": \"unit-test-only\"", "\"identity\": \"\"" },
        { "\"measuredFrames\": 3", "\"measuredFrames\": 0" },
        { "\"schema\": \"khaozengine.coverage-cost-pair/1\"", "\"schema\": \"khaozengine.coverage-cost-manifest/1\"" },
    };

    [Theory]
    [MemberData(nameof(InvalidPairs))]
    public void An_invalid_pair_is_refused_before_either_session_opens(string from, string to)
    {
        Assert.Throws<InvalidDataException>(() => CoverageCostPairManifest.Parse(Bytes(With(from, to))));
        var opened = new List<string>();
        var report = Run(With(from, to), opened: opened);
        Assert.Equal(CoverageCostRunStatus.Refused, report.Status);
        Assert.Empty(opened);
        Assert.All(report.Reports, r => Assert.Null(r));
        Assert.All(report.Budgets, b => Assert.Equal(CoverageCostBudgetStatus.Unproved, b.Status));
    }

    [Fact]
    public void Added_cpu_counts_every_disjoint_preparation_span_on_both_conditions()
    {
        // Off: 12 + 2 + 1 + 9 + 4 + 1 = 29. On: 29. Added 0.
        var equal = Run();
        var budget = equal.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal((CoverageCostBudgetStatus.Pass, 0d), (budget.Status, budget.Observed!.Value));
        Assert.Contains("prepare.total + submission + reserve + encode.resolution + encode.skinnedPreparation + encode.motionCommit",
            budget.Reason, StringComparison.Ordinal);
        // Off 1 + 2 + 1 + 9 + 4 + 1 = 18, added 11.
        var dear = Run(offPrepare: 1).Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal((CoverageCostBudgetStatus.Fail, 11d), (dear.Status, dear.Observed!.Value));
        // A session that cannot observe the skinned preparation proves nothing.
        var blind = Run(session: m => new CoverageCostFakeSession
        { Coverage = m.Coverage, Synthetic = false, AllCovered = true, ObservesSkinnedPreparation = false })
            .Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, blind.Status);
        Assert.Contains("encode.skinnedPreparation", blind.Reason, StringComparison.Ordinal);

        var deltas = equal.Deltas;
        Assert.Equal((CoverageCostStatus.Derived, 0d), (deltas.Single(d => d.Name == "delta.encode.total").Status,
            deltas.Single(d => d.Name == "delta.encode.total").Value!.Value));
        Assert.Equal(0, deltas.Single(d => d.Name == "delta.frame.wall").Value);
        Assert.Equal(CoverageCostBudgetStatus.Pass, equal.Budgets.Single(b => b.Name == "alloc.warmCoverage").Status);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, equal.Budgets.Single(b => b.Name == "gpu.completedFrameOverhead.native").Status);
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(4, 4)]
    public void Work_outside_the_child_spans_is_charged_through_the_parent_and_the_commit(long onPreparation, long onCommit)
    {
        // Child timings stay fixed between the theory rows. They differ by condition (surface 1 vs 0, ordinary skinning 0 vs 2) but never
        // enter the sum, so only the parent's own motion work or the commit moves the 3 ms difference.
        var report = Run(onPreparation: onPreparation, onCommit: onCommit);
        var budget = report.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal(CoverageCostBudgetStatus.Fail, budget.Status);
        Assert.Equal(3, budget.Observed!.Value, 9);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, report.Budgets.Single(b => b.Name == CoverageCostBudgets.GpuName).Status);
    }

    [Fact]
    public void One_unit_deadline_covers_both_conditions_and_their_setup()
    {
        // Each factory costs 20 s and each of the seven frames 4 s. Separate clocks would let both conditions finish.
        long now = 0;
        var opened = new List<string>();
        var report = Run(clock: new CoverageCostClock(() => now, 1000), opened: opened, session: m =>
        {
            now += 20_000;
            return new CoverageCostFakeSession { Coverage = m.Coverage, Synthetic = false, AllCovered = true, OnFrame = _ => now += 4_000 };
        });
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Equal(CoverageCostRunStatus.Completed, report.Reports[0]!.Status);
        Assert.Equal(CoverageCostRunStatus.Failed, report.Reports[1]!.Status);
        Assert.Contains("The unit elapsed 68 s after session setup, above limits.maxWallSeconds 60 s", report.Reports[1]!.Reason,
            StringComparison.Ordinal);
        Assert.Equal(0, report.Reports[1]!.MeasuredFrames);
        Assert.Equal(2, opened.Count);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, report.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName).Status);
    }

    [Fact]
    public void An_exhausted_deadline_never_starts_the_next_factory()
    {
        // The first condition finishes at 58.5 s. Publishing it costs 2 s, so the deadline is spent before the second.
        long now = 0;
        var opened = new List<string>();
        var report = Run(clock: new CoverageCostClock(() => now, 1000), opened: opened, publish: _ => now += 2_000, session: m =>
        {
            now += 20_000;
            return new CoverageCostFakeSession { Coverage = m.Coverage, Synthetic = false, AllCovered = true, OnFrame = _ => now += 5_500 };
        });
        Assert.Equal(new[] { "typical-a32-cpuskin-full-off" }, opened);
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Equal(CoverageCostRunStatus.Completed, report.Reports[0]!.Status);
        Assert.Null(report.Reports[1]);
        Assert.Contains("The unit elapsed 60.5 s before condition on, above limits.maxWallSeconds 60 s", report.Reason, StringComparison.Ordinal);
        Assert.Contains("Condition on was not run.", report.Reason, StringComparison.Ordinal);
    }

    // Seven frames per condition at 4.2 s with free factories. Off ends at 29.4 s and on at 58.8 s, so every check on the last
    // condition passes and only its publication can spend the 60 s deadline.
    static CoverageCostPairReport RunLastPublication(long publicationMilliseconds, List<CoverageCostReport> published)
    {
        long now = 0;
        return Run(clock: new CoverageCostClock(() => now, 1000), publish: r =>
        {
            published.Add(r);
            if (r.Manifest!.Coverage) now += publicationMilliseconds;
        }, session: m => new CoverageCostFakeSession
        {
            Coverage = m.Coverage,
            Synthetic = false,
            AllCovered = true,
            OnFrame = _ => now += 4_200,
        });
    }

    [Fact]
    public void The_last_condition_publication_is_charged_to_the_unit()
    {
        var published = new List<CoverageCostReport>();
        var report = RunLastPublication(2_000, published);
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("The unit elapsed 60.8 s after condition on publication, above limits.maxWallSeconds 60 s", report.Reason,
            StringComparison.Ordinal);
        Assert.Equal(2, published.Count);
        Assert.Same(published[0], report.Reports[0]);
        Assert.Same(published[1], report.Reports[1]);
        Assert.All(report.Reports, r => Assert.Equal(CoverageCostRunStatus.Completed, r!.Status));
        Assert.Equal(3, report.Budgets.Count);
        Assert.All(report.Budgets, b =>
        {
            Assert.Equal(CoverageCostBudgetStatus.Unproved, b.Status);
            Assert.Null(b.Observed);
            Assert.Contains("after condition on publication", b.Reason, StringComparison.Ordinal);
        });
        var cpu = report.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal((CoverageCostBudgets.AddedCpuMilliseconds, "ms/frame"), (cpu.Threshold, cpu.Unit));
        var gpu = report.Budgets.Single(b => b.Name == CoverageCostBudgets.GpuName);
        Assert.Equal((CoverageCostBudgets.GpuOverheadPercent, "percent"), (gpu.Threshold, gpu.Unit));
        Assert.Equal((0d, "bytes/frame"), (report.Budgets.Single(b => b.Name == CoverageCostBudgets.AllocationName).Threshold,
            report.Budgets.Single(b => b.Name == CoverageCostBudgets.AllocationName).Unit));
        Assert.Equal(2, report.Deltas.Count);
    }

    [Fact]
    public void A_last_publication_within_the_limit_leaves_the_unit_complete()
    {
        var published = new List<CoverageCostReport>();
        var report = RunLastPublication(1_000, published);
        Assert.Equal(CoverageCostRunStatus.Completed, report.Status);
        Assert.Equal("", report.Reason);
        Assert.NotEqual(CoverageCostBudgetStatus.Unproved, report.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName).Status);
        Assert.NotEqual(CoverageCostBudgetStatus.Unproved, report.Budgets.Single(b => b.Name == CoverageCostBudgets.AllocationName).Status);
    }

    [Fact]
    public void Each_finished_condition_is_published_before_the_next_one_starts()
    {
        var events = new List<string>();
        var report = Run(opened: events, publish: r => events.Add($"published {r.Manifest!.Case}"));
        Assert.Equal(CoverageCostRunStatus.Completed, report.Status);
        Assert.Equal(new[] { "typical-a32-cpuskin-full-off", "published typical-a32-cpuskin-full-off",
            "typical-a32-cpuskin-full-on", "published typical-a32-cpuskin-full-on" }, events);
    }

    [Fact]
    public void A_publication_failure_keeps_the_report_and_stops_the_unit()
    {
        var opened = new List<string>();
        var report = Run(opened: opened, publish: _ => throw new IOException("sink closed"));
        Assert.Equal(new[] { "typical-a32-cpuskin-full-off" }, opened);
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("publication failed: IOException: sink closed", report.Reason, StringComparison.Ordinal);
        Assert.NotNull(report.Reports[0]);
        Assert.Null(report.Reports[1]);
        Assert.All(report.Budgets, b => Assert.Equal(CoverageCostBudgetStatus.Unproved, b.Status));
    }

    public static TheoryData<string> InvalidOutcomes => new() { "declined", "fallback", "mixed", "incomplete", "error", "synthetic" };

    [Theory]
    [MemberData(nameof(InvalidOutcomes))]
    public void Budgets_refuse_declined_fallback_incomplete_failed_or_synthetic_runs(string shape)
    {
        var report = Run(synthetic: shape == "synthetic", session: shape == "synthetic" ? null : m => new CoverageCostFakeSession
        {
            Coverage = m.Coverage,
            Synthetic = false,
            AllCovered = shape is "incomplete" or "error",
            AllDeclined = shape == "declined",
            AllFallback = shape == "fallback",
            SlowFrame = shape == "incomplete" && m.Coverage ? 1 : int.MinValue,
            ThrowAt = shape == "error" && !m.Coverage ? 5 : -1,
        });
        foreach (var budget in report.Budgets) Assert.Equal(CoverageCostBudgetStatus.Unproved, budget.Status);
        Assert.Null(report.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName).Observed);
        if (shape == "declined") Assert.Contains("declined", report.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName).Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_first_condition_leaves_the_second_not_run_and_the_budget_unproved()
    {
        var opened = new List<string>();
        var report = Run(opened: opened, session: m => new CoverageCostFakeSession { Coverage = m.Coverage, Synthetic = false, ThrowAt = 0 });
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Equal(new[] { "typical-a32-cpuskin-full-off" }, opened);
        Assert.NotNull(report.Reports[0]);
        Assert.Null(report.Reports[1]);
        Assert.Contains("not run", report.Reason, StringComparison.Ordinal);
        Assert.All(report.Budgets, b => Assert.Equal(CoverageCostBudgetStatus.Unproved, b.Status));
    }

    public static TheoryData<string> Mismatches => new() { "commit", "cpu", "runId", "unavailable", "adapter", "topology" };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public void Budgets_require_one_run_identity_and_one_fixture(string mismatch)
    {
        var report = Run(environment: index => mismatch switch
        {
            "commit" when index == 1 => CoverageCostFakes.Environment(commit: "def456"),
            "cpu" when index == 1 => CoverageCostFakes.Environment(cpu: "other cpu"),
            "runId" when index == 1 => CoverageCostFakes.Environment(runId: "run-2"),
            "unavailable" => CoverageCostFakes.Environment(commit: "unavailable"),
            _ => CoverageCostFakes.Environment(),
        }, session: m => new CoverageCostFakeSession
        {
            Coverage = m.Coverage,
            Synthetic = false,
            AllCovered = true,
            Adapter = mismatch == "adapter" && m.Coverage ? "other adapter" : "fake adapter",
            Topology = mismatch == "topology" && m.Coverage ? new(1, 2000, 200, 96, 2900, 2600, 2, 2, "2", 2, "other") : new(2121, 2000, 200, 96, 2900, 2600, 2, 2, "2", 2, "fingerprint"),
        });
        var budget = report.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, budget.Status);
        Assert.Contains(mismatch == "unavailable" ? "commit" : mismatch == "topology" ? "topology" : mismatch, budget.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_native_adapter_must_be_named_and_a_recording_device_proves_no_hardware()
    {
        var unnamed = Run(session: m => new CoverageCostFakeSession { Coverage = m.Coverage, Synthetic = false, AllCovered = true, Adapter = "" });
        Assert.Equal(CoverageCostBudgetStatus.Unproved, unnamed.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName).Status);
        var gpu = Run().Budgets.Single(b => b.Name == "gpu.completedFrameOverhead.native");
        Assert.Equal(CoverageCostBudgetStatus.Unproved, gpu.Status);
        Assert.Null(gpu.Observed);
    }

    [Fact]
    public void Single_case_reports_never_carry_an_added_cost_verdict()
    {
        var single = CoverageCostDriver.Run(Bytes(CoverageCostContractTests.Valid), (_, _) => new CoverageCostFakeSession
        { Synthetic = false, AllCovered = true }, CoverageCostFakes.Clock(), () => CoverageCostFakes.Environment());
        var budget = single.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, budget.Status);
        Assert.Contains("paired unit", budget.Reason, StringComparison.Ordinal);
        Assert.Equal(CoverageCostBudgetStatus.Pass, single.Budgets.Single(b => b.Name == "alloc.warmCoverage").Status);
    }
}
