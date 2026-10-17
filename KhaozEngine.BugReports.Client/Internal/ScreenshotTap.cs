using System;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>
/// The standby screenshot tap. The recorder registers it at construction and keeps it until dispose, so the
/// swapchain stays copyable whether or not the clip records. It does nothing per frame until <see cref="Arm"/>.
/// The armed frame's <see cref="Record"/> downscales the swapchain copy to at most
/// <see cref="ScreenshotEncoder.MaxLongEdge"/>, and its <see cref="AfterSubmit"/> reads it back once with the
/// blocking <see cref="GpuReadback.ToRgba"/> and encodes it off the frame thread. A frame whose image is not
/// copyable leaves the arm pending for the next one. Frame thread only.
/// </summary>
internal sealed class ScreenshotTap : IFrameCaptureTap
{
    readonly TapGuard _guard;
    TaskCompletionSource<ReportImage>? _pending;
    Exception? _fault;
    GpuScaledCopy? _scaledCopy;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    int _width;
    int _height;
    bool _detached;

    internal ScreenshotTap()
    {
        _guard = new TapGuard("FrameHistoryRecorder screenshot tap", FaultPending, (message, fault) =>
        {
            _fault = fault;
            Log.Warn(message, fault);
        });
    }

    /// <summary>
    /// The pending screenshot, or a new one taken in the next copyable frame. A faulted or detached tap returns a
    /// faulted task at once, so a request never hangs on a tap that will not run.
    /// </summary>
    internal Task<ReportImage> Arm()
    {
        if (_detached)
            return Task.FromException<ReportImage>(Disposed());
        if (_guard.Tripped)
            return Task.FromException<ReportImage>(
                new InvalidOperationException("The screenshot capture faulted earlier and stopped.", _fault));

        _pending ??= new TaskCompletionSource<ReportImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _pending.Task;
    }

    public void Record(FrameCaptureContext context) => _guard.Run(() => RecordCore(context));

    public void AfterSubmit(FrameCaptureContext context) => _guard.Run(() => AfterSubmitCore(context));

    public void Detach(Action<IDisposable> retire)
    {
        ArgumentNullException.ThrowIfNull(retire);
        _detached = true;
        Abandon();
        if (_framebuffer is not null) retire(_framebuffer);
        if (_target is not null) retire(_target);
        if (_scaledCopy is not null) retire(_scaledCopy);
        _framebuffer = null;
        _target = null;
        _scaledCopy = null;
    }

    /// <summary>Faults a pending screenshot with <see cref="ObjectDisposedException"/>. Safe to repeat.</summary>
    internal void Abandon()
    {
        TaskCompletionSource<ReportImage>? source = _pending;
        _pending = null;
        source?.TrySetException(Disposed());
    }

    void RecordCore(FrameCaptureContext context)
    {
        if (_pending is null || _target is not null) return;
        IGpuTexture? image = context.AcquireSwapchainImage();
        if (image is null) return;

        (int width, int height) = CaptureSize.Fit((int)image.Width, (int)image.Height, ScreenshotEncoder.MaxLongEdge);
        IGpuResourceFactory factory = context.Device.Factory;
        _scaledCopy ??= new GpuScaledCopy(context.Device);
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = factory.CreateFramebuffer(null, _target);
        _width = width;
        _height = height;
        _scaledCopy.Record(context.Commands, image, _framebuffer);
    }

    void AfterSubmitCore(FrameCaptureContext context)
    {
        if (_target is not { } target) return;

        byte[] rgba;
        try
        {
            // The one blocking readback. Legal here because nothing records after the frame's submit.
            rgba = GpuReadback.ToRgba(context.Device, target, _width, _height);
        }
        finally
        {
            if (_framebuffer is not null) context.Retire(_framebuffer);
            context.Retire(target);
            _framebuffer = null;
            _target = null;
        }

        TaskCompletionSource<ReportImage>? source = _pending;
        _pending = null;
        if (source is null) return;

        int width = _width;
        int height = _height;
        _ = Task.Run(() =>
        {
            try
            {
                source.TrySetResult(ScreenshotEncoder.Encode(rgba, width, height));
            }
            catch (Exception ex)
            {
                source.TrySetException(ex);
            }
        });
    }

    void FaultPending()
    {
        TaskCompletionSource<ReportImage>? source = _pending;
        _pending = null;
        source?.TrySetException(new InvalidOperationException("The screenshot capture faulted.", _fault));
    }

    static ObjectDisposedException Disposed()
        => new(nameof(FrameHistoryRecorder), "The recorder was disposed before the screenshot was taken.");
}
