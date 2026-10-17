using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>
/// The public constants and the headless half of <see cref="BugReportService"/>: its overlay, open state, built-in
/// sections, recording toggle and shutdown order. The window half needs a <see cref="GameApp"/> and is proved live.
/// </summary>
[Collection("AmbientLocalization")]
public sealed class BugReportServiceTests : BugReportFlowHarness
{
    readonly FakeCaptureHost _host = new();
    readonly List<IGameOverlay> _removed = new();
    Action? _onRemove;

    [Fact]
    public void Trigger_action_id_is_ReportBug() => Assert.Equal("ReportBug", BugReportService.TriggerActionId);

    [Fact]
    public async Task IsOpen_is_true_from_capture_until_the_screen_closes()
    {
        using BugReportService service = await SignedInService();
        Assert.False(service.IsOpen);

        Assert.True(Run(service, Frame(pressed: [Key.F10])));
        Assert.True(service.IsOpen);
        Assert.True(Run(service, Frame(held: [Key.F10])));
        Assert.True(service.IsOpen);
        Assert.True(Run(service));
        Assert.True(service.IsOpen);

        Assert.True(Run(service, Frame(pressed: [Key.Escape])));
        Assert.False(service.IsOpen);
        // The frame after the close still holds the game's input, while the report is no longer open.
        Assert.True(Run(service));
        Assert.False(service.IsOpen);
        Assert.False(Run(service));
    }

    [Fact]
    public async Task Built_in_sections_read_the_trigger_frame()
    {
        using BugReportService service = await SignedInService();
        Run(service, Frame(pressed: [Key.F10]));
        Run(service, Frame(held: [Key.F10]));
        Run(service, Frame(text: "Fell through"));
        Run(service, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
        await Settle(service);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        string[] names = EntryNames(submission);
        foreach (string key in new[] { "build", "platform", "session", "input", "performance", "log", "probe" })
            Assert.Contains(BundleEntryNames.Section(key), names);
        Assert.Contains("\"framebuffer\":{\"width\":1280,\"height\":720}",
            EntryText(submission, BundleEntryNames.Section("platform")), StringComparison.Ordinal);
        Assert.Contains("Key:F10", EntryText(submission, BundleEntryNames.Section("input")), StringComparison.Ordinal);
    }

    [Fact]
    public void SetRecordingEnabled_starts_and_stops_the_recorder()
    {
        using BugReportService service = NewService();
        Assert.True(service.Recorder.IsRecording);

        service.SetRecordingEnabled(false);
        Assert.False(service.Recorder.IsRecording);
        service.SetRecordingEnabled(true);
        Assert.True(service.Recorder.IsRecording);
    }

    [Fact]
    public void Recording_stays_off_when_the_options_disable_it()
    {
        using BugReportService service = NewService(new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            RecordingEnabled = false,
        });

        Assert.False(service.Recorder.IsRecording);
    }

    [Fact]
    public async Task Dispose_cancels_pending_work_and_keeps_the_queued_report()
    {
        var sink = new BlockingSink();
        var queue = new BugReportOfflineQueue(Path.Combine(_root, "pending"), _time);
        BugReportService service;
        using (CaptureLogs())
        {
            service = await SignedInService(sink, queue);
            int tapsAtRemoval = -1;
            bool canceledAtRemoval = true;
            _onRemove = () =>
            {
                tapsAtRemoval = _host.Taps.Count;
                canceledAtRemoval = sink.Token.IsCancellationRequested;
            };
            Run(service, Frame(pressed: [Key.F10]));
            Run(service, Frame(held: [Key.F10]));
            Run(service, Frame(text: "Fell through"));
            Run(service, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
            Task send = service.Flow.PendingWork!;
            await sink.Entered.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, queue.Count);
            Log.Warn("before shutdown");

            service.Dispose();
            Log.Warn("after shutdown");

            Assert.Equal([service.Overlay], _removed);
            Assert.Equal(2, tapsAtRemoval);
            Assert.False(canceledAtRemoval);
            Assert.Empty(_host.Taps);
            // The send registers for shutdown after the sink is entered, so its cancel can land after Dispose returns.
            await send.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(sink.Token.IsCancellationRequested);
            Assert.Equal(1, queue.Count);
            Assert.Contains(service.RecentLog.Snapshot(), line => line.Contains("before shutdown", StringComparison.Ordinal));
            Assert.DoesNotContain(service.RecentLog.Snapshot(), line => line.Contains("after shutdown", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(Warnings, entry => entry.Message.Contains("could not be submitted", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispose_twice_removes_the_overlay_once()
    {
        BugReportService service = NewService();
        service.Dispose();
        service.Dispose();

        Assert.Equal([service.Overlay], _removed);
    }

    BugReportService NewService(BugReportServiceOptions? options = null, IBugReportSink? sink = null,
        BugReportOfflineQueue? queue = null)
    {
        options ??= new BugReportServiceOptions { GameId = "grimhollow", GameDisplayName = "Grimhollow" };
        queue ??= new BugReportOfflineQueue(Path.Combine(_root, "pending"), _time);
        var recorder = new FrameHistoryRecorder(_host, options.Recording, _time);
        var service = new BugReportService(options, recorder, _capture, new BugReportSubmitter(sink ?? _sink, queue),
            () => _subject, HeadlessScreen, overlay =>
            {
                _onRemove?.Invoke();
                _removed.Add(overlay);
                return true;
            }, _time);
        service.AddSection(_probe);
        return service;
    }

    /// <summary>Signs in as discord:42, creates the service and settles its sign-in flush.</summary>
    async Task<BugReportService> SignedInService(IBugReportSink? sink = null, BugReportOfflineQueue? queue = null)
    {
        _subject = "discord:42";
        BugReportService service = NewService(sink: sink, queue: queue);
        Assert.False(Run(service));
        await Settle(service);
        return service;
    }

    /// <summary>Runs one frame of the service's overlay with the calling thread as the frame thread.</summary>
    bool Run(BugReportService service, InputState? input = null)
    {
        bool held = OnFrame(() => service.Overlay.Update(input ?? Frame(), Dt));
        if (service.Flow.PendingWork is { } work && !_work.Contains(work)) _work.Add(work);
        return held;
    }

    async Task Settle(BugReportService service)
    {
        if (service.Flow.PendingWork is { } work) await work.WaitAsync(TimeSpan.FromSeconds(30));
        Run(service);
    }

    /// <summary>Holds every submission until its token is canceled, then throws the cancellation.</summary>
    sealed class BlockingSink : IBugReportSink
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal CancellationToken Token { get; private set; }

        public async Task<BugReportSubmitResult> SubmitAsync(BugReportSubmission submission, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("A canceled delay returned.");
        }
    }
}
