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

    // On is all covered and costs prepare 12 + submission 2 + reserve 1 + resolution 9 + surface 1 + ordinary 0 +
    // upload 1 = 26 ms. Off costs offPrepare + 2 + 1 + 9 + 0 + ordinary + 1.
    static CoverageCostPairReport Run(string json = Pair, long offPrepare = 12, long ordinary = 2, bool synthetic = false,
        Func<CoverageCostManifest, CoverageCostFakeSession>? session = null, List<string>? opened = null,
        Func<int, IReadOnlyList<KeyValuePair<string, string>>>? environment = null)
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
                OrdinarySkinningTicks = ordinary,
            };
        }, CoverageCostFakes.Clock(), () => environment?.Invoke(index++) ?? CoverageCostFakes.Environment());
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
        // Off: 11 + 2 + 1 + 9 + 0 + 2 + 1 = 26. On: 26. Added 0.
        var equal = Run(offPrepare: 11);
        var budget = equal.Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal((CoverageCostBudgetStatus.Pass, 0d), (budget.Status, budget.Observed!.Value));
        Assert.Contains("prepare.total + submission + reserve + encode.resolution + encode.surfaceAppend + encode.ordinarySkinning + encode.skinnedUpload",
            budget.Reason, StringComparison.Ordinal);
        // The ordinary skinning the off condition does is credited. Off 1 + 2 + 1 + 9 + 0 + 2 + 1 = 16, added 10.
        var dear = Run(offPrepare: 1).Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal((CoverageCostBudgetStatus.Fail, 10d), (dear.Status, dear.Observed!.Value));
        // Without the ordinary credit the same on run would be 2 ms dearer. A blind session cannot prove anything.
        var blind = Run(session: m => new CoverageCostFakeSession
        { Coverage = m.Coverage, Synthetic = false, AllCovered = true, ObservesOrdinarySkinning = false })
            .Budgets.Single(b => b.Name == CoverageCostBudgets.AddedCpuName);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, blind.Status);
        Assert.Contains("encode.ordinarySkinning", blind.Reason, StringComparison.Ordinal);

        var deltas = equal.Deltas;
        Assert.Equal((CoverageCostStatus.Derived, 0d), (deltas.Single(d => d.Name == "delta.encode.total").Status,
            deltas.Single(d => d.Name == "delta.encode.total").Value!.Value));
        Assert.Equal(0, deltas.Single(d => d.Name == "delta.frame.wall").Value);
        Assert.Equal(CoverageCostBudgetStatus.Pass, equal.Budgets.Single(b => b.Name == "alloc.warmCoverage").Status);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, equal.Budgets.Single(b => b.Name == "gpu.completedFrameOverhead.native").Status);
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
