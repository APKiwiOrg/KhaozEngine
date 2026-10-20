using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfDriverTests
{
    [Theory]
    [InlineData("setup", 300000, 1)]
    [InlineData("setup", 300001, 1)]
    [InlineData("frame", 300000, 1)]
    [InlineData("frame", 300001, 1)]
    [InlineData("batch", 300000, 1)]
    [InlineData("batch", 300001, 1)]
    [InlineData("cleanup", 300000, 1)]
    [InlineData("cleanup", 300001, 1)]
    [InlineData("block publication", 300000, 1)]
    [InlineData("block publication", 300001, 1)]
    [InlineData("unit publication", 300000, 4)]
    [InlineData("unit publication", 300001, 4)]
    [InlineData("later setup", 300000, 2)]
    [InlineData("later setup", 300001, 2)]
    public void OneDeadlineCoversEveryBlockSetupBatchCleanupAndFinalPublication(string boundary, long ticks, int factories)
    {
        var run = new CoveragePerfFakeRun();
        run.Setup = s =>
        {
            if (boundary == "setup") run.Time.Advance(ticks);
            if (boundary == "later setup") run.Time.Advance(s.Block.Index == 0 ? 100000 : ticks - 150000);
            s.OnFrame = (CoveragePerfFakeSession _, int i, ref CoveragePerfFrameSample sample) =>
            { if (boundary == "frame" && i == 0) run.Time.Advance(ticks); };
            s.OnBatch = _ => { if (boundary == "batch") run.Time.Advance(ticks); };
            s.OnDispose = _ => { if (boundary == "cleanup") run.Time.Advance(ticks); };
        };
        run.Publisher.OnBlock = _ =>
        {
            if (boundary == "block publication") run.Time.Advance(ticks);
            if (boundary == "later setup") run.Time.Advance(50000);
        };
        run.Publisher.OnUnit = _ => { if (boundary == "unit publication") run.Time.Advance(ticks); };

        var report = run.Run();

        Assert.Equal(CoveragePerfRunStatus.Failed, report.Status);
        Assert.Contains("deadline", report.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(factories, run.Sessions.Count);
        Assert.All(run.Sessions, s => Assert.True(s.Disposed));
        Assert.Single(run.Publisher.Units);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.CpuStatus);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.GpuStatus);
    }

    [Fact]
    public void AnExpiredUnitNeverStartsAnotherFactory()
    {
        var run = new CoveragePerfFakeRun();
        run.Setup = _ => run.Time.Advance(100000);
        run.Publisher.OnBlock = _ => run.Time.Advance(50000);

        var report = run.Run();

        Assert.Equal(2, run.Sessions.Count);
        Assert.Equal(2, report.Blocks.Length);
        Assert.Equal(2, run.Publisher.Blocks.Count);
        Assert.Equal(CoveragePerfRunStatus.Failed, report.Status);
        Assert.Contains("deadline", report.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("frame", 5)]
    [InlineData("completion", 3)]
    public void PartialSamplesSurviveFrameAndCompletionFailure(string failure, int captured)
    {
        var run = new CoveragePerfFakeRun();
        run.Setup = s =>
        {
            s.OnFrame = (CoveragePerfFakeSession _, int i, ref CoveragePerfFrameSample sample) =>
            {
                sample.CpuFramePreparationTicks = 42;
                if (failure == "frame" && i == 68)
                {
                    sample.Error = "partial frame evidence";
                    throw new InvalidOperationException("frame primary");
                }
            };
            s.OnBatch = session =>
            {
                if (failure == "completion" && session.Frames == 67)
                    throw new InvalidOperationException("completion primary");
            };
        };

        var report = run.Run();

        var block = Assert.Single(report.Blocks);
        Assert.Equal(CoveragePerfRunStatus.Failed, block.Status);
        Assert.Equal(64, block.Warmup.Length);
        Assert.Equal(captured, block.Samples.Length);
        Assert.Equal(42, block.Samples[^1].CpuFramePreparationTicks);
        if (failure == "frame") Assert.Equal("partial frame evidence", block.Samples[^1].Error);
        Assert.Contains(failure + " primary", report.Reason, StringComparison.Ordinal);
        Assert.Equal(block, Assert.Single(run.Publisher.Blocks));
        Assert.True(Assert.Single(run.Sessions).Disposed);
    }

    [Fact]
    public void FirstFailureSurvivesCleanupAndPublicationErrors()
    {
        var run = new CoveragePerfFakeRun();
        run.Setup = s =>
        {
            s.OnFrame = (CoveragePerfFakeSession _, int i, ref CoveragePerfFrameSample sample) =>
                throw new InvalidOperationException("frame primary");
            s.OnDispose = _ => throw new InvalidOperationException("cleanup secondary");
        };
        run.Publisher.OnBlock = _ => throw new InvalidOperationException("block publication secondary");
        run.Publisher.OnUnit = _ => throw new InvalidOperationException("unit publication secondary");

        var report = run.Run();

        Assert.Contains("frame primary", report.Reason, StringComparison.Ordinal);
        Assert.Contains(report.Errors, e => e.Contains("cleanup secondary", StringComparison.Ordinal));
        Assert.Contains(report.Errors, e => e.Contains("block publication secondary", StringComparison.Ordinal));
        Assert.Contains(report.Errors, e => e.Contains("unit publication secondary", StringComparison.Ordinal));
        Assert.Equal(4, report.Errors.Length);
        Assert.Contains("frame primary", Assert.Single(report.Blocks).Reason, StringComparison.Ordinal);
        Assert.Single(run.Publisher.Blocks);
        Assert.Single(run.Publisher.Units);
    }

    [Fact]
    public void BlockIsPublishedBeforeNextFactory()
    {
        var run = new CoveragePerfFakeRun();

        var report = run.Run();

        Assert.Equal(CoveragePerfRunStatus.Completed, report.Status);
        Assert.Equal(new[] { "factory-0", "dispose-0", "block-0", "factory-1", "dispose-1", "block-1",
            "factory-2", "dispose-2", "block-2", "factory-3", "dispose-3", "block-3", "unit" }, run.Events);
    }

    [Fact]
    public void FinalPublicationFailureRevokesUnitVerdicts()
    {
        var run = new CoveragePerfFakeRun();
        run.Publisher.OnUnit = r =>
        {
            Assert.Equal(CoveragePerfRunStatus.Completed, r.Status);
            throw new InvalidOperationException("unit publication failed");
        };

        var report = run.Run();

        Assert.Equal(CoveragePerfRunStatus.Failed, report.Status);
        Assert.Contains("unit publication failed", report.Reason, StringComparison.Ordinal);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.CpuStatus);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.GpuStatus);
        Assert.Equal(4, report.Blocks.Length);
        Assert.All(report.Blocks, b => Assert.Equal(CoveragePerfRunStatus.Completed, b.Status));
        Assert.Single(run.Publisher.Units);
    }

    [Theory]
    [InlineData("retired schema", "schema")]
    [InlineData("retired identity", "authorization.identity")]
    [InlineData("wrong leg", "identity.leg")]
    [InlineData("wrong SHA", "identity.workloadSha")]
    [InlineData("unapproved blocks", "condition")]
    public void RefusalNeverOpensAFactoryOrReadsAClock(string change, string reason)
    {
        var run = new CoveragePerfFakeRun();
        switch (change)
        {
            case "retired schema": run.ReplaceJson(CoveragePerfManifest.Schema, "khaozengine.coverage-cost-manifest/1"); break;
            case "retired identity": run.Identity = run.Identity with { AuthorizationIdentity = "coverage-pilot-20261018-a1-after-unit4-once" }; break;
            case "wrong leg": run.Identity = run.Identity with { Leg = "linux-vulkan" }; break;
            case "wrong SHA": run.Identity = run.Identity with { WorkloadSha = new string('4', 40) }; break;
            case "unapproved blocks": run.ReplaceJson("DirectOff", "SharedOn"); break;
        }

        var report = run.Run();

        Assert.Equal(CoveragePerfRunStatus.Refused, report.Status);
        Assert.Contains(reason, report.Reason, StringComparison.Ordinal);
        Assert.Empty(run.Sessions);
        Assert.Equal(0, run.Time.Reads);
        Assert.Empty(run.Publisher.Blocks);
        Assert.Empty(report.Blocks);
        Assert.Equal(report, Assert.Single(run.Publisher.Units));
    }

    [Theory]
    [InlineData("G1", 1, "route")]
    [InlineData("G2", 3, "capability")]
    [InlineData("G3", 4, "outcome")]
    [InlineData("G1", 0, "software")]
    [InlineData("G2", 2, "backend")]
    [InlineData("G3", 3, "unresolved")]
    [InlineData("G1", 1, "disjoint")]
    public void InvalidGpuEvidenceStopsBeforeTheNextStage(string preset, int invalidBlock, string evidence)
    {
        var run = new CoveragePerfFakeRun(preset);
        run.Setup = s =>
        {
            if (s.Block.Index != invalidBlock) return;
            s.Info = evidence switch
            {
                "route" => s.Info with { CoveredSurfaceRoute = "cpu fallback" },
                "capability" => s.Info with { TimestampSupported = false },
                "software" => s.Info with { SoftwareAdapter = true },
                "backend" => s.Info with { Backend = null },
                _ => s.Info,
            };
            s.OnFrame = (CoveragePerfFakeSession _, int i, ref CoveragePerfFrameSample sample) =>
            {
                if (evidence == "outcome") sample.DeviceComplete = false;
                if (evidence == "unresolved") sample.Gpu = null;
                if (evidence == "disjoint") sample.Gpu = sample.Gpu! with { Disjoint = true };
            };
        };

        var report = run.Run();

        Assert.NotEqual(CoveragePerfRunStatus.Completed, report.Status);
        Assert.False(string.IsNullOrWhiteSpace(report.Reason));
        Assert.Equal(invalidBlock + 1, run.Sessions.Count);
        Assert.Equal(invalidBlock + 1, report.Blocks.Length);
        Assert.Equal(report.Blocks.Length, run.Publisher.Blocks.Count);
        Assert.NotEqual(CoveragePerfRunStatus.Completed, report.Blocks[^1].Status);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.GpuStatus);
        Assert.Single(run.Publisher.Units);
        if (evidence is "route" or "capability" or "software" or "backend")
            Assert.Equal(0, run.Sessions[^1].Frames);
        else Assert.NotEmpty(report.Blocks[^1].Warmup);
    }

    [Theory]
    [InlineData("C0", 4, 384, 132)]
    [InlineData("C1", 6, 1152, 390)]
    [InlineData("G1", 8, 1152, 392)]
    [InlineData("G2", 8, 1152, 392)]
    [InlineData("G3", 8, 1152, 392)]
    public void DeclaredFramesAndBatchesRunOnceAndSyntheticResultsStayUnproved(string preset, int blocks, int frames, int batches)
    {
        var run = new CoveragePerfFakeRun(preset);

        var report = run.Run();

        Assert.Equal(CoveragePerfRunStatus.Completed, report.Status);
        Assert.Equal(blocks, run.Sessions.Count);
        Assert.Equal(frames, run.Sessions.Sum(s => s.Frames));
        Assert.Equal(batches, run.Sessions.Sum(s => s.Batches));
        Assert.All(report.Blocks, b =>
        {
            Assert.Equal(b.Block.WarmupFrames, b.Warmup.Length);
            Assert.Equal(b.Block.MeasuredFrames, b.Samples.Length);
            Assert.Equal(0, b.Warmup[0].FrameIndex);
            Assert.Equal(b.Block.WarmupFrames, b.Samples[0].FrameIndex);
            Assert.Equal(b.Block.WarmupFrames + b.Block.MeasuredFrames - 1, b.Samples[^1].FrameIndex);
            Assert.True(b.Session!.Synthetic);
        });
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.CpuStatus);
        Assert.Equal(CoveragePerfBudgetStatus.Unproved, report.GpuStatus);
        Assert.Contains("synthetic", report.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(9999, true)]
    [InlineData(10000, false)]
    [InlineData(10001, false)]
    public void FrameDeadlineUsesInjectedElapsedTicks(long ticks, bool completes)
    {
        var run = new CoveragePerfFakeRun();
        run.Setup = s => s.OnFrame = (CoveragePerfFakeSession _, int i, ref CoveragePerfFrameSample sample) =>
        { if (s.Block.Index == 0 && i == 0) run.Time.Advance(ticks); };

        var report = run.Run();

        Assert.Equal(completes ? CoveragePerfRunStatus.Completed : CoveragePerfRunStatus.Failed, report.Status);
        if (!completes)
        {
            Assert.Contains("frame deadline", report.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Single(run.Sessions);
            Assert.Single(report.Blocks[0].Warmup);
        }
    }

    [Fact]
    public void JustBelowTheUnitLimitDoesNotExpireOrResetAtTheNextBlock()
    {
        var run = new CoveragePerfFakeRun();
        run.Setup = s => { if (s.Block.Index == 0) run.Time.Advance(299999); };

        Assert.Equal(CoveragePerfRunStatus.Completed, run.Run().Status);
        Assert.Equal(4, run.Sessions.Count);
    }

    [Fact]
    public void ClockOriginsAndFrequencyDoNotOverflowDeadlineArithmetic()
    {
        var run = new CoveragePerfFakeRun();
        run.Time.Ticks = long.MaxValue - 1000;

        Assert.Equal(CoveragePerfRunStatus.Completed, run.Run(long.MaxValue).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidClockFrequencyRefusesWithoutStartingWork(long frequency)
    {
        var run = new CoveragePerfFakeRun();

        var report = run.Run(frequency);

        Assert.Equal(CoveragePerfRunStatus.Refused, report.Status);
        Assert.Empty(run.Sessions);
        Assert.Equal(0, run.Time.Reads);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("diagnostics")]
    [InlineData("cleanup")]
    [InlineData("block publication")]
    public void LifecycleFailuresPublishTerminalPartialResults(string failure)
    {
        var run = new CoveragePerfFakeRun();
        run.Setup = s =>
        {
            if (failure == "setup") throw new InvalidOperationException("setup failure");
            s.OnReadInfo = _ => { if (failure == "diagnostics") throw new InvalidOperationException("diagnostics failure"); };
            s.OnDispose = _ => { if (failure == "cleanup") throw new InvalidOperationException("cleanup failure"); };
        };
        run.Publisher.OnBlock = _ => { if (failure == "block publication") throw new InvalidOperationException("block publication failure"); };

        var report = run.Run();

        Assert.Equal(CoveragePerfRunStatus.Failed, report.Status);
        Assert.Contains(failure + " failure", report.Reason, StringComparison.Ordinal);
        Assert.Single(report.Blocks);
        Assert.Single(run.Sessions);
        Assert.Single(run.Publisher.Blocks);
        Assert.Single(run.Publisher.Units);
    }
}
