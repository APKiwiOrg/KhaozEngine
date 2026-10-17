using System;
using System.Buffers;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Client.Internal;
using KhaozEngine.Imaging;
using KhaozEngine.Windowing;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// Records a rolling in-memory clip of the last few seconds off a window's frames and captures the trigger frame
/// as a screenshot. The clip is downscaled JPEG frames, read back without ever waiting on the GPU and encoded off
/// the frame thread. Every member is frame thread only.
/// <para>
/// A screenshot tap is registered from construction to <see cref="Dispose"/>, so the swapchain stays copyable and
/// <see cref="CaptureScreenshotAsync"/> lands in the trigger frame whether or not the clip records. The clip tap is
/// registered only between <see cref="Start"/> and <see cref="Stop"/>. A fault inside either tap is warned once
/// and turns that capture off, and the game's frame continues.
/// </para>
/// </summary>
public sealed class FrameHistoryRecorder : IDisposable
{
    /// <summary>The most frames a report clip can name.</summary>
    internal const int MaxClipFrames = 120;

    internal const int MaxFramesPerSecond = 60;

    internal const int MinLongEdge = 16;

    /// <summary>
    /// The smallest byte cap, a lower bound for sane configurations. It does not guarantee that one textured
    /// 960 px frame fits, and a frame larger than the cap is dropped. The default of 8 MB is what holds a full clip.
    /// </summary>
    internal const int MinMaxBytes = 64 * 1024;

    static readonly TimeSpan FreezeWait = TimeSpan.FromMilliseconds(250);

    readonly IFrameCaptureHost _host;
    readonly TimeProvider _time;
    readonly long _epoch;
    readonly ScreenshotTap? _screenshotTap;
    ClipCaptureTap? _clipTap;
    Exception? _encodeFault;
    bool _disposed;

    /// <summary>
    /// Creates a recorder for <paramref name="window"/> and registers its standby screenshot tap. Recording starts
    /// with <see cref="Start"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An option the report bundle cannot carry. The parameter name
    /// is the option's: <c>Duration</c> at or below zero or above 120 frames at the rate, <c>FramesPerSecond</c>
    /// outside 1 to 60, <c>LongEdge</c> outside 16 to 2560, <c>JpegQuality</c> outside 1 to 100, or
    /// <c>MaxBytes</c> below 64 KiB.</exception>
    public FrameHistoryRecorder(AppWindow window, FrameHistoryOptions options)
        : this(new AppWindowCaptureHost(window), options, TimeProvider.System)
    {
    }

    internal FrameHistoryRecorder(IFrameCaptureHost host, FrameHistoryOptions options, TimeProvider time,
        Func<RawClipFrame, byte[]>? encode = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        Validate(options);

        _host = host;
        _time = time;
        _epoch = time.GetTimestamp();
        Options = options;
        History = new FrameHistory(options);
        Pool = ArrayPool<byte>.Shared;
        ClipGuard = new TapGuard("FrameHistoryRecorder clip tap", StopRecording);
        encode ??= frame => JpegWriter.Encode(frame.Rgba.AsSpan(0, frame.Width * frame.Height * 4), frame.Width,
            frame.Height, options.JpegQuality);
        Encoder = new ClipEncodeQueue(encode, OnClipEncoded, RecordEncodeFault, Pool);

        // A device that cannot copy its swapchain has no copy-source usage to keep on, so it gets no standby tap.
        if (!host.SupportsSwapchainCopy) return;
        var screenshot = new ScreenshotTap();
        try
        {
            host.AddCaptureTap(screenshot);
        }
        catch (Exception)
        {
            Encoder.Dispose();
            throw;
        }
        _screenshotTap = screenshot;
    }

    /// <summary>
    /// True when the device can record the rolling clip: it has completion fences and can copy its swapchain.
    /// Without them <see cref="Start"/> does nothing. A screenshot needs only the swapchain copy.
    /// </summary>
    public bool IsAvailable => _host.SupportsClipCapture;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>, unless a capture fault stopped it.</summary>
    public bool IsRecording => _clipTap is not null;

    internal FrameHistoryOptions Options { get; }

    internal FrameHistory History { get; }

    /// <summary>Trips on the first clip tap fault and stops recording for the recorder's lifetime.</summary>
    internal TapGuard ClipGuard { get; }

    internal ClipEncodeQueue Encoder { get; }

    /// <summary>The pool clip frame buffers are rented from and the encode queue returns them to.</summary>
    internal ArrayPool<byte> Pool { get; }

    /// <summary>Time since construction on the recorder's clock.</summary>
    internal TimeSpan Now => _time.GetElapsedTime(_epoch);

    /// <summary>
    /// Registers the clip tap and starts recording. Does nothing while recording, on a device where
    /// <see cref="IsAvailable"/> is false, or after a capture fault stopped recording.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_clipTap is not null || !IsAvailable || ClipGuard.Tripped) return;

