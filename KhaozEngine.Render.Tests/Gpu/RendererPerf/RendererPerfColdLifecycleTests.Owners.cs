using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfColdLifecycleTests
{
    [Fact]
    public void Both_contexts_are_gated_and_disposed_before_publication()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        object gate = typeof(GpuDeviceContext).GetField("_lifecycleGate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        rig.Boundary = stage =>
        {
            if (stage.StartsWith("create.", StringComparison.Ordinal) || stage.EndsWith("context.dispose", StringComparison.Ordinal)
                || stage.EndsWith("context.mark", StringComparison.Ordinal)) Assert.True(Monitor.IsEntered(gate));
        };
        var outcome = rig.Run();
        Assert.Empty(outcome.Failures);
        Assert.NotNull(outcome.Inventory);
        Assert.Equal(1260, rig.Frames);
        Assert.Equal(2, outcome.Inventory.Recipes.Length);
        Assert.Equal(2, rig.Diagnostic.CompilerReads);
        Assert.Equal(0, rig.Diagnostic.Submissions); Assert.Equal(0, rig.Diagnostic.Waits);
        Assert.Equal(2, rig.Publication.Committed);
        Assert.Equal(RendererPerfCollectionState.Complete, outcome.Report!.Compiler.State);
        Assert.False(outcome.Report.Assessment.ValidForPromotion);
        Assert.Equal(new[] { false, true }, rig.BackendProvider.Settings.Select(s => s.Diagnostics.CompilerMetadata));
        Assert.All(rig.BackendProvider.Settings, s => Assert.False(s.Diagnostics.RuntimeInvocations));
        Assert.Equal(rig.Production.Configuration.AdapterKey, rig.BackendProvider.Settings[1].AdapterKey);
        Assert.True(rig.Trace.IndexOf("production.context.dispose") < rig.Trace.IndexOf("create.diagnostic"));
        Assert.True(rig.Trace.IndexOf("diagnostic.context.dispose") < rig.Trace.IndexOf("publish"));
        Assert.Equal(1, rig.Trace.Count(s => s == "identify"));
        _output.WriteLine("frames={0} recipes={1} occurrences={2} reads={3} reservations={4} commits={5} contexts={6} calibrationOwners=1",
            rig.Frames, outcome.Inventory.Recipes.Length, outcome.Inventory.Occurrences.Length, rig.Diagnostic.CompilerReads,
            rig.Publication.Reserved, rig.Publication.Committed, rig.Attempts.Count);
    }

    [Fact]
    public void Actual_frame_and_completion_observation_cleanup_failures_reach_outcome_by_reference()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var frame = new AggregateException("frame", new InvalidOperationException("inner"));
        var drain = new InvalidOperationException("drain");
        var observation = new InvalidOperationException("observation");
        var mark = new InvalidOperationException("mark"); var native = new InvalidOperationException("native");
        bool failed = false;
        rig.Boundary = stage =>
        {
            if (stage == "frame") { failed = true; throw frame; }
            if (failed && stage == "production.drain") throw drain;
            if (failed && stage == "production.memory") throw observation;
            if (stage == "production.context.mark") throw mark;
            if (stage == "production.context.dispose") throw native;
        };
        var outcome = rig.Run();
        Exception[] expected = [frame, drain, observation, drain, observation, mark, native];
        Assert.Equal(expected.Length, outcome.Failures.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Same(expected[i], outcome.Failures[i].Error);
        Assert.Equal("renderer.frame", outcome.Failures[0].Stage);
        Assert.Single(outcome.Report!.Conditioning);
        Assert.Equal(frame.Message, outcome.Report.Publication.OriginalFailure);
        Assert.Equal(0, rig.Diagnostic.CompilerReads);
        Assert.Single(rig.Attempts);
        Assert.DoesNotContain("production.dispose.shader", rig.Trace);
        _output.WriteLine("actual reference order={0}", string.Join(",", outcome.Failures.Select(f => f.Stage)));
        Assert.Single(outcome.Report.Memory.Observations, o => o.Stage == "unit-final");
        var exhausted = RendererPerfFailureText.Project(outcome.Report, outcome.Failures, 0);
        Assert.False(exhausted.Publication.FailureText.Complete);
        Assert.Equal(expected.Length, exhausted.Publication.FailureText.OmittedFailures);
        Assert.Equal(RendererPerfFailureText.OmittedOriginal, exhausted.Publication.OriginalFailure);
        Assert.Same(frame, outcome.Failures[0].Error);
    }

    [Fact]
    public void Actual_collector_read_graph_and_context_failures_preserve_partial_report()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var read = new InvalidOperationException("native read");
        var pipeline = new InvalidOperationException("pipeline"); var layout = new InvalidOperationException("layout");
        var shader = new InvalidOperationException("shader"); var mark = new InvalidOperationException("mark");
        var context = new InvalidOperationException("context");
        ((RendererPerfColdLifecycleFixture.ExecutableApi)rig.Diagnostic.CompilerApi!).Failure = read;
        rig.Boundary = stage =>
        {
            if (stage == "diagnostic.dispose.pipeline") throw pipeline;
            if (stage == "diagnostic.dispose.layout") throw layout;
            if (stage == "diagnostic.dispose.shader") throw shader;
            if (stage == "diagnostic.context.mark") throw mark;
            if (stage == "diagnostic.context.dispose") throw context;
        };
        var outcome = rig.Run();
        Exception[] expected = [read, pipeline, layout, shader, mark, context];
        Assert.Equal(expected.Length, outcome.Failures.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Same(expected[i], outcome.Failures[i].Error);
        Assert.Single(outcome.Report!.Pipelines);
        Assert.NotNull(outcome.Report.Compiler.Results[0].Read!.Counts);
        Assert.Equal(RendererPerfCollectionState.Incomplete, outcome.Report.Compiler.Results[1].State);
        Assert.Equal(1, rig.Diagnostic.CompilerReads);
        Assert.Equal(1, rig.Publication.Committed);
        Assert.Equal(1260, rig.Frames);
    }

    [Fact]
    public void Control_constructor_and_adoption_cleanup_reach_outcome_by_reference()
    {
        using (var rig = new RendererPerfColdLifecycleFixture())
        {
            var construct = new InvalidOperationException("control construction"); var cleanup = new InvalidOperationException("texture cleanup");
            rig.Production.FactoryFailure = construct;
            rig.Boundary = stage => { if (stage == "production.dispose.texture") throw cleanup; };
            rig.WorkFactory = (device, _, _) => new RendererPerfControlWorkload(device, rig.Template.Manifest.Controls.Single(c => c.Id == 4), RendererPerfControls.Definition(4));
            var outcome = rig.Run();
            Assert.Equal(2, outcome.Failures.Length);
            Assert.Same(construct, outcome.Failures[0].Error); Assert.Same(cleanup, outcome.Failures[1].Error);
            Assert.Equal("renderer.control.construct", outcome.Failures[0].Stage);
            Assert.Equal(0, rig.Frames); Assert.Single(rig.Attempts);
        }
        using (var rig = new RendererPerfColdLifecycleFixture())
        {
            var rejected = new InvalidOperationException("rejected dispose");
            rig.Production.ActualBackend = GpuBackendKind.Direct3D11Native;
            rig.Boundary = stage => { if (stage == "production.context.dispose") throw rejected; };
            var outcome = rig.Run();
            Assert.Null(outcome.Report); Assert.NotNull(outcome.EarlyFailure);
            Assert.Equal(2, outcome.Failures.Length);
            Assert.IsType<ArgumentException>(outcome.Failures[0].Error);
            Assert.Same(rig.Attempts[0].Failures[0].Error, outcome.Failures[0].Error);
            Assert.Same(rejected, outcome.Failures[1].Error);
            Assert.Equal(new[] { "gpu.context.adopt", "gpu.context.reject.dispose" }, outcome.Failures.Select(f => f.Stage));
            Assert.Same(rig.Publication.Early, outcome.EarlyFailure);
            Assert.False(outcome.EarlyFailure.KnownFacts.ContainsKey("adapterKey"));
            Assert.Equal(0, rig.Publication.Published);
        }
    }

    [Fact]
    public void Ordinary_creation_keeps_the_original_primary_exception_and_stack()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var sentinel = new InvalidOperationException("provider"); rig.BackendProvider.Failure = sentinel;
        var actual = Assert.Throws<InvalidOperationException>(() => GpuDeviceContext.CreateHeadless(GpuBackendKind.VulkanNative));
        Assert.Same(sentinel, actual);
        Assert.Contains("CreateHeadless", actual.StackTrace!);
        var outcome = rig.Run();
        Assert.Same(sentinel, Assert.Single(outcome.Failures).Error);
        Assert.Null(outcome.Report); Assert.NotNull(outcome.EarlyFailure);
        Assert.Equal(1, rig.Publication.EarlyWrites);
    }
    [Fact]
    public void Preidentity_failure_keeps_actual_known_facts_and_failed_early_write_evidence()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var identify = new AggregateException("identity", new InvalidOperationException("original inner"));
        var write = new InvalidOperationException("early write");
        rig.Identify = _ => throw identify;
        rig.Boundary = stage => { if (stage == "early") throw write; };
        var outcome = rig.Run();
        Assert.Null(outcome.Report); Assert.Same(rig.Publication.Early, outcome.EarlyFailure);
        Assert.Equal(2, outcome.Failures.Length);
        Assert.Same(identify, outcome.Failures[0].Error); Assert.Same(write, outcome.Failures[1].Error);
        Assert.Equal("renderer.publication.early.write", outcome.Failures[1].Stage);
        Assert.Equal(1, rig.Publication.EarlyWrites); Assert.Equal(0, rig.Publication.Published);
        Assert.Equal(rig.Production.Configuration.AdapterKey, outcome.EarlyFailure!.KnownFacts["adapterKey"]);
        Assert.False(outcome.EarlyFailure.KnownFacts.ContainsKey("timestampPeriodNs"));
        Assert.False(outcome.EarlyFailure.KnownFacts.ContainsKey("vendorId"));
        Assert.True(rig.Trace.IndexOf("production.context.dispose") < rig.Trace.IndexOf("early"));
    }

}
