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

/// <summary>Send, the toasts, the offline queue and flushes, and the media failure paths of <see cref="BugReportFlow"/>.</summary>
[Collection("AmbientLocalization")]
public sealed class BugReportFlowSendTests : BugReportFlowHarness
{
    [Fact]
    public async Task Send_submits_a_valid_bundle_and_toasts_the_reference()
    {
        BugReportFlow flow = await SignedIn();
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
        BugReportFlow flow = await SignedIn();
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
    public async Task Flush_with_a_rejected_report_toasts_the_rejection()
    {
        BugReportFlow flow = NewFlow();
        for (int i = 0; i < 2; i++)
        {
            Open(flow);
            Send(flow);
            await Settle(flow);
            Step(flow);
        }
        _sink.Respond(BugReportSubmitStatus.Rejected, "blocked").Respond(BugReportSubmitStatus.Sent);

        _subject = "acct-1";
        Step(flow);
        await Settle(flow);

        Assert.Equal(2, _sink.Submissions.Count);
        string[] toasts = flow.Toasts.Active.Select(toast => toast.Message.Resolve()).ToArray();
        Assert.Contains("1 saved bug reports sent.", toasts);
        Assert.Contains("1 saved bug reports were not accepted.", toasts);
    }

    [Fact]
    public async Task Retry_flushes_for_the_current_subject()
    {
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
        BugReportFlow flow = await SignedIn();
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
        BugReportFlow flow = await SignedIn();
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
        BugReportFlow flow = await SignedIn();
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
        BugReportFlow flow = await SignedIn();
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
        BugReportFlow flow = await SignedIn(queueDirectory: Path.Combine(blocked, "pending"));
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
        BugReportFlow flow = await SignedIn();
        Open(flow);

        Send(flow);
        await Settle(flow);

        Assert.Equal(BugReportSubmitter.SinkFailedError, Assert.Single(_submitted).Error);
        Assert.Equal("Bug report saved. It will be sent the next time you sign in.", Toast(flow));
    }

    [Fact]
    public async Task Clip_frame_with_a_mismatched_header_sends_without_the_clip()
    {
        ReportClipFrame frame = FakeCapture.DefaultClip(1).Frames[0];
        _capture.Clip = new ReportClip([frame with { Width = frame.Width + 1 }]);
        BugReportFlow flow = await SignedIn();
        Open(flow);

        Send(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.True(BundleValidator.Validate(new MemoryStream(submission.Bundle.ToArray())).IsValid);
        Assert.DoesNotContain(EntryNames(submission), n => n.StartsWith("clip/", StringComparison.Ordinal));
        Assert.Contains(BundleEntryNames.ScreenshotPng, EntryNames(submission));
        Assert.Equal("Bug report sent. Reference " + ReportIds.ReferenceCode(submission.ReportId), Toast(flow));
    }

    [Fact]
    public async Task Screenshot_with_a_mismatched_header_sends_without_it()
    {
        _capture.Screenshot = () => Task.FromResult(FakeCapture.DefaultScreenshot() with { Width = FakeCapture.ShotWidth + 1 });
        BugReportFlow flow = await SignedIn();
        Open(flow);

        Send(flow);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.True(BundleValidator.Validate(new MemoryStream(submission.Bundle.ToArray())).IsValid);
        Assert.DoesNotContain(EntryNames(submission), n => n.StartsWith("screenshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_failure_without_media_toasts_the_failed_line()
    {
        BugReportFlow flow = await SignedIn();
        // A capture after 2107 UTC cannot stamp a zip entry, so even the sections alone cannot be written.
        _time.Advance(TimeSpan.FromDays(366 * 82));
        Open(flow);

        Send(flow);
        await Settle(flow);

        Assert.Empty(_sink.Submissions);
        Assert.Empty(_submitted);
        Assert.Equal("The bug report could not be prepared.", Toast(flow));
    }

    [Fact]
    public async Task Corrupt_screenshot_shows_no_thumbnail_and_still_sends()
    {
        _capture.Screenshot = () => Task.FromResult(FakeCapture.CorruptScreenshot());
        BugReportFlow flow = await SignedIn();
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
    public async Task Retry_runs_off_the_caller_thread()
    {
        BugReportFlow flow = NewFlow();
        Open(flow);
        Send(flow);
        await Settle(flow);
        bool? submitOnCaller = null;
        _sink.OnSubmit = () => submitOnCaller = OnFrameThread();
        _subject = "acct-1";

        Task<int> retry = OnFrame(() => flow.RetryQueuedAsync(default));

        Assert.Equal(1, await retry.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(submitOnCaller);
    }

    [Fact]
    public async Task Sign_in_during_a_signed_out_send_flushes_it()
    {
        BugReportFlow flow = NewFlow();
        Open(flow);
        using var gate = new ManualResetEventSlim();
        flow.FinishStarting = () => gate.Wait(TimeSpan.FromSeconds(30));
        Send(flow);
        Task send = flow.PendingWork!;

        _subject = "acct-1";
        Step(flow);
        await Settle(flow);
        Assert.Empty(_sink.Submissions);

        gate.Set();
        await send.WaitAsync(TimeSpan.FromSeconds(30));
        Step(flow);
        Assert.Equal(BugReportSubmitter.SignedOutError, Assert.Single(_submitted).Error);
        await Settle(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.Equal("acct-1", submission.Subject);
        Assert.Equal(_captured[0], submission.ReportId);
        Assert.Equal("1 saved bug reports sent.", Toast(flow));
    }

    [Fact]
    public async Task Screenshot_timeout_sends_without_a_screenshot()
    {
        var shot = new TaskCompletionSource<ReportImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capture.Screenshot = () => shot.Task;
        BugReportFlow flow = await SignedIn();
        Open(flow);
        Send(flow);
        Task send = flow.PendingWork!;

        await WaitForTimers(2);
        _time.Advance(BugReportFlow.ScreenshotWait - TimeSpan.FromTicks(1));
        Assert.False(send.IsCompleted);
        _time.Advance(TimeSpan.FromTicks(1));
        await send.WaitAsync(TimeSpan.FromSeconds(30));
        Step(flow);

        BugReportSubmission submission = Assert.Single(_sink.Submissions);
        Assert.True(BundleValidator.Validate(new MemoryStream(submission.Bundle.ToArray())).IsValid);
        Assert.DoesNotContain(EntryNames(submission), n => n.StartsWith("screenshot", StringComparison.Ordinal));
        Assert.Equal("Bug report sent. Reference " + ReportIds.ReferenceCode(submission.ReportId), Toast(flow));
    }
}
