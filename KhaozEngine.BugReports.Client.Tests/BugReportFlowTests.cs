using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Client.Internal;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>
/// Drives <see cref="BugReportFlow"/> frame by frame with a scripted capture and sink, a real submitter and offline
/// queue in a temporary directory, and a headless <see cref="BugReportScreen"/>. The test thread is the frame thread.
/// </summary>
[Collection("AmbientLocalization")]
public sealed class BugReportFlowTests : IDisposable
{
    const float Dt = 1f / 60f;

    readonly string _root = Path.Combine(Path.GetTempPath(), "ke-bugreport-flow-" + Guid.NewGuid().ToString("N"));
    readonly ManualTimeProvider _time = new();
    readonly FakeCapture _capture = new();
    readonly FakeSink _sink = new();
    readonly BugReportSectionSet _sections = new();
    readonly DesignViewport _viewport = new(1280, 720);
    readonly List<string> _captured = new();
    readonly List<BugReportSubmitResult> _submitted = new();
    readonly List<bool> _submittedOnFrameThread = new();
    readonly List<(int Width, int Height, bool OnFrameThread)> _textures = new();
    readonly List<string> _saved = new();
    readonly ProbeSection _probe;
    string? _subject = "discord:42";
    int _frameThread;
    bool _inUpdate;

    public BugReportFlowTests()
    {
        _probe = new ProbeSection((writer, context) =>
        {
            writer.WriteStartObject();
            writer.WriteString("subject", context.Subject);
            writer.WriteEndObject();
        });
        _sections.Add(_probe);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        else if (File.Exists(_root)) File.Delete(_root);
    }

