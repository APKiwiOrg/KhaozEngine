using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.BugReports.Client;

public sealed partial class BugReportServiceTests
{
    [Fact]
    public async Task Dispose_with_a_pending_screenshot_keeps_the_report()
    {
        using var logs = CaptureLogs();
        await using var pending = new PendingScreenshotScenario(this);
        await pending.StartAsync();

        pending.DisposeService();

        pending.AssertDisposalCompleted();
        pending.AssertRetainedWithoutScreenshot();
        await pending.AwaitSendAsync();
        Assert.True(pending.Sink.Token.IsCancellationRequested, pending.State);
        pending.AssertRetainedWithoutScreenshot();
    }

    [Fact]
    public async Task Shutdown_with_a_held_screenshot_reaches_the_queue_before_network_cancellation()
    {
        using var logs = CaptureLogs();
        await using var pending = new PendingScreenshotScenario(this);
        await pending.StartAsync();

        pending.Service.Flow.SignalShutdown();
        await pending.AwaitFinishingAsync();
        await pending.AwaitAsync(pending.Sink.Entered, "sink entry");
        pending.AssertBuildCompleted(BugReportService.ShutdownBuildWait);

        pending.AssertRetainedWithoutScreenshot();
        Assert.True(pending.Sink.Token.CanBeCanceled, pending.State);
        Assert.False(pending.Sink.Token.IsCancellationRequested, pending.State);
        pending.AssertSendPending();

        pending.DisposeService();
        pending.AssertDisposalCompleted();
        await pending.AwaitSendAsync();
        Assert.True(pending.Sink.Token.IsCancellationRequested, pending.State);
        pending.AssertRetainedWithoutScreenshot();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_screenshot_cleanup_joins_work_after_an_assertion_failure(bool disposalThrows)
    {
        using var logs = CaptureLogs();
        var pending = new PendingScreenshotScenario(this);
        var heldWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?>? exit = null;
        try
        {
            await pending.StartAsync();
            _work.Add(heldWork.Task);
            var disposalFailure = new InvalidOperationException("window gone");
            if (disposalThrows) _host.RemoveFailure = disposalFailure;

            async Task<Exception?> FailWhileOwningScreenshot()
            {
                try
                {
                    await using (pending)
                        Assert.True(pending.Shot.Task.IsCompleted, "Intentional assertion failure while the screenshot is held.");
                }
                catch (Exception failure)
                {
                    return failure;
                }
                return null;
            }

            exit = FailWhileOwningScreenshot();
            Assert.True(pending.Shot.Task.IsFaulted, pending.State);
            Assert.False(exit.IsCompleted, "Cleanup returned before its held tracked work completed. " + pending.State);
            heldWork.TrySetResult();
            Exception? failure = await exit.WaitAsync(TimeSpan.FromSeconds(30));
            if (disposalThrows) Assert.Same(disposalFailure, failure);
            else Assert.IsType<TrueException>(failure);
            Assert.All(_work, work => Assert.True(work.IsCompletedSuccessfully, pending.State));
            Assert.Equal([pending.Service.Overlay], _removed);
            if (!disposalThrows) Assert.Empty(_host.Taps);
        }
        finally
        {
            // Independent safety ownership also drains the deliberately unsafe RED version of the scenario.
            heldWork.TrySetResult();
            pending.Shot.TrySetException(new ObjectDisposedException("recorder"));
            _host.RemoveFailure = null;
            try
            {
                pending.Service.Dispose();
            }
            finally
            {
                try
                {
                    await Task.WhenAll(_work).WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    if (exit is not null) await exit.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        }
    }

    /// <summary>Owns the fake screenshot separately from the real recorder, including assertion-failure exits.</summary>
    sealed class PendingScreenshotScenario : IAsyncDisposable
    {
        const string BuildTimeoutWarning = "A bug report was still being built at shutdown. It reaches the offline queue only if the process runs on.";
        readonly BugReportServiceTests _owner;
        readonly TaskCompletionSource _timersReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _finishing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly long _timestamp;
        Task? _thumbnail;
        Task? _send;
        TimeSpan? _disposeElapsed;
        bool? _buildsCompleted;

        internal readonly TaskCompletionSource<ReportImage> Shot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly BlockingSink Sink = new();
        internal readonly BugReportOfflineQueue Queue;
        internal readonly BugReportService Service;
        internal readonly string PendingDirectory;

        internal PendingScreenshotScenario(BugReportServiceTests owner)
        {
            _owner = owner;
            _timestamp = owner._time.GetTimestamp();
            owner._capture.Screenshot = () => Shot.Task;
            PendingDirectory = Path.Combine(owner._root, "pending");
            Queue = new BugReportOfflineQueue(PendingDirectory, owner._time);
            owner._subject = "discord:42";
            Service = owner.NewService(sink: Sink, queue: Queue);
            Service.Flow.FinishStarting = () => _finishing.TrySetResult();
            owner._time.TimerVisible = () =>
            {
                if (owner._time.TimersDueIn(BugReportFlow.ScreenshotWait) == 2) _timersReady.TrySetResult();
            };
        }

        internal string State => $"finish={_finishing.Task.Status}, sink={Sink.Entered.Status}, " +
            $"tokenCanCancel={Sink.Token.CanBeCanceled}, tokenCanceled={Sink.Token.IsCancellationRequested}, " +
            $"screenshot={Shot.Task.Status}, thumbnail={_thumbnail?.Status}, send={_send?.Status}, " +
            $"work=[{string.Join(", ", _owner._work.Select(work => work.Status))}], " +
            $"timestamp={_owner._time.GetTimestamp()} (start={_timestamp}), buildsCompleted={_buildsCompleted}, " +
            $"disposeElapsed={_disposeElapsed}, buildTimeout={HasBuildTimeoutWarning} ({BuildTimeoutWarning})";

        internal bool HasBuildTimeoutWarning => _owner.Warnings.Any(entry => entry.Message == BuildTimeoutWarning);

        internal Task AwaitFinishingAsync() => AwaitAsync(_finishing.Task, "finish entry");

        internal Task AwaitSendAsync() => AwaitAsync(_send!, "send completion");

        internal void AssertSendPending() => Assert.False(_send!.IsCompleted, State);

        internal void AssertBuildCompleted(TimeSpan timeout)
        {
            _buildsCompleted = Service.Flow.Builds.WaitAll(timeout);
            Assert.True(_buildsCompleted.Value, State);
        }

        internal void AssertDisposalCompleted()
        {
            Assert.True(_finishing.Task.IsCompletedSuccessfully, State);
            Assert.True(Sink.Entered.IsCompletedSuccessfully, State);
            // Dispose has already performed its bounded build wait. Its promises must now be complete.
            AssertBuildCompleted(TimeSpan.Zero);
            Assert.False(HasBuildTimeoutWarning, State);
        }

        internal void AssertRetainedWithoutScreenshot()
        {
            Assert.False(Shot.Task.IsCompleted, State);
            Assert.True(_owner._time.GetTimestamp() == _timestamp, State);
            Assert.True(Queue.Count == 1, State);
            string[] files = Directory.GetFiles(PendingDirectory, "*.zip");
            Assert.True(files.Length == 1, State);
            string zip = files[0];
            string[] names = EntryNames(new BugReportSubmission(Path.GetFileNameWithoutExtension(zip), null, File.ReadAllBytes(zip)));
            Assert.False(names.Any(name => name.StartsWith("screenshot", StringComparison.Ordinal)), State);
        }

        internal void DisposeService()
        {
            var clock = Stopwatch.StartNew();
            try
            {
                Service.Dispose();
            }
            finally
            {
                clock.Stop();
                _disposeElapsed = clock.Elapsed;
            }
        }

        internal async Task StartAsync()
        {
            Assert.False(_owner.Run(Service));
            await _owner.Settle(Service);
            _owner.Run(Service, Frame(pressed: [Key.F10]));
            for (int frame = 0; frame < 3; frame++) _owner.Run(Service, Frame(held: [Key.F10]));
            _thumbnail = Service.Flow.PendingWork;
            Assert.True(Service.IsOpen, State);
            _owner.Run(Service, Frame(text: "Fell through"));
            _owner.Run(Service, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
            _send = Service.Flow.PendingWork;
            Assert.NotNull(_send);
            // Timer publication is readiness notification, not a promise that continuations have been scheduled.
            await AwaitAsync(_timersReady.Task, "screenshot timer readiness");
            Assert.True(Queue.Count == 0, State);
            Assert.True(!Shot.Task.IsCompleted, State);
        }

        internal async Task AwaitAsync(Task work, string phase)
        {
            try
            {
                await work.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Timed out waiting for {phase}. {State}", exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Shot.TrySetException(new ObjectDisposedException("recorder"));
                _ = Shot.Task.Exception;
            }
            finally
            {
                try
                {
                    Service.Dispose();
                }
                finally
                {
                    try
                    {
                        await AwaitAsync(Task.WhenAll(_owner._work), "owned background work");
                    }
                    finally
                    {
                        _owner._time.TimerVisible = null;
                    }
                }
            }
        }
    }
}
