using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
public sealed partial class BugReportServiceTests : BugReportFlowHarness
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
    public async Task Dispose_during_the_build_keeps_the_report()
    {
        var sink = new BlockingSink();
        var queue = new BugReportOfflineQueue(Path.Combine(_root, "pending"), _time);
        BugReportService service = await SignedInService(sink, queue);
        using var finish = new ManualResetEventSlim();
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Flow.FinishStarting = () =>
        {
            finishing.TrySetResult();
            finish.Wait(TimeSpan.FromSeconds(30));
        };
        Run(service, Frame(pressed: [Key.F10]));
        Run(service, Frame(held: [Key.F10]));
        Run(service, Frame(text: "Fell through"));
        Run(service, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
        Task send = service.Flow.PendingWork!;
        await finishing.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, queue.Count);

        Task release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            finish.Set();
        });
        service.Dispose();

        // Dispose returned only once the build had written the report to the queue.
        Assert.Equal(1, queue.Count);
        await release;
        await send.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Dispose_closes_an_open_report()
    {
        BugReportService service = await SignedInService();
        var confirmed = new List<string>();
        service.ReportConfirmed += confirmed.Add;
        Run(service, Frame(pressed: [Key.F10]));
        Run(service, Frame(held: [Key.F10]));
        Assert.True(service.IsOpen);
        Assert.Single(service.Flow.Screens.Screens);

        service.Dispose();

        Assert.False(service.IsOpen);
        Assert.Empty(service.Flow.Screens.Screens);
        Assert.Equal(1, _capture.ThawCount);
        Assert.Empty(confirmed);
    }

    [Fact]
    public void Throwing_recorder_dispose_still_detaches_the_log()
    {
        BugReportService service;
        using (CaptureLogs())
        {
            service = NewService();
            Log.Warn("before shutdown");
            _host.RemoveFailure = new InvalidOperationException("window gone");

            Assert.Throws<InvalidOperationException>(service.Dispose);
            Log.Warn("after shutdown");
        }

        Assert.Contains(service.RecentLog.Snapshot(), line => line.Contains("before shutdown", StringComparison.Ordinal));
        Assert.DoesNotContain(service.RecentLog.Snapshot(), line => line.Contains("after shutdown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Previous_crash_appears_once_the_scan_finishes()
    {
        string logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        string crash = Path.Combine(logs, "game-crash-20200101.log");
        File.WriteAllText(crash, "boom");
        File.SetLastWriteTimeUtc(crash, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _subject = "discord:42";
        using BugReportService service = NewService(new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            SessionLogPath = Path.Combine(logs, "session.log"),
        });
        await service.CrashScan.WaitAsync(TimeSpan.FromSeconds(30));

        using JsonDocument session = JsonDocument.Parse(await SendAndReadSection(service, "session"));

        JsonElement previous = session.RootElement.GetProperty("previousCrash");
        Assert.Equal("game-crash-20200101.log", previous.GetProperty("file").GetString());
    }

    [Fact]
    public async Task Previous_crash_stays_null_without_a_session_log()
    {
        _subject = "discord:42";
        using BugReportService service = NewService();
        Assert.True(service.CrashScan.IsCompleted);

        using JsonDocument session = JsonDocument.Parse(await SendAndReadSection(service, "session"));

        Assert.Equal(JsonValueKind.Null, session.RootElement.GetProperty("previousCrash").ValueKind);
    }

    /// <summary>Runs the sign-in frame, opens, sends and settles one report, then returns one section's JSON text.</summary>
    async Task<string> SendAndReadSection(BugReportService service, string key)
    {
        Run(service);
        await Settle(service);
        Run(service, Frame(pressed: [Key.F10]));
        Run(service, Frame(held: [Key.F10]));
        Run(service, Frame(text: "Fell through"));
        Run(service, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
        await Settle(service);
        return EntryText(Assert.Single(_sink.Submissions), BundleEntryNames.Section(key));
    }

    [Fact]
    public async Task Dispose_cancels_the_sign_in_flush_and_keeps_the_report()
    {
        var sink = new BlockingSink();
        BugReportOfflineQueue queue = QueueWithOneReport();
        _subject = "discord:42";
        BugReportService service = NewService(sink: sink, queue: queue);
        Run(service);
        Task flush = service.Flow.PendingWork!;
        _work.Remove(flush);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        service.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(sink.Token.IsCancellationRequested);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Dispose_cancels_a_retry_and_keeps_the_report()
    {
        var sink = new BlockingSink();
        BugReportOfflineQueue queue = QueueWithOneReport();
        _subject = "discord:42";
        BugReportService service = NewService(sink: sink, queue: queue);
        Task<int> retry = service.RetryQueuedAsync(CancellationToken.None);
        await sink.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        service.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(sink.Token.IsCancellationRequested);
        Assert.Equal(1, queue.Count);
    }

    BugReportOfflineQueue QueueWithOneReport()
    {
        var queue = new BugReportOfflineQueue(Path.Combine(_root, "pending"), _time);
        queue.Enqueue(new BugReportSubmission(ReportIds.NewId(), null, new byte[] { 0x50, 0x4B, 3, 4 }));
        return queue;
    }

    [Fact]
    public async Task Throwing_subject_leaves_the_overlay_registered()
    {
        using BugReportService service = await SignedInService();
        _subjectFailure = new InvalidOperationException("session lookup");

        for (int i = 0; i < 5; i++) Assert.False(Run(service));
        Run(service, Frame(pressed: [Key.F10]));
        Run(service, Frame(held: [Key.F10]));
        Assert.True(service.IsOpen);
        Run(service, Frame(text: "Fell through"));
        Run(service, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
        await Settle(service);

        Assert.Empty(_removed);
        Assert.Equal("discord:42", Assert.Single(_sink.Submissions).Subject);
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
            () => _subjectFailure is { } failure ? throw failure : _subject, HeadlessScreen, overlay =>
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
