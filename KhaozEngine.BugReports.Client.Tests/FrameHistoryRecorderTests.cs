using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Client.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

public sealed class FrameHistoryRecorderTests
{
    static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    static FrameHistoryRecorder Create(FakeCaptureHost host, ManualTimeProvider? time = null,
        Func<RawClipFrame, byte[]>? encode = null, TimeSpan? freezeWait = null)
        => new(host, new FrameHistoryOptions(), time ?? new ManualTimeProvider(), encode)
        {
            FreezeWait = freezeWait ?? FrameHistoryRecorder.DefaultFreezeWait,
        };

    static ClipFrameRecord Encoded(FrameHistoryRecorder recorder, TimeSpan at)
        => new(at, 16.7, 960, 540, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, recorder.History.Generation);

    static RawClipFrame Raw(FrameHistoryRecorder recorder, TimeSpan at)
        => new(ArrayPool<byte>.Shared.Rent(4), 1, 1, at, 16.7, recorder.History.Generation);

    [Fact]
    public void Construction_registers_only_the_standby_screenshot_tap()
    {
        var host = new FakeCaptureHost();
        using var recorder = Create(host);

        IFrameCaptureTap tap = Assert.Single(host.Taps);
        Assert.IsType<ScreenshotTap>(tap);
        Assert.True(recorder.IsAvailable);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void Start_registers_the_clip_tap_and_Stop_removes_it_and_clears_the_ring()
    {
        var host = new FakeCaptureHost();
        using var recorder = Create(host);

        recorder.Start();
        Assert.True(recorder.IsRecording);
        Assert.Equal(2, host.Taps.Count);
        ClipCaptureTap clip = Assert.IsType<ClipCaptureTap>(host.Taps[1]);
        recorder.Start();
        Assert.Equal(2, host.Taps.Count);

        recorder.OnClipEncoded(Encoded(recorder, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, recorder.History.Count);
        int generation = recorder.History.Generation;

        recorder.Stop();
        Assert.False(recorder.IsRecording);
        Assert.IsType<ScreenshotTap>(Assert.Single(host.Taps));
        Assert.Equal(0, recorder.History.Count);
        Assert.Equal(1, host.DetachCount(clip));

        // A frame still encoding when recording stopped never lands in the cleared ring.
        recorder.OnClipEncoded(new ClipFrameRecord(TimeSpan.FromSeconds(2), 16.7, 960, 540, new byte[4], generation));
        Assert.Equal(0, recorder.History.Count);

        recorder.Stop();
        Assert.Equal(1, host.DetachCount(clip));
    }

    [Fact]
    public void An_unavailable_device_never_records()
    {
        var host = new FakeCaptureHost { SupportsClipCapture = false };
        using var recorder = Create(host);

        Assert.False(recorder.IsAvailable);
        recorder.Start();

        Assert.False(recorder.IsRecording);
        Assert.IsType<ScreenshotTap>(Assert.Single(host.Taps));
    }

    [Fact]
    public void Options_the_bundle_cannot_carry_are_refused()
    {
        var defaults = new FrameHistoryOptions();
        (FrameHistoryOptions Options, string Param)[] refused =
        {
            (defaults with { FramesPerSecond = 30, Duration = TimeSpan.FromSeconds(10) }, nameof(FrameHistoryOptions.Duration)),
            (defaults with { LongEdge = 4096 }, nameof(FrameHistoryOptions.LongEdge)),
            (defaults with { LongEdge = 0 }, nameof(FrameHistoryOptions.LongEdge)),
            (defaults with { JpegQuality = 0 }, nameof(FrameHistoryOptions.JpegQuality)),
            (defaults with { JpegQuality = 101 }, nameof(FrameHistoryOptions.JpegQuality)),
            (defaults with { MaxBytes = 0 }, nameof(FrameHistoryOptions.MaxBytes)),
            (defaults with { MaxBytes = 64 * 1024 - 1 }, nameof(FrameHistoryOptions.MaxBytes)),
            (defaults with { Duration = TimeSpan.Zero }, nameof(FrameHistoryOptions.Duration)),
            (defaults with { FramesPerSecond = 0 }, nameof(FrameHistoryOptions.FramesPerSecond)),
            (defaults with { FramesPerSecond = 61 }, nameof(FrameHistoryOptions.FramesPerSecond)),
        };

        foreach ((FrameHistoryOptions options, string param) in refused)
        {
            var host = new FakeCaptureHost();
            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => new FrameHistoryRecorder(host, options, new ManualTimeProvider()));
            Assert.Equal(param, ex.ParamName);
            Assert.Empty(host.Taps);
        }

        FrameHistoryOptions[] accepted =
        {
            defaults with { FramesPerSecond = 12, Duration = TimeSpan.FromSeconds(10) },
            defaults with { LongEdge = 16 },
            defaults with { LongEdge = 2560 },
            defaults with { JpegQuality = 1 },
            defaults with { JpegQuality = 100 },
            defaults with { MaxBytes = 64 * 1024 },
        };
        foreach (FrameHistoryOptions options in accepted)
            new FrameHistoryRecorder(new FakeCaptureHost(), options, new ManualTimeProvider()).Dispose();
    }

    [Fact]
    public void Freeze_returns_the_clip_and_stops_ticks_until_Thaw()
    {
        var time = new ManualTimeProvider();
        using var recorder = Create(new FakeCaptureHost(), time);
        recorder.Start();

        Assert.True(recorder.History.TryClaimTick(recorder.Now));
        recorder.OnClipEncoded(Encoded(recorder, recorder.Now));
        time.Advance(TimeSpan.FromMilliseconds(100));
        recorder.OnClipEncoded(Encoded(recorder, recorder.Now));

        ReportClip clip = recorder.Freeze();
        Assert.Equal(2, clip.Frames.Count);
        Assert.Equal(TimeSpan.Zero, clip.Frames[0].Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clip.Frames[1].Offset);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(recorder.History.TryClaimTick(recorder.Now));
        recorder.OnClipEncoded(Encoded(recorder, recorder.Now));
        Assert.Equal(2, recorder.History.Count);

        recorder.Thaw();
        Assert.True(recorder.History.TryClaimTick(recorder.Now));
        Assert.True(recorder.IsRecording);
    }

    [Fact]
    public void Freeze_keeps_frames_that_were_still_encoding()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        byte[] Encode(RawClipFrame frame)
        {
            entered.Set();
            release.Wait(Long);
            return new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        }

        // A wait far longer than the release delay, so a slow machine still sees the encode finish inside it.
        using var recorder = Create(new FakeCaptureHost(), encode: Encode, freezeWait: TimeSpan.FromSeconds(5));
        recorder.Start();
        Task? releaser = null;
        try
        {
            recorder.Encoder.Enqueue(Raw(recorder, recorder.Now));
            Assert.True(entered.Wait(Long));

            releaser = Task.Run(() =>
            {
                Thread.Sleep(25);
                release.Set();
            });
            ReportClip clip = recorder.Freeze();

            Assert.Single(clip.Frames);
        }
        finally
        {
            release.Set();
            releaser?.Wait(Long);
        }
    }

