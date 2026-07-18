using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Localization.TestKit;
using KhaozEngine.ServerStatus;
using KhaozEngine.Tests.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.Game;

/// <summary>
/// Headless coverage of the boot pipeline: weighted-progress arithmetic, step ordering, the determinate/indeterminate
/// progress mapping, the failed / restarting / cancelled transitions, step-label surfacing, the server-status
/// min-version gate, and the empty-options default. No GPU and no real threads beyond the async machinery (steps are
/// driven with a controllable gate so completion is deterministic).
/// </summary>
public class BootPipelineTests
{
    static bool Close(float a, float b) => Math.Abs(a - b) < 1e-4f;

    static LocalizedText Named(string name) => LocalizedText.Raw(name);

    // A step whose completion the test controls, so progress can be observed mid-step. Records the reporter it was
    // handed and the token, and stays suspended until Complete is called.
    sealed class GateStep : IBootStep
    {
        readonly TaskCompletionSource<BootStepResult> _tcs = new();
        public IBootProgress? Progress;
        public GateStep(LocalizedText name, float weight) { Name = name; Weight = weight; }
        public LocalizedText Name { get; }
        public float Weight { get; }
        public Task<BootStepResult> RunAsync(IBootProgress progress, CancellationToken cancellationToken)
        {
            Progress = progress;
            return _tcs.Task;
        }
        public void Complete(BootStepResult result) => _tcs.SetResult(result);
    }

    // A step that suspends until its cancellation token fires, then throws (like a real awaited cancellable call).
    sealed class CancelStep : IBootStep
    {
        public LocalizedText Name => Named("cancel");
        public float Weight => 1f;
        public async Task<BootStepResult> RunAsync(IBootProgress progress, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<BootStepResult>();
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
                return await tcs.Task;
        }
    }

    static IBootStep Record(string name, IList<string> log, float weight = 1f)
        => BootStep.Create(Named(name), weight, (p, ct) => { log.Add(name); return Task.CompletedTask; });

    static IBootStep Throw(Exception ex)
        => BootStep.Create(Named("throw"), 1f, (p, ct) => throw ex);

    static IBootStep Restart()
        => BootStep.Create(Named("restart"), 1f, (p, ct) => Task.FromResult(BootStepResult.Restarting));

    [Fact]
    public void ProgressMath_Slices_PartitionTheBar_ByWeight()
    {
        var (starts, sizes) = BootProgressMath.Slices(new float[] { 1f, 3f });
        Assert.True(Close(sizes[0], 0.25f));
        Assert.True(Close(sizes[1], 0.75f));
        Assert.True(Close(starts[0], 0f));
        Assert.True(Close(starts[1], 0.25f));
        Assert.True(Close(sizes[0] + sizes[1], 1f));
    }

    [Fact]
    public void ProgressMath_Overall_MapsStepFractionOntoSlice()
    {
        var (starts, sizes) = BootProgressMath.Slices(new float[] { 1f, 3f });
        Assert.True(Close(BootProgressMath.Overall(0, 0.5f, starts, sizes), 0.125f));
        Assert.True(Close(BootProgressMath.Overall(1, 0.5f, starts, sizes), 0.625f));
        Assert.True(Close(BootProgressMath.Overall(1, 1f, starts, sizes), 1f));
    }

    [Fact]
    public void ProgressMath_AllZeroWeights_FallBackToEqualSlices()
    {
        var (starts, sizes) = BootProgressMath.Slices(new float[] { 0f, 0f });
        Assert.True(Close(sizes[0], 0.5f));
        Assert.True(Close(sizes[1], 0.5f));
        Assert.True(Close(starts[1], 0.5f));
    }

    [Fact]
    public void Steps_RunInRegistrationOrder_AndCompleteAtFull()
    {
        var log = new List<string>();
        var pipeline = new BootPipeline(new[] { Record("a", log), Record("b", log), Record("c", log) });

        pipeline.Start(); // synchronous steps complete during the kick-off

        Assert.Equal(BootState.Completed, pipeline.State);
        Assert.Equal(new[] { "a", "b", "c" }, log);
        Assert.True(Close(pipeline.Snapshot().Fraction, 1f));
    }

    [Fact]
    public void CurrentStepLabel_IsSurfaced_WhileRunning()
    {
        var gate = new GateStep(Named("contacting"), 1f);
        var pipeline = new BootPipeline(new IBootStep[] { gate });

        pipeline.Start();

        Assert.Equal(BootState.Running, pipeline.State);
        Assert.Equal("contacting", pipeline.Snapshot().StepLabel.Resolve());
    }

    [Fact]
    public void MidStepProgress_MapsOntoWeightedOverallBar()
    {
        var g0 = new GateStep(Named("a"), 1f);
        var g1 = new GateStep(Named("b"), 3f);
        var pipeline = new BootPipeline(new IBootStep[] { g0, g1 });

        pipeline.Start();
        g0.Progress!.Report(0.5f);
        Assert.True(Close(pipeline.Snapshot().Fraction, 0.125f)); // half of the first 25% slice

        g0.Complete(BootStepResult.Proceed);
        pipeline.Pump();
        Assert.True(Close(pipeline.Snapshot().Fraction, 0.25f)); // first slice fully done
        Assert.Equal("b", pipeline.Snapshot().StepLabel.Resolve());

        g1.Progress!.Report(0.5f);
        Assert.True(Close(pipeline.Snapshot().Fraction, 0.625f)); // 0.25 + half of the 75% slice

        g1.Complete(BootStepResult.Proceed);
        pipeline.Pump();
        Assert.Equal(BootState.Completed, pipeline.State);
        Assert.True(Close(pipeline.Snapshot().Fraction, 1f));
    }

