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
using KhaozEngine.App;
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

/// <summary>The trigger, the screen, input holding and the frame-thread failure paths of <see cref="BugReportFlow"/>.</summary>
[Collection("AmbientLocalization")]
public sealed class BugReportFlowTests : BugReportFlowHarness
{
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
    public async Task Context_carries_the_signed_in_subject()
    {
        BugReportFlow flow = NewFlow();

        Open(flow);
        Step(flow, Frame(pressed: [Key.Escape]));
        Step(flow);

        _subject = "discord:42";
        Step(flow);
        await Settle(flow);
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

        using IDisposable logs = CaptureLogs();
        BugReportFlow reloaded = NewFlow(new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            TriggerBindingsJson = saved,
        });
        Assert.Empty(Warnings);
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

    [Fact]
    public void Throwing_capture_handler_leaves_the_flow_working()
    {
        BugReportFlow flow = NewFlow();
        flow.ReportCaptured += _ => throw new InvalidOperationException("game hook");
        using IDisposable logs = CaptureLogs();

        Assert.True(Step(flow, Frame(pressed: [Key.F10])));

        Assert.Single(_captured);
        Assert.Equal(BugReportFlowState.Captured, flow.State);
        Assert.Single(Warnings);
        Assert.True(Step(flow));
        Assert.Equal(BugReportFlowState.Open, flow.State);
        Assert.Single(flow.Screens.Screens);
        Assert.True(Step(flow, Frame(pressed: [Key.Escape])));
        Assert.Equal(1, _capture.ThawCount);
        Assert.Equal(BugReportFlowState.Idle, flow.State);
    }

    [Fact]
    public void Throwing_thaw_still_closes_the_report()
    {
        BugReportFlow flow = NewFlow();
        Open(flow);
        _capture.ThawFailure = new ObjectDisposedException("recorder");

        Assert.Throws<ObjectDisposedException>(() => Step(flow, Frame(pressed: [Key.Escape])));

        Assert.Equal(BugReportFlowState.Idle, flow.State);
        Assert.Empty(flow.Screens.Screens);
        _capture.ThawFailure = null;
        Step(flow);
        Open(flow);
        Assert.Equal(2, _captured.Count);
    }