    BugReportFlow NewFlow(BugReportServiceOptions? options = null, string? queueDirectory = null)
    {
        options ??= new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            SaveTriggerBindings = _saved.Add,
        };
        var queue = new BugReportOfflineQueue(queueDirectory ?? Path.Combine(_root, "pending"), _time);
        var flow = new BugReportFlow(options, _capture, _sections, new BugReportSubmitter(_sink, queue), new ReportRedactor(),
            () => _subject,
            model => new BugReportScreen(model, new BugReportScreenResources(new FixedMeasurer(), null, null, _viewport)),
            image =>
            {
                _textures.Add((image.Width, image.Height, OnFrameThread()));
                return null;
            },
            _time);
        flow.ReportCaptured += _captured.Add;
        flow.ReportSubmitted += result =>
        {
            _submitted.Add(result);
            _submittedOnFrameThread.Add(OnFrameThread());
        };
        return flow;
    }

    static InputState Frame(Key[]? pressed = null, Key[]? held = null, string? text = null)
    {
        var down = new HashSet<Key>(held ?? []);
        var edges = new HashSet<Key>(pressed ?? []);
        down.UnionWith(edges);
        return new InputState(down, edges, new HashSet<Key>(), new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 1280, 720, textInput: text ?? "", textInputAvailable: text is not null);
    }

    /// <summary>Runs one frame. The calling thread is the frame thread for the length of the call.</summary>
    bool Step(BugReportFlow flow, InputState? input = null)
    {
        _frameThread = Environment.CurrentManagedThreadId;
        Volatile.Write(ref _inUpdate, true);
        try
        {
            return flow.Update(input ?? Frame(), Dt, _viewport);
        }
        finally
        {
            Volatile.Write(ref _inUpdate, false);
        }
    }

    /// <summary>True when called from inside <see cref="Step"/> on its thread.</summary>
    bool OnFrameThread() => Volatile.Read(ref _inUpdate) && Environment.CurrentManagedThreadId == _frameThread;

    /// <summary>Presses F10, then runs the frame that pushes the screen, and returns the screen.</summary>
    BugReportScreen Open(BugReportFlow flow)
    {
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));
        Assert.True(Step(flow, Frame(held: [Key.F10])));
        return Assert.IsType<BugReportScreen>(Assert.Single(flow.Screens.Screens));
    }

    /// <summary>Types a description and sends with Ctrl+Enter. Returns what the send frame returned.</summary>
    bool Send(BugReportFlow flow, string description = "Fell through")
    {
        Assert.True(Step(flow, Frame(text: description)));
        return Step(flow, Frame(pressed: [Key.Enter], held: [Key.LeftControl]));
    }

    /// <summary>Awaits the latest background task, then runs one frame so its posted work lands.</summary>
    async Task Settle(BugReportFlow flow)
    {
        if (flow.PendingWork is { } work) await work.WaitAsync(TimeSpan.FromSeconds(30));
        Step(flow);
    }

    string Toast(BugReportFlow flow) => flow.Toasts.Active[0].Message.Resolve();

    static string[] EntryNames(BugReportSubmission submission)
    {
        using var zip = new ZipArchive(new MemoryStream(submission.Bundle.ToArray()), ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToArray();
    }

    static string EntryText(BugReportSubmission submission, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(submission.Bundle.ToArray()), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Trigger_captures_in_the_same_frame_and_opens_next_frame()
    {
        BugReportFlow flow = NewFlow();
        Assert.False(Step(flow));
        Assert.Equal(BugReportFlowState.Idle, flow.State);

        Assert.True(Step(flow, Frame(pressed: [Key.F10])));

        string id = Assert.Single(_captured);
        Assert.True(ReportIds.IsValid(id));
        Assert.Equal(1, _capture.FreezeCount);
        Assert.Equal(1, _capture.ScreenshotCount);
        Assert.Empty(flow.Screens.Screens);
        Assert.Equal(BugReportFlowState.Captured, flow.State);

        Assert.True(Step(flow, Frame(held: [Key.F10])));

        Assert.IsType<BugReportScreen>(Assert.Single(flow.Screens.Screens));
        Assert.Equal(BugReportFlowState.Open, flow.State);
        Assert.Single(_captured);
    }

    [Fact]
    public void Sections_are_captured_in_the_trigger_frame()
    {
        BugReportFlow flow = NewFlow();

        Step(flow, Frame(pressed: [Key.F10]));
        Assert.Equal(1, _probe.Writes);

        Step(flow);
        Send(flow);
        Assert.Equal(1, _probe.Writes);
    }

    [Fact]
    public void Context_carries_the_signed_in_subject()
    {
        _subject = null;
        BugReportFlow flow = NewFlow();

        Open(flow);
        Step(flow, Frame(pressed: [Key.Escape]));
        Step(flow);
        Step(flow);

        _subject = "discord:42";
        Open(flow);

        Assert.Equal([null, "discord:42"], _probe.Subjects);
    }

    [Fact]
    public void Sections_see_the_trigger_frame_input()
    {
        BugReportFlow? flow = null;
        InputState? seen = null;
        _sections.Add(new ProbeSection((writer, _) =>
        {
            seen = flow!.LastFrameInput;
            writer.WriteStartObject();
            writer.WriteEndObject();
        }, "input-probe"));
        flow = NewFlow();
        InputState trigger = Frame(pressed: [Key.F10]);

        Step(flow, trigger);

        Assert.Same(trigger, seen);
        Assert.Same(trigger, flow.LastFrameInput);
    }

    [Fact]
    public void Recorder_screenshot_limits_come_from_contracts()
    {
        using var largest = new FrameHistoryRecorder(new FakeCaptureHost(),
            new FrameHistoryOptions { LongEdge = BugReportLimits.MaxScreenshotLongEdge }, _time);
        ArgumentOutOfRangeException tooLarge = Assert.Throws<ArgumentOutOfRangeException>(() => new FrameHistoryRecorder(
            new FakeCaptureHost(), new FrameHistoryOptions { LongEdge = BugReportLimits.MaxScreenshotLongEdge + 1 }, _time));
        Assert.Contains(BugReportLimits.MaxScreenshotLongEdge.ToString(System.Globalization.CultureInfo.InvariantCulture),
            tooLarge.Message, StringComparison.Ordinal);

        ReportImage small = ScreenshotEncoder.Encode(new byte[16 * 8 * 4], 16, 8);
        Assert.Equal(ImageHeaders.PngMediaType, small.MediaType);
        Assert.True(small.Bytes.Length < BugReportLimits.MaxPngScreenshotBytes);
    }

    [Fact]
    public void Recorder_capture_offers_the_clip_only_while_recording()
    {
        using var recorder = new FrameHistoryRecorder(new FakeCaptureHost(), new FrameHistoryOptions(), _time);
        var capture = new RecorderCapture(recorder);

        Assert.False(capture.ClipAvailable);
        Assert.Null(capture.FreezeClip());
        capture.Thaw();

        recorder.Start();
        Assert.True(capture.ClipAvailable);
        Assert.NotNull(capture.FreezeClip());
        capture.Thaw();

        using var unavailable = new FrameHistoryRecorder(new FakeCaptureHost { SupportsClipCapture = false },
            new FrameHistoryOptions(), _time);
        unavailable.Start();
        Assert.False(new RecorderCapture(unavailable).ClipAvailable);
    }

    [Fact]
    public void Second_trigger_while_open_is_ignored()
    {
        BugReportFlow flow = NewFlow();
        Step(flow, Frame(pressed: [Key.F10]));
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));

        Assert.Single(_captured);
        Assert.Equal(1, _capture.FreezeCount);
        Assert.Equal(1, _capture.ScreenshotCount);
        Assert.Single(flow.Screens.Screens);
        Assert.Equal(BugReportFlowState.Open, flow.State);
    }

    [Fact]
    public void Begin_captures_on_the_next_update_and_fails_while_open()
    {
        BugReportFlow flow = NewFlow();

        Assert.True(flow.Begin());
        Assert.Empty(_captured);
        Assert.Equal(BugReportFlowState.Idle, flow.State);

        Assert.True(Step(flow));
        Assert.Single(_captured);
        Assert.Equal(BugReportFlowState.Captured, flow.State);
        Assert.False(flow.Begin());

        Step(flow);
        Assert.Equal(BugReportFlowState.Open, flow.State);
        Assert.False(flow.Begin());
        Step(flow);
        Assert.Single(_captured);
    }

    [Fact]
    public async Task Cancel_thaws_and_sends_nothing()
    {
        BugReportFlow flow = NewFlow();
        Open(flow);
        Step(flow, Frame(text: "Fell through"));

        Assert.True(Step(flow, Frame(pressed: [Key.Escape])));

        Assert.Equal(1, _capture.ThawCount);
        Assert.Empty(flow.Screens.Screens);
        Assert.Equal(BugReportFlowState.Idle, flow.State);
        Assert.True(Step(flow));
        Assert.False(Step(flow));

        await Settle(flow);
        Assert.Empty(_sink.Submissions);
        Assert.Empty(_submitted);
        Assert.Empty(flow.Toasts.Active);
        Assert.Equal(1, _capture.ThawCount);
    }

    [Fact]
    public async Task Send_submits_a_valid_bundle_and_toasts_the_reference()
    {
        BugReportFlow flow = NewFlow();
        Open(flow);

        Assert.True(Send(flow));
        Assert.Equal(1, _capture.ThawCount);
        Assert.Empty(flow.Screens.Screens);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.Equal(_captured[0], submission.ReportId);
        Assert.Equal("discord:42", submission.Subject);
        BundleValidationResult validation = BundleValidator.Validate(new MemoryStream(submission.Bundle.ToArray()));
        Assert.True(validation.IsValid, validation.Error);
        Assert.Equal("Fell through", validation.Manifest!.Description);
        Assert.Equal("Other", validation.Manifest.Category);
        Assert.Equal("grimhollow", validation.Manifest.GameId);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 3, 4, 5, TimeSpan.Zero), validation.Manifest.CapturedAt);
        string[] names = EntryNames(submission);
        Assert.Contains("sections/probe.json", names);
        Assert.Contains(BundleEntryNames.ScreenshotPng, names);
        Assert.Contains(BundleEntryNames.ClipIndex, names);

        Assert.Equal("Bug report sent. Reference " + ReportIds.ReferenceCode(submission.ReportId), Toast(flow));
        BugReportSubmitResult result = Assert.Single(_submitted);
        Assert.Equal(BugReportSubmitStatus.Sent, result.Status);
        Assert.Equal([true], _submittedOnFrameThread);
    }

    [Fact]
    public async Task Sections_are_redacted_off_the_frame_thread()
    {
        _sections.Add(new ProbeSection((writer, _) =>
        {
            writer.WriteStartObject();
            writer.WriteString("token", "Bearer abcdefgh12");
            writer.WriteEndObject();
        }, "secrets"));
        BugReportFlow flow = NewFlow();
        bool? finishOnFrameThread = null;
        flow.FinishStarting = () => finishOnFrameThread = OnFrameThread();
        Open(flow);

        Send(flow);
        await Settle(flow);

        string section = EntryText(Assert.Single(_sink.Submissions), "sections/secrets.json");
        Assert.Contains(ReportRedactor.Mask, section, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefgh12", section, StringComparison.Ordinal);
        Assert.False(finishOnFrameThread);
    }

    [Fact]
    public async Task Signed_out_send_is_queued_without_the_sink()
    {
        _subject = null;
        BugReportFlow flow = NewFlow();
        BugReportScreen screen = Open(flow);
        Assert.False(screen.Model.SignedIn);

        Send(flow);
        await Settle(flow);

        Assert.Empty(_sink.Submissions);
        Assert.Equal("Bug report saved. It will be sent after you sign in.", Toast(flow));
        BugReportSubmitResult result = Assert.Single(_submitted);
        Assert.Equal(BugReportSubmitStatus.Queued, result.Status);
        Assert.Equal(BugReportSubmitter.SignedOutError, result.Error);
    }

    [Fact]
    public async Task Sign_in_flushes_the_queue_once()
    {
        _subject = null;
        BugReportFlow flow = NewFlow();
        Open(flow);
        Send(flow);
        await Settle(flow);
        Assert.Empty(_sink.Submissions);

        _subject = "acct-1";
        Step(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.Equal("acct-1", submission.Subject);
        Assert.Equal(_captured[0], submission.ReportId);
        Assert.Equal("1 saved bug reports sent.", Toast(flow));

        Step(flow);
        Step(flow);
        await Settle(flow);
        Assert.Single(_sink.Submissions);
    }

    [Fact]
    public async Task Retry_flushes_for_the_current_subject()
    {
        _subject = null;
        BugReportFlow flow = NewFlow();
        Open(flow);
        Send(flow);
        await Settle(flow);

        Assert.Equal(0, await flow.RetryQueuedAsync(default));
        _subject = "acct-1";
        Assert.Equal(1, await flow.RetryQueuedAsync(default));
        Step(flow);

        Assert.Equal("acct-1", Assert.Single(_sink.Submissions).Subject);
        Assert.Equal("1 saved bug reports sent.", Toast(flow));
    }

    [Fact]
    public async Task Screenshot_failure_disables_the_toggle_and_still_sends()
    {
        _capture.Screenshot = () => Task.FromException<ReportImage>(new InvalidOperationException("tap fault"));
        BugReportFlow flow = NewFlow();
        BugReportScreen screen = Open(flow);

        Assert.False(screen.Model.ScreenshotAvailable);
        Assert.False(screen.Model.IncludeScreenshot);
        Send(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.True(BundleValidator.Validate(new MemoryStream(submission.Bundle.ToArray())).IsValid);
        Assert.DoesNotContain(EntryNames(submission), n => n.StartsWith("screenshot", StringComparison.Ordinal));
        Assert.Equal("Bug report sent. Reference " + ReportIds.ReferenceCode(submission.ReportId), Toast(flow));
    }

    [Fact]
    public async Task Screenshot_failure_after_open_disables_the_toggle_on_the_frame_thread()
    {
        var shot = new TaskCompletionSource<ReportImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.Screenshot = () => shot.Task;
        BugReportFlow flow = NewFlow();
        BugReportScreen screen = Open(flow);
        Assert.True(screen.Model.ScreenshotAvailable);

        shot.SetException(new InvalidOperationException("tap fault"));
        await Settle(flow);

        Assert.False(screen.Model.ScreenshotAvailable);
        Assert.False(screen.ScreenshotToggle.Enabled || screen.ScreenshotToggle.IsOn);
        Send(flow);
        await Settle(flow);
        Assert.DoesNotContain(EntryNames(Assert.Single(_sink.Submissions)),
            n => n.StartsWith("screenshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_clip_sends_without_clip_entries()
    {
        _capture.ClipAvailable = false;
        BugReportFlow flow = NewFlow();
        BugReportScreen screen = Open(flow);

        Assert.False(screen.Model.ClipAvailable);
        Assert.False(screen.Model.IncludeClip);
        Step(flow);
        Assert.False(screen.ClipToggle.Enabled);
        Send(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.True(BundleValidator.Validate(new MemoryStream(submission.Bundle.ToArray())).IsValid);
        Assert.DoesNotContain(EntryNames(submission), n => n.StartsWith("clip/", StringComparison.Ordinal));
        Assert.Contains(BundleEntryNames.ScreenshotPng, EntryNames(submission));
    }

    [Fact]
    public async Task Rejected_send_toasts_the_reason()
    {
        _sink.Respond(BugReportSubmitStatus.Rejected, "blocked");
        BugReportFlow flow = NewFlow();
        Open(flow);

        Send(flow);
        await Settle(flow);

        Assert.Equal("Bug report not accepted (blocked).", Toast(flow));
        BugReportSubmitResult result = Assert.Single(_submitted);
        Assert.Equal(BugReportSubmitStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Queue_failure_toasts_the_failed_line()
    {
        Directory.CreateDirectory(_root);
        string blocked = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocked, "x");
        _sink.Respond(BugReportSubmitStatus.Queued, "network");
        BugReportFlow flow = NewFlow(queueDirectory: Path.Combine(blocked, "pending"));
        Open(flow);

        Send(flow);
        await Settle(flow);

        Assert.Single(_sink.Submissions);
        BugReportSubmitResult result = Assert.Single(_submitted);
        Assert.Equal(BugReportSubmitter.QueueFailedError, result.Error);
        Assert.Equal("The bug report could not be prepared.", Toast(flow));
    }

    [Fact]
    public async Task Sink_failure_toasts_the_queued_line()
    {
        _sink.Throw(new HttpRequestException("offline"));
        BugReportFlow flow = NewFlow();
        Open(flow);

        Send(flow);
        await Settle(flow);

        Assert.Equal(BugReportSubmitter.SinkFailedError, Assert.Single(_submitted).Error);
        Assert.Equal("Bug report saved. It will be sent the next time you sign in.", Toast(flow));
    }

    [Fact]
    public async Task Build_failure_toasts_the_failed_line()
    {
        ReportClipFrame frame = FakeCapture.DefaultClip(1).Frames[0];
        _capture.Clip = new ReportClip([frame with { Width = frame.Width + 1 }]);
        BugReportFlow flow = NewFlow();
        Open(flow);

        Send(flow);
        await Settle(flow);

        Assert.Empty(_sink.Submissions);
        Assert.Empty(_submitted);
        Assert.Equal("The bug report could not be prepared.", Toast(flow));
    }

    [Fact]
    public async Task Thumbnails_are_textured_on_the_frame_thread()
    {
        BugReportFlow flow = NewFlow();
        Open(flow);

        await Settle(flow);

        Assert.Equal(5, _textures.Count);
        Assert.All(_textures, t => Assert.True(t.OnFrameThread));
        Assert.Single(_textures, t => (t.Width, t.Height) == (FakeCapture.ShotWidth, FakeCapture.ShotHeight));
        Assert.Equal(4, _textures.Count(t => (t.Width, t.Height) == (FakeCapture.ClipWidth / 8, FakeCapture.ClipHeight / 8)));
    }

    [Fact]
    public async Task Corrupt_screenshot_shows_no_thumbnail_and_still_sends()
    {
        _capture.Screenshot = () => Task.FromResult(FakeCapture.CorruptScreenshot());
        BugReportFlow flow = NewFlow();
        BugReportScreen screen = Open(flow);
        await Settle(flow);

        Assert.Equal(4, _textures.Count);
        Assert.DoesNotContain(_textures, t => (t.Width, t.Height) == (FakeCapture.ShotWidth, FakeCapture.ShotHeight));
        Assert.Empty(flow.Toasts.Active);
        Assert.True(screen.Model.ScreenshotAvailable);

        Send(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.Contains(BundleEntryNames.ScreenshotPng, EntryNames(submission));
        Assert.Equal("Bug report sent. Reference " + ReportIds.ReferenceCode(submission.ReportId), Toast(flow));
    }

    [Fact]
    public void Typing_in_the_screen_never_reaches_the_game()
    {
        BugReportFlow flow = NewFlow();
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));
        Assert.True(Step(flow, Frame(held: [Key.F10])));

        foreach (string text in new[] { "W", "A", "S", "D", " ", "jump" })
            Assert.True(Step(flow, Frame(text: text)));
        Assert.Equal("WASD jump", ((BugReportScreen)flow.Screens.Screens[0]).Model.Description);

        Assert.True(Step(flow, Frame(pressed: [Key.Enter], held: [Key.LeftControl])));
        Assert.True(Step(flow, Frame(held: [Key.LeftControl])));
        Assert.False(Step(flow));
    }

    [Fact]
    public void Frame_after_send_still_holds()
    {
        BugReportFlow flow = NewFlow();
        Open(flow);

        Assert.True(Send(flow));
        Assert.Equal(BugReportFlowState.Idle, flow.State);
        Assert.True(Step(flow));
        Assert.False(Step(flow));
    }

    [Fact]
    public void Trigger_rebind_saves_through_the_callback()
    {
        BugReportFlow flow = NewFlow();
        flow.TriggerControls.BeginRebind(BugReportFlow.TriggerActionId, 0);

        Assert.False(Step(flow, Frame(pressed: [Key.F9])));
        string saved = Assert.Single(_saved);
        Assert.Contains("F9", saved, StringComparison.Ordinal);
        Assert.Empty(_captured);

        Assert.False(Step(flow, Frame(pressed: [Key.F10])));
        Assert.True(Step(flow, Frame(pressed: [Key.F9])));
        Assert.Single(_captured);

        BugReportFlow reloaded = NewFlow(new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            TriggerBindingsJson = saved,
        });
        Assert.False(Step(reloaded, Frame(pressed: [Key.F10])));
        Assert.True(Step(reloaded, Frame(pressed: [Key.F9])));
        Assert.Equal(2, _captured.Count);
    }

    [Fact]
    public void Options_are_validated_and_categories_reach_the_screen()
    {
        Assert.ThrowsAny<ArgumentException>(() => NewFlow(new BugReportServiceOptions
        {
            GameId = "Grim Hollow",
            GameDisplayName = "Grimhollow",
        }));

        BugReportFlow flow = NewFlow(new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            Categories = ["Crafting", "Combat"],
        });
        Assert.Equal(["Crafting", "Combat"], Open(flow).Model.Categories);

        BugReportFlow defaults = NewFlow();
        Assert.Equal(BugReportDraft.DefaultCategories, Open(defaults).Model.Categories);
    }

    sealed class ProbeSection(Action<Utf8JsonWriter, BugReportContext> write, string key = "probe") : IBugReportSection
    {
        public string Key => key;

        public int MaxBytes => 4096;

        public int Writes { get; private set; }

        public List<string?> Subjects { get; } = new();

        public void Write(Utf8JsonWriter writer, BugReportContext context)
        {
            Writes++;
            Subjects.Add(context.Subject);
            write(writer, context);
        }
    }

    sealed class FixedMeasurer : ITextMeasurer
    {
        public float LineHeight => 20f;

        public Vector2 Measure(string text) => new(text.Length * 10f, LineHeight);

        public Vector2 Measure(ReadOnlySpan<char> text) => new(text.Length * 10f, LineHeight);
    }
}