    [Fact]
    public void Indeterminate_MarksTheSnapshot_WithoutAdvancingFraction()
    {
        var g0 = new GateStep(Named("a"), 1f);
        var g1 = new GateStep(Named("b"), 1f);
        var pipeline = new BootPipeline(new IBootStep[] { g0, g1 });

        pipeline.Start();
        g0.Complete(BootStepResult.Proceed);
        pipeline.Pump(); // now on the second slice, starting at 0.5

        g1.Progress!.ReportIndeterminate();
        BootView view = pipeline.Snapshot();
        Assert.True(view.Indeterminate);
        Assert.True(Close(view.Fraction, 0.5f)); // holds at the slice start
    }

    [Fact]
    public void BootStepException_FailsWithItsLocalizedMessage()
    {
        var pipeline = new BootPipeline(new[] { Throw(new BootStepException(Named("boom"))) });

        pipeline.Start();

        Assert.Equal(BootState.Failed, pipeline.State);
        Assert.Equal("boom", pipeline.Snapshot().FailureMessage!.Value.Resolve());
    }

    [Fact]
    public void UnexpectedException_FailsWithTheGenericMessage()
    {
        var pipeline = new BootPipeline(new[] { Throw(new InvalidOperationException("internal")) });

        pipeline.Start();

        Assert.Equal(BootState.Failed, pipeline.State);
        Assert.Equal(BootStrings.ErrorGeneric, pipeline.Snapshot().FailureMessage!.Value.Id);
    }

    [Fact]
    public void RestartingStep_StopsThePipeline_WithoutRunningLaterSteps()
    {
        var log = new List<string>();
        var pipeline = new BootPipeline(new[] { Restart(), Record("after", log) });

        pipeline.Start();

        Assert.Equal(BootState.Restarting, pipeline.State);
        Assert.DoesNotContain("after", log);
    }

    [Fact]
    public void Cancel_SettlesIntoCancelled()
    {
        var pipeline = new BootPipeline(new IBootStep[] { new CancelStep() });

        pipeline.Start();
        Assert.Equal(BootState.Running, pipeline.State);

        pipeline.Cancel();
        pipeline.Pump();

        Assert.Equal(BootState.Cancelled, pipeline.State);
    }

    [Fact]
    public void Retry_ReRunsAfterFailure()
    {
        int runs = 0;
        var step = BootStep.Create(Named("flaky"), 1f, (p, ct) =>
        {
            runs++;
            if (runs == 1) throw new BootStepException(Named("first-fail"));
            return Task.CompletedTask;
        });
        var pipeline = new BootPipeline(new[] { step });

        pipeline.Start();
        Assert.Equal(BootState.Failed, pipeline.State);

        pipeline.Retry();
        Assert.Equal(BootState.Completed, pipeline.State);
        Assert.Equal(2, runs);
    }

    [Fact]
    public void EmptyOptions_YieldNoSteps_AndCompleteImmediately()
    {
        var options = new BootOptions();
        var steps = options.BuildSteps();
        Assert.Empty(steps);

        var pipeline = new BootPipeline(steps);
        pipeline.Start();

        Assert.Equal(BootState.Completed, pipeline.State);
        Assert.True(Close(pipeline.Snapshot().Fraction, 1f));
    }

    [Fact]
    public void EnglishDefaults_CoverEveryBootStringId()
    {
        foreach (string key in LocalizationCoverage.Keys(typeof(BootStrings)))
            Assert.True(BootStrings.EnglishDefaults.TryGet(key, out _), $"missing English default for '{key}'");
    }

    [Fact]
    public void ServerStatus_MinVersionGate_FailsWithUpdateRequired()
    {
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var source = new FakeServerStatusSource();
        source.Enqueue(new ServerStatusReport { Health = ServerHealth.Healthy, MinClientVersion = "2.0.0", LastHeartbeatUtc = now });
        var client = new ServerStatusClient(source, clock: () => now);
        var step = new ServerStatusBootStep(client, localClientVersion: "1.0.0", clock: () => now);
        var pipeline = new BootPipeline(new IBootStep[] { step });

        pipeline.Start();

        Assert.Equal(BootState.Failed, pipeline.State);
        Assert.Equal(BootStrings.ErrorUpdateRequired, pipeline.Snapshot().FailureMessage!.Value.Id);
    }

    [Fact]
    public void ServerStatus_Unreachable_ProceedsGracefully()
    {
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var source = new FakeServerStatusSource();
        source.Enqueue(null); // transport miss -> StatusUnknown -> non-blocking
        var client = new ServerStatusClient(source, clock: () => now);
        var step = new ServerStatusBootStep(client, localClientVersion: "1.0.0", clock: () => now);
        var pipeline = new BootPipeline(new IBootStep[] { step });

        pipeline.Start();

        Assert.Equal(BootState.Completed, pipeline.State);
    }
}
