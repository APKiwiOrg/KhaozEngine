using System;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfBudgetTests
{
    [Theory]
    [InlineData(2.0, true)]
    [InlineData(2.000001, false)]
    public void CpuGatePinsTheBoundaryAndRequiresBothOrders(double delta, bool pass)
    {
        var report = CoveragePerfReportVectors.Cpu(CoveragePerfReportVectors.Create(), 2, 10 + delta);
        report = CoveragePerfReportVectors.Cpu(report, 3, 12);
        var gate = CoveragePerfBudgets.Evaluate(report).Cpu;
        Assert.Equal(pass ? CoveragePerfBudgetStatus.Pass : CoveragePerfBudgetStatus.Fail, gate.Status);
        Assert.Equal(2, gate.Pairs.Length);
        Assert.Equal((1, 2), (gate.Pairs[0].OffBlock, gate.Pairs[0].OnBlock));
        Assert.Equal((4, 3), (gate.Pairs[1].OffBlock, gate.Pairs[1].OnBlock));
        Assert.Equal((delta + 2) / 2, gate.PooledDeltaMilliseconds!.Value, 8);
        Assert.Equal(delta - 2, gate.OrderDifferenceMilliseconds!.Value, 8);
    }

    [Theory]
    [InlineData("G1", 15.0, true)]
    [InlineData("G2", 15.0, true)]
    [InlineData("G3", 15.0, true)]
    [InlineData("G1", 15.000001, false)]
    public void GpuGatePinsExactBackendTotalPairs(string preset, double percent, bool pass)
    {
        var report = CoveragePerfReportVectors.Gpu(CoveragePerfReportVectors.Create(preset), 5, 10 * (1 + percent / 100));
        report = CoveragePerfReportVectors.Gpu(report, 6, 11.5);
        var gate = CoveragePerfBudgets.Evaluate(report).Gpu;
        Assert.Equal(pass ? CoveragePerfBudgetStatus.Pass : CoveragePerfBudgetStatus.Fail, gate.Status);
        Assert.Equal((4, 5), (gate.Pairs[0].OffBlock, gate.Pairs[0].OnBlock));
        Assert.Equal((7, 6), (gate.Pairs[1].OffBlock, gate.Pairs[1].OnBlock));
        Assert.Equal(percent, gate.Pairs[0].Percent!.Value, 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailingOrderCannotAverageIntoAPass(bool gpu)
    {
        var report = CoveragePerfReportVectors.Create(gpu ? "G1" : "C1");
        report = gpu ? CoveragePerfReportVectors.Gpu(report, 6, 12) : CoveragePerfReportVectors.Cpu(report, 3, 13);
        var verdict = CoveragePerfBudgets.Evaluate(report);
        Assert.Equal(CoveragePerfBudgetStatus.Fail, gpu ? verdict.Gpu.Status : verdict.Cpu.Status);
    }

    [Fact]
    public void GpuUsesRatioOfMeansAndNeverAddsChildren()
    {
        var report = CoveragePerfReportVectors.Gpu(CoveragePerfReportVectors.Create("G1"), 4, 1, 19);
        report = CoveragePerfReportVectors.Gpu(report, 5, 11, 12);
        var pair = CoveragePerfBudgets.Evaluate(report).Gpu.Pairs[0];
        Assert.Equal(15, pair.Percent!.Value, 8);
        Assert.Equal(CoveragePerfBudgetStatus.Pass, pair.Status);
        Assert.Equal(CoveragePerfBudgetStatus.Pass, CoveragePerfBudgets.Evaluate(CoveragePerfReportVectors.Create()).Cpu.Status);
    }

    [Fact]
    public void NegativeDeltasArePreservedAndSharedCostHasNoThreshold()
    {
        var cpu = CoveragePerfReportVectors.Cpu(CoveragePerfReportVectors.Create(), 2, 9);
        var verdict = CoveragePerfBudgets.Evaluate(cpu);
        Assert.Equal(-1, verdict.Cpu.Pairs[0].DeltaMilliseconds);
        Assert.Equal(CoveragePerfBudgetStatus.Pass, verdict.Cpu.Status);
        Assert.All(verdict.SharedSubmission, p => Assert.Equal(CoveragePerfBudgetStatus.Unproved, p.Status));
        Assert.Contains("owner", verdict.SharedSubmissionAcceptance, StringComparison.OrdinalIgnoreCase);
        cpu = CoveragePerfReportVectors.Cpu(cpu, 0, 1);
        var shared = CoveragePerfBudgets.Evaluate(cpu).SharedSubmission[0];
        Assert.Equal(9000, shared.Microseconds);
        Assert.Equal(900, shared.Percent);
        var gpu = CoveragePerfReportVectors.Gpu(CoveragePerfReportVectors.Create("G1"), 5, 9);
        Assert.Equal(-10, CoveragePerfBudgets.Evaluate(gpu).Gpu.Pairs[0].Percent!.Value, 8);
    }

    [Fact]
    public void C0ReportsObservedOneActorAndEmptySharedCostWithoutExtrapolation()
    {
        var report = CoveragePerfReportVectors.Cpu(CoveragePerfReportVectors.Create("C0"), 0, 8);
        report = CoveragePerfReportVectors.Cpu(report, 3, 11);
        var verdict = CoveragePerfBudgets.Evaluate(report);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, verdict.Cpu.Status);
        Assert.Single(verdict.SharedSubmission);
        Assert.Equal(2000, verdict.SharedSubmission[0].Microseconds);
        Assert.Equal(25, verdict.SharedSubmission[0].Percent);
        Assert.Equal(1000, verdict.OneActor!.Microseconds);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, verdict.OneActor.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveBaselineCannotBeADenominator(double milliseconds)
    {
        var report = CoveragePerfReportVectors.Create("G1");
        report = milliseconds < 0
            ? CoveragePerfReportVectors.ChangeSample(report, 4, s => s with { Gpu = null })
            : CoveragePerfReportVectors.Gpu(report, 4, milliseconds);
        var gate = CoveragePerfBudgets.Evaluate(report).Gpu;
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, gate.Status);
        Assert.NotEmpty(gate.Reasons);
        Assert.Null(gate.Pairs[0].Percent);
    }

    [Fact]
    public void StatisticsUseMedianNearestRankP95AndBalancedPosesWithoutTrimming()
    {
        var stats = CoveragePerfStatistics.Summarize(ImmutableArray.Create(1d, 2d, 3d, 100d));
        Assert.Equal(4, stats.Count);
        Assert.Equal(26.5, stats.Mean);
        Assert.Equal(2.5, stats.Median);
        Assert.Equal(100, stats.P95);
        Assert.Equal(1, stats.Minimum);
        Assert.Equal(100, stats.Maximum);
        var report = CoveragePerfReportVectors.Cpu(CoveragePerfReportVectors.Create(), 2, 11, 13);
        var summary = CoveragePerfStatistics.Summarize(report.Blocks[2], 1000000000);
        Assert.Equal(new[] { 64, 64 }, summary.PerPoseCpu.Select(p => p.Count));
        Assert.Equal(new[] { 11d, 13d }, summary.PerPoseCpu.Select(p => p.Mean));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OddOrUnbalancedMeasuredCountsAreRefused(bool unbalanced)
    {
        var report = CoveragePerfReportVectors.ChangeBlock(CoveragePerfReportVectors.Create(), 2, b => b with
        { Samples = unbalanced ? b.Samples.Select(s => s with { PoseId = 0 }).ToImmutableArray() : b.Samples.RemoveAt(0) });
        var gate = CoveragePerfBudgets.Evaluate(report).Cpu;
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, gate.Status);
        Assert.Contains(gate.Reasons, r => r.Contains(unbalanced ? "pose" : "even", StringComparison.OrdinalIgnoreCase));
    }
}
