using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Early_failure_write_keeps_evidence_and_actual_error(bool exhaustText)
    {
        using var fixture = new RendererPerfColdLifecycleFixture();
        var original = new AggregateException(exhaustText ? new string('\u0001', 3000000) : "actual identify", new Exception("original leaf"));
        var cleanup = new InvalidOperationException("actual production dispose");
        var write = new IOException("actual early write"); var recovery = new IOException("actual early recovery");
        fixture.Identify = _ => throw original;
        fixture.Boundary = stage => { if (stage == "production.context.dispose") throw cleanup; };
        int writes = 0;
        var writer = new RendererPerfPublicationWriter((_, _) => throw new InvalidOperationException("Normal publication is forbidden before identity."),
            _ => throw new InvalidOperationException("No early stamp."), writeFailure: (_, _) => throw (++writes == 1 ? write : recovery));
        var observed = new ObservePublication(writer);
        var outcome = fixture.Run(observed);
        Assert.Null(outcome.Report);
        Assert.Same(observed.Early, outcome.EarlyFailure);
        Assert.Equal(4, outcome.Failures.Length);
        Assert.Same(original, outcome.Failures[0].Error); Assert.Same(cleanup, outcome.Failures[1].Error);
        Assert.Same(write, outcome.Failures[2].Error); Assert.Same(recovery, outcome.Failures[3].Error);
        Assert.Equal(new[] { "renderer.production", "gpu.context.dispose.native", "renderer.publication.early.write", "renderer.publication.early.recovery" },
            outcome.Failures.Select(f => f.Stage));
        Assert.Equal(!exhaustText, outcome.EarlyFailure!.FailureText.Complete);
        if (exhaustText) Assert.Equal(RendererPerfFailureText.OmittedOriginal, outcome.EarlyFailure.OriginalFailure);
        Assert.False(outcome.EarlyFailure.KnownFacts.ContainsKey("vendorId"));
        _output.WriteLine("early original aggregate preserved=true events=4 sameEvidence=true textComplete={0}", outcome.EarlyFailure.FailureText.Complete);
    }
    sealed class ObservePublication(IRendererPerfUnitPublication actual, Action<RendererPerfUnitBudget>? observeBudget = null) : IRendererPerfUnitPublication
    {
        internal RendererPerfUnitFailure? Early;
        internal RendererPerfPublicationResult? Final;
        internal RendererPerfReport? Prepared;
        public RendererPerfCollectionPreparation PrepareCollection(RendererPerfReport report, RendererPerfCompilerInventory inventory, RendererPerfUnitBudget budget)
        { Prepared = report; observeBudget?.Invoke(budget); return actual.PrepareCollection(report, inventory, budget); }
        public RendererPerfReadReservation? ReserveReport(GpuBackendKind backend) => actual.ReserveReport(backend);
        public void CommitReportBytes(RendererPerfReadReservation reservation, int bytes) => actual.CommitReportBytes(reservation, bytes);
        public RendererPerfPublicationResult Publish(RendererPerfReport report, RendererPerfCompilerInventory inventory, RendererPerfUnitBudget budget)
            => Final = actual.Publish(report, inventory, budget);
        public RendererPerfFailurePublicationResult WriteFailure(RendererPerfUnitFailure failure, RendererPerfUnitBudget budget)
        { Early = failure; return actual.WriteFailure(failure, budget); }
    }
}
