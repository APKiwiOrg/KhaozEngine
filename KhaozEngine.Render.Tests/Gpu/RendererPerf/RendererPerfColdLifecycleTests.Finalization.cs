using System;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfColdLifecycleTests
{
    [Fact]
    public void Frame_finalization_keeps_original_error_and_harvestable_raw_sample()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var frame = new InvalidOperationException("frame"); var finalization = new InvalidOperationException("finalization");
        bool failed = false, finalized = false;
        rig.Boundary = stage =>
        {
            if (stage == "frame") { failed = true; throw frame; }
            if (failed && !finalized && stage == "production.diagnostics") { finalized = true; throw finalization; }
        };
        var outcome = rig.Run();
        Assert.Equal(2, outcome.Failures.Length);
        Assert.Same(frame, outcome.Failures[0].Error); Assert.Same(finalization, outcome.Failures[1].Error);
        Assert.NotEmpty(Assert.Single(outcome.Report!.Conditioning).Metrics);
        Assert.Single(rig.Attempts);
    }

    [Fact]
    public void Multiple_actual_pool_poll_failures_are_not_lost_and_keep_other_raw_frames()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var first = new InvalidOperationException("first poll"); var second = new InvalidOperationException("second poll");
        rig.Boundary = stage =>
        {
            if (stage != "frame") return;
            rig.Production.Pools[0].ResolveFailure = first;
            rig.Production.Pools[1].ResolveFailure = second;
        };
        var outcome = rig.Run();
        Assert.Equal(2, outcome.Failures.Length);
        Assert.Same(first, outcome.Failures[0].Error); Assert.Same(second, outcome.Failures[1].Error);
        Assert.Equal(3, outcome.Report!.Conditioning.Length);
        Assert.Equal(RendererPerfMetricStatus.Available, outcome.Report.Conditioning[2].GpuFrame.Status);
        Assert.Equal(3, rig.Frames);
        Assert.Single(rig.Attempts);
    }
    [Fact]
    public void Recorder_finalization_observation_failure_closes_and_keeps_the_recorded_frame()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var frame = new InvalidOperationException("frame"); var counters = new InvalidOperationException("counters");
        bool failed = false;
        rig.Boundary = stage =>
        {
            if (stage == "frame") { failed = true; throw frame; }
            if (failed && stage == "production.counters") throw counters;
        };
        var outcome = rig.Run();
        Assert.Equal(2, outcome.Failures.Length);
        Assert.Same(frame, outcome.Failures[0].Error); Assert.Same(counters, outcome.Failures[1].Error);
        Assert.NotEmpty(Assert.Single(outcome.Report!.Conditioning).Metrics);
        Assert.Single(rig.Attempts);
    }

    [Fact]
    public void Actual_artifact_mismatch_stops_before_the_next_recipe()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        rig.Diagnostic.ArtifactSalt = "changed compiled artifact ";
        var outcome = rig.Run();
        Assert.NotEmpty(outcome.Failures);
        Assert.Equal(1, rig.Diagnostic.CompilerReads);
        Assert.Single(outcome.Report!.Pipelines);
        Assert.Equal(RendererPerfCollectionState.Invalid, outcome.Report.Compiler.Results[0].State);
        Assert.Equal(RendererPerfCollectionState.Incomplete, outcome.Report.Compiler.Results[1].State);
    }

    [Fact]
    public void Exhausted_serialized_error_space_preserves_every_actual_error_and_repeated_occurrence()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var original = new InvalidOperationException(new string('\\', 9 * 1024 * 1024));
        var repeated = new InvalidOperationException("independent cleanup occurrence");
        rig.Boundary = stage =>
        {
            if (stage == "frame") throw original;
            if (stage is "production.context.mark" or "production.context.dispose") throw repeated;
        };
        var outcome = rig.Run();
        Assert.Equal(3, outcome.Failures.Length);
        Assert.Same(original, outcome.Failures[0].Error);
        Assert.Same(repeated, outcome.Failures[1].Error); Assert.Same(repeated, outcome.Failures[2].Error);
        var text = outcome.Report!.Publication.FailureText;
        Assert.False(text.Complete); Assert.Equal(3, text.OmittedFailures);
        long required = JsonSerializer.SerializeToUtf8Bytes(original.Message, RendererPerfManifest.JsonOptions).LongLength
            + JsonSerializer.SerializeToUtf8Bytes(new[] { repeated.Message, repeated.Message }, RendererPerfManifest.JsonOptions).LongLength;
        Assert.Equal(required, text.RequiredUtf8Bytes);
        _output.WriteLine("escaped failure bytes={0} omitted={1} actualEvents={2} repeatedReference=true", required, text.OmittedFailures, outcome.Failures.Length);
        Assert.Equal(RendererPerfFailureText.OmittedOriginal, outcome.Report.Publication.OriginalFailure);
        Assert.False(outcome.Report.Assessment.ValidForPromotion);
        Assert.Single(rig.Attempts);
    }

    [Fact]
    public void Deadline_after_actual_native_count_stops_fill_and_preserves_the_actual_partial_result()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var api = (RendererPerfColdLifecycleFixture.ExecutableApi)rig.Diagnostic.CompilerApi!;
        api.AfterPropertyCount = () => rig.Now = 600100;
        var outcome = rig.Run();
        Assert.Equal(1, api.Calls); Assert.Equal(1, rig.Diagnostic.CompilerReads);
        var read = rig.Diagnostic.LastRead!;
        Assert.Same(read.Report, Assert.Single(outcome.Report!.Pipelines));
        Assert.Same(read.Failures[0].Error, outcome.Failures[0].Error);
        Assert.IsType<TimeoutException>(outcome.Failures[0].Error);
        Assert.Equal(RendererPerfCollectionState.DeadlineExceeded, outcome.Report.Compiler.State);
        Assert.Null(outcome.Report.Compiler.Results[1].Read);
        Assert.Equal(100, outcome.Report.Startup[0].Wall.StartTicks);
    }

    [Fact]
    public void Genuine_optional_absence_completes_collection_without_any_native_queries()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        rig.Production.Configuration = rig.Production.Configuration with { PipelineStatisticsSupported = false };
        rig.Diagnostic.Configuration = rig.Production.Configuration with { Requested = new(true, false) };
        var outcome = rig.Run();
        Assert.Empty(outcome.Failures);
        Assert.Equal(RendererPerfCollectionState.Complete, outcome.Report!.Compiler.State);
        Assert.All(outcome.Report.Pipelines, p => Assert.Equal(KhaozEngine.Gpu.GpuDiagnosticStatus.Unsupported, p.Status));
        Assert.All(outcome.Report.Compiler.Results, r => Assert.False(r.Read!.Counts!.EnumerationRequired));
        Assert.Equal(0, ((RendererPerfColdLifecycleFixture.ExecutableApi)rig.Diagnostic.CompilerApi!).Calls);
        Assert.Equal(2, rig.Publication.Committed);
        Assert.False(outcome.Report.Assessment.ValidForPromotion);
    }

}