    [Fact]
    public void Freeze_returns_without_the_frame_when_the_encoder_outlives_the_wait()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        byte[] Encode(RawClipFrame frame)
        {
            entered.Set();
            release.Wait(Long);
            return new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        }

        using var recorder = Create(new FakeCaptureHost(), encode: Encode, freezeWait: TimeSpan.FromMilliseconds(20));
        recorder.Start();
        try
        {
            recorder.Encoder.Enqueue(Raw(recorder, recorder.Now));
            Assert.True(entered.Wait(Long));

            // The gate stays shut until Freeze has returned, so the frame is still encoding when the wait gives up.
            ReportClip clip = recorder.Freeze();

            Assert.Empty(clip.Frames);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
        }
        Assert.True(recorder.Encoder.WaitIdle(Long));
    }

    [Fact]
    public void Suspend_and_Resume_pause_ticks_without_dropping_frames()
    {
        var time = new ManualTimeProvider();
        using var recorder = Create(new FakeCaptureHost(), time);
        recorder.Start();
        Assert.True(recorder.History.TryClaimTick(recorder.Now));
        recorder.OnClipEncoded(Encoded(recorder, recorder.Now));

        recorder.Suspend();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(recorder.History.TryClaimTick(recorder.Now));
        // A frame read back before the pause still lands.
        recorder.OnClipEncoded(Encoded(recorder, recorder.Now));
        Assert.Equal(2, recorder.History.Count);
        Assert.True(recorder.IsRecording);

        recorder.Resume();
        Assert.True(recorder.History.TryClaimTick(recorder.Now));
        Assert.Equal(2, recorder.History.Count);
    }

    [Fact]
    public void CaptureScreenshotAsync_faults_with_NotSupportedException_without_swapchain_copy()
    {
        var host = new FakeCaptureHost { SupportsClipCapture = false, SupportsSwapchainCopy = false };
        using var recorder = Create(host);

        Task<ReportImage> screenshot = recorder.CaptureScreenshotAsync();

        Assert.True(screenshot.IsFaulted);
        Assert.IsType<NotSupportedException>(screenshot.Exception!.InnerException);
        // Nothing to keep copyable, so no standby tap either.
        Assert.Empty(host.Taps);
    }

    [Fact]
    public void A_device_that_can_never_copy_its_swapchain_is_unavailable_and_faults_screenshots()
    {
        // A Vulkan surface without TRANSFER_SRC. The clip flag is left at its default, so the recorder itself must
        // treat a swapchain it can never copy as no clip either, rather than waiting on a frame that never comes.
        var host = new FakeCaptureHost { SupportsSwapchainCopy = false };
        using var recorder = Create(host);

        Assert.Empty(host.Taps);
        Assert.False(recorder.IsAvailable);
        recorder.Start();
        Assert.False(recorder.IsRecording);
        Assert.Empty(host.Taps);

        Task<ReportImage> screenshot = recorder.CaptureScreenshotAsync();
        Assert.True(screenshot.IsFaulted);
        Assert.IsType<NotSupportedException>(screenshot.Exception!.InnerException);
    }

    [Fact]
    public void A_second_screenshot_request_while_one_is_pending_returns_the_same_task()
    {
        using var recorder = Create(new FakeCaptureHost());

        Task<ReportImage> first = recorder.CaptureScreenshotAsync();
        Task<ReportImage> second = recorder.CaptureScreenshotAsync();

        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
    }

    [Fact]
    public void A_tripped_clip_guard_stops_recording()
    {
        var host = new FakeCaptureHost();
        using var recorder = Create(host);
        recorder.Start();
        ClipCaptureTap clip = Assert.IsType<ClipCaptureTap>(host.Taps[1]);
        recorder.OnClipEncoded(Encoded(recorder, TimeSpan.FromSeconds(1)));

        Assert.False(recorder.ClipGuard.Run(() => throw new InvalidOperationException("device lost")));

        Assert.False(recorder.IsRecording);
        Assert.IsType<ScreenshotTap>(Assert.Single(host.Taps));
        Assert.Equal(1, host.DetachCount(clip));
        Assert.Equal(0, recorder.History.Count);

        // The fault stops recording for the recorder's lifetime.
        recorder.Start();
        Assert.False(recorder.IsRecording);
        Assert.Single(host.Taps);
    }

    [Fact]
    public void An_encode_fault_stops_recording_on_the_next_frame()
    {
        var host = new FakeCaptureHost();
        using var recorder = Create(host, encode: _ => throw new InvalidOperationException("encoder broke"));
        recorder.Start();
        ClipCaptureTap clip = Assert.IsType<ClipCaptureTap>(host.Taps[1]);

        recorder.Encoder.Enqueue(Raw(recorder, recorder.Now));
        Assert.True(recorder.Encoder.WaitIdle(Long));
        Assert.Equal(1, recorder.Encoder.Faults);

        // The worker only records the fault. The frame thread acts on it.
        Assert.True(recorder.IsRecording);
        Assert.False(recorder.ClipGuard.Tripped);

        // The clip tap's AfterSubmit reads nothing from the context.
        clip.AfterSubmit(null!);

        Assert.True(recorder.ClipGuard.Tripped);
        Assert.False(recorder.IsRecording);
        Assert.Equal(1, host.DetachCount(clip));
        Assert.IsType<ScreenshotTap>(Assert.Single(host.Taps));
    }

    [Fact]
    public void Dispose_removes_every_tap_and_faults_a_pending_screenshot()
    {
        var host = new FakeCaptureHost();
        var recorder = Create(host);
        recorder.Start();
        Task<ReportImage> pending = recorder.CaptureScreenshotAsync();

        recorder.Dispose();

        Assert.True(pending.IsFaulted);
        Assert.IsType<ObjectDisposedException>(pending.Exception!.InnerException);
        Assert.Empty(host.Taps);
        Assert.Equal(2, host.Detached.Count);
        Assert.Equal(1, host.DetachCount(host.Detached[0]));
        Assert.Equal(1, host.DetachCount(host.Detached[1]));
        Assert.False(recorder.IsRecording);

        recorder.Dispose();
        Assert.Equal(2, host.Detached.Count);
    }
}
