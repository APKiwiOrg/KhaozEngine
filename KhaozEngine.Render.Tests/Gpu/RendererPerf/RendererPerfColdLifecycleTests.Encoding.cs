using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfColdLifecycleTests
{
    [Fact]
    public void Actual_commit_failure_keeps_the_completed_native_report_before_graph_cleanup()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var commit = new InvalidOperationException("commit bytes");
        var cleanup = new InvalidOperationException("pipeline cleanup");
        rig.Boundary = stage =>
        {
            if (stage == "commit") throw commit;
            if (stage == "diagnostic.dispose.pipeline") throw cleanup;
        };
        var outcome = rig.Run();
        Assert.Equal(2, outcome.Failures.Length);
        Assert.Same(commit, outcome.Failures[0].Error); Assert.Same(cleanup, outcome.Failures[1].Error);
        Assert.Same(rig.Diagnostic.LastRead!.Report, Assert.Single(outcome.Report!.Pipelines));
        Assert.True(outcome.Report.Compiler.Results[0].Read!.Complete);
        Assert.Equal(1, rig.Diagnostic.CompilerReads); Assert.Equal(0, rig.Publication.Committed);
        Assert.Equal(1, rig.Publication.Terminated);
        Assert.Equal(RendererPerfCollectionState.Incomplete, outcome.Report.Compiler.Results[1].State);
        Assert.False(outcome.Report.Publication.FinalUnitPublished);
    }

    [Fact]
    public void Actual_bounded_encoder_failure_keeps_reader_evidence_and_original_error_instances()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        // A hostile in-memory witness. Real native shader producers already enforce their 16384-byte flags bound.
        rig.Diagnostic.Flags = new string('\\', 1048576);
        var outcome = rig.Run();
        var read = rig.Diagnostic.LastRead!;
        Assert.Equal(2, outcome.Failures.Length);
        Assert.Same(read.Failures[0].Error, outcome.Failures[0].Error);
        Assert.Contains("above reservation 1048576", outcome.Failures[1].Error.Message, StringComparison.Ordinal);
        Assert.Same(read.Report, Assert.Single(outcome.Report!.Pipelines));
        Assert.Same(read.Counts, outcome.Report.Compiler.Results[0].Read!.Counts);
        Assert.Equal(1, rig.Diagnostic.CompilerReads); Assert.Equal(0, rig.Publication.Committed);
        Assert.Equal(1, rig.Publication.Terminated);
        Assert.False(outcome.Report.Assessment.ValidForPromotion);
        _output.WriteLine("encoding failure events={0} retainedReports={1} reads={2} terminalReservations={3}",
            outcome.Failures.Length, outcome.Report.Pipelines.Length, rig.Diagnostic.CompilerReads, rig.Publication.Terminated);
    }
}