    [Fact]
    public void Screen_failure_thaws_and_toasts_the_failed_line()
    {
        BugReportFlow flow = NewFlow();
        _screenFailure = new InvalidOperationException("no font");
        using IDisposable logs = CaptureLogs();
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));

        Assert.True(Step(flow));

        Assert.Equal(BugReportFlowState.Idle, flow.State);
        Assert.Empty(flow.Screens.Screens);
        Assert.Equal(1, _capture.ThawCount);
        Assert.Single(Warnings);
        Assert.Equal("The bug report could not be prepared.", Toast(flow));

        _screenFailure = null;
        Assert.True(Step(flow));
        Open(flow);
        Assert.Equal(2, _captured.Count);
    }

    [Fact]
    public void Screenshot_completing_on_frame_2_opens_the_screen_on_frame_2()
    {
        var shot = new TaskCompletionSource<ReportImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.Screenshot = () => shot.Task;
        BugReportFlow flow = NewFlow();
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));

        Assert.True(Step(flow));
        Assert.Empty(flow.Screens.Screens);
        Assert.Equal(BugReportFlowState.Captured, flow.State);
        shot.SetResult(FakeCapture.DefaultScreenshot());
        Assert.True(Step(flow));

        Assert.Single(flow.Screens.Screens);
        Assert.True(Assert.IsType<BugReportScreen>(flow.Screens.Screens[0]).Model.ScreenshotAvailable);
    }

    [Fact]
    public void Screenshot_completing_on_frame_3_opens_the_screen_on_frame_3()
    {
        var shot = new TaskCompletionSource<ReportImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.Screenshot = () => shot.Task;
        BugReportFlow flow = NewFlow();
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));

        for (int frame = 1; frame <= 2; frame++)
        {
            Assert.True(Step(flow));
            Assert.Empty(flow.Screens.Screens);
        }
        shot.SetResult(FakeCapture.DefaultScreenshot());
        Assert.True(Step(flow));

        Assert.Single(flow.Screens.Screens);
        Assert.Equal(BugReportFlowState.Open, flow.State);
    }

    [Fact]
    public void Pending_screenshot_opens_the_screen_on_frame_3()
    {
        var shot = new TaskCompletionSource<ReportImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.Screenshot = () => shot.Task;
        BugReportFlow flow = NewFlow();
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));

        Assert.True(Step(flow));
        Assert.True(Step(flow));
        Assert.Empty(flow.Screens.Screens);
        Assert.True(Step(flow));

        Assert.Single(flow.Screens.Screens);
        Assert.True(Assert.IsType<BugReportScreen>(flow.Screens.Screens[0]).Model.ScreenshotAvailable);
        shot.SetResult(FakeCapture.DefaultScreenshot());
    }

    [Fact]
    public void Unexpected_fault_while_open_thaws_closes_and_rethrows()
    {
        BugReportFlow flow = NewFlow();
        Open(flow);

        using (LocalizationContext.Use(new ThrowingCatalog()))
            Assert.Throws<InvalidOperationException>(() => Step(flow));

        Assert.Equal(1, _capture.ThawCount);
        Assert.Equal(BugReportFlowState.Idle, flow.State);
        Assert.Empty(flow.Screens.Screens);
        Step(flow);
        Open(flow);
        Assert.Equal(2, _captured.Count);
    }

    [Fact]
    public void Unexpected_fault_after_the_trigger_frame_leaves_the_recorder_thawed()
    {
        BugReportFlow flow = NewFlow();
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));

        using (LocalizationContext.Use(new ThrowingCatalog()))
            Assert.Throws<InvalidOperationException>(() => Step(flow));

        Assert.Equal(1, _capture.ThawCount);
        Assert.Equal(BugReportFlowState.Idle, flow.State);
        Assert.Empty(flow.Screens.Screens);
    }

    [Fact]
    public async Task Throwing_subject_keeps_reports_working_with_the_last_known_subject()
    {
        BugReportFlow flow = await SignedIn();
        using IDisposable logs = CaptureLogs();
        _subjectFailure = new InvalidOperationException("session lookup");

        for (int i = 0; i < 5; i++) Assert.False(Step(flow));
        Open(flow);
        Send(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.Equal("discord:42", submission.Subject);
        Assert.Single(Warnings, entry => entry.Message.Contains("currentSubject", StringComparison.Ordinal));
        Assert.Equal(BugReportSubmitStatus.Sent, Assert.Single(_submitted).Status);
    }

    [Fact]
    public async Task Subject_that_never_answers_reports_signed_out()
    {
        _subjectFailure = new InvalidOperationException("session lookup");
        BugReportFlow flow = NewFlow();
        using IDisposable logs = CaptureLogs();

        BugReportScreen screen = Open(flow);
        Assert.False(screen.Model.SignedIn);
        Send(flow);
        await Settle(flow);

        Assert.Empty(_sink.Submissions);
        Assert.Equal(BugReportSubmitter.SignedOutError, Assert.Single(_submitted).Error);
        Assert.Equal(0, await flow.RetryQueuedAsync(default));
    }

    [Fact]
    public void Throwing_save_keeps_the_rebinding_and_the_flow_working()
    {
        BugReportFlow flow = NewFlow(new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            SaveTriggerBindings = _ => throw new IOException("disk full"),
        });
        using IDisposable logs = CaptureLogs();
        flow.TriggerControls.BeginRebind(BugReportFlow.TriggerActionId, 0);

        Assert.False(Step(flow, Frame(pressed: [Key.F9])));

        Assert.False(flow.TriggerControls.IsRebinding);
        Assert.Single(Warnings);
        Assert.False(Step(flow, Frame(pressed: [Key.F10])));
        Assert.True(Step(flow, Frame(pressed: [Key.F9])));
        Assert.Single(_captured);
    }

    /// <summary>A wired catalog whose every lookup throws, a fault the flow does not expect.</summary>
    sealed class ThrowingCatalog : IStringCatalog
    {
        public string Get(string key) => throw new InvalidOperationException("catalog");

        public string Format(string key, params object?[] args) => throw new InvalidOperationException("catalog");

        public bool TryGet(string key, out string value) => throw new InvalidOperationException("catalog");
    }

    [Fact]
    public void Malformed_trigger_bindings_keep_the_default_key()
    {
        using IDisposable logs = CaptureLogs();

        BugReportFlow flow = NewFlow(new BugReportServiceOptions
        {
            GameId = "grimhollow",
            GameDisplayName = "Grimhollow",
            TriggerBindingsJson = "{",
        });

        Assert.Single(Warnings);
        Assert.True(Step(flow, Frame(pressed: [Key.F10])));
        Assert.Single(_captured);
    }
}