        var tap = new ClipCaptureTap(this);
        _clipTap = tap;
        try
        {
            _host.AddCaptureTap(tap);
        }
        catch (Exception)
        {
            _clipTap = null;
            throw;
        }
    }

    /// <summary>Unregisters the clip tap and drops every frame held, such as when a settings toggle turns it off.</summary>
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopRecording();
    }

    /// <summary>Stops capturing new frames and keeps the frames held, such as while a pause menu is open.</summary>
    public void Suspend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        History.Suspend();
    }

    /// <summary>Captures again after <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        History.Resume();
    }

    /// <summary>
    /// Returns the clip held, oldest frame first, and stops the ring until <see cref="Thaw"/>. Frames still
    /// encoding are waited for, at most 250 ms.
    /// </summary>
    public ReportClip Freeze()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Encoder.WaitIdle(FreezeWait);
        return History.Freeze();
    }

    /// <summary>Lets the ring run again after <see cref="Freeze"/>.</summary>
    public void Thaw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        History.Thaw();
    }

    /// <summary>
    /// Arms the standby screenshot tap for the current frame. Call before the frame's draws finish, such as from
    /// the game's frame callback, and the screenshot is taken from that frame. A frame that cannot be copied, the
    /// first after construction or one of a minimised window, leaves the request pending for the next one that
    /// can. A second call while one is pending returns the same task. Long edge at most 2560. PNG when it encodes
    /// under 4 MB, JPEG quality 92 otherwise.
    /// </summary>
    /// <returns>The screenshot, faulted with <see cref="NotSupportedException"/> on a device that cannot copy its
    /// swapchain, with <see cref="ObjectDisposedException"/> when the recorder is disposed first, or with
    /// <see cref="InvalidOperationException"/> after a capture fault.</returns>
    public Task<ReportImage> CaptureScreenshotAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_screenshotTap is null)
        {
            return Task.FromException<ReportImage>(new NotSupportedException(
                "This GPU device cannot copy its swapchain image, so FrameHistoryRecorder cannot take a screenshot."));
        }
        return _screenshotTap.Arm();
    }

    /// <summary>
    /// Unregisters both taps, faults a pending screenshot with <see cref="ObjectDisposedException"/> and stops the
    /// encode worker.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            StopRecording();
        }
        finally
        {
            try
            {
                if (_screenshotTap is not null) _host.RemoveCaptureTap(_screenshotTap);
            }
            finally
            {
                _screenshotTap?.Abandon();
                // No lock held here that the queue's callbacks take, so its join cannot deadlock.
                Encoder.Dispose();
            }
        }
    }

    /// <summary>The encode queue's deliver target. Runs on the encode worker.</summary>
    internal void OnClipEncoded(ClipFrameRecord frame) => History.Append(frame);

    /// <summary>
    /// Throws when the encode worker recorded a fault, so the clip tap trips its guard on the frame thread.
    /// </summary>
    internal void ThrowIfEncodeFaulted()
    {
        if (Volatile.Read(ref _encodeFault) is { } fault)
            throw new InvalidOperationException("The clip encoder faulted on its worker thread: " + fault.Message, fault);
    }

    // The queue's fault target, on the encode worker. It only records, because the guard is frame thread only.
    void RecordEncodeFault(Exception fault) => Interlocked.CompareExchange(ref _encodeFault, fault, null);

    void StopRecording()
    {
        ClipCaptureTap? tap = _clipTap;
        _clipTap = null;
        try
        {
            if (tap is not null) _host.RemoveCaptureTap(tap);
        }
        finally
        {
            History.Clear();
        }
    }

    static void Validate(FrameHistoryOptions options)
    {
        if (options.Duration <= TimeSpan.Zero)
            throw Refuse(nameof(FrameHistoryOptions.Duration), options.Duration, "Duration must be longer than zero.");

        if (options.FramesPerSecond is < 1 or > MaxFramesPerSecond)
        {
            throw Refuse(nameof(FrameHistoryOptions.FramesPerSecond), options.FramesPerSecond,
                string.Create(CultureInfo.InvariantCulture, $"FramesPerSecond must be 1 to {MaxFramesPerSecond}."));
        }

        double frames = Math.Ceiling(options.Duration.TotalSeconds * options.FramesPerSecond);
        if (frames > MaxClipFrames)
        {
            throw Refuse(nameof(FrameHistoryOptions.Duration), options.Duration, string.Create(
                CultureInfo.InvariantCulture,
                $"Duration at {options.FramesPerSecond} frames per second holds {frames} frames and a report clip holds at most {MaxClipFrames}."));
        }

        if (options.LongEdge is < MinLongEdge or > ScreenshotEncoder.MaxLongEdge)
        {
            throw Refuse(nameof(FrameHistoryOptions.LongEdge), options.LongEdge, string.Create(
                CultureInfo.InvariantCulture, $"LongEdge must be {MinLongEdge} to {ScreenshotEncoder.MaxLongEdge} pixels."));
        }

        if (options.JpegQuality is < 1 or > 100)
            throw Refuse(nameof(FrameHistoryOptions.JpegQuality), options.JpegQuality, "JpegQuality must be 1 to 100.");

        if (options.MaxBytes < MinMaxBytes)
        {
            throw Refuse(nameof(FrameHistoryOptions.MaxBytes), options.MaxBytes, string.Create(
                CultureInfo.InvariantCulture, $"MaxBytes must be at least {MinMaxBytes}."));
        }
    }

    static ArgumentOutOfRangeException Refuse(string option, object value, string message) => new(option, value, message);
}
