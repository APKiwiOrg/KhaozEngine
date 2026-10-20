using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>
/// The rolling clip's frame tap, registered while the recorder records. On each claimed tick it downscales the
/// frame's swapchain copy to capture size and copies it into a fenced readback ring, and after the submit it reads
/// every completed slot and hands the pixels to the encode queue. It never waits on the GPU: a tick with no free
/// slot is dropped. A resize starts a new ring at the new size and keeps the old one until its slots are read, so
/// frames reach the encoder in capture order. Every callback runs inside the recorder's clip guard, and a fault
/// stops recording. Frame thread only.
/// </summary>
internal sealed class ClipCaptureTap : IFrameCaptureTap
{
    readonly FrameHistoryRecorder _recorder;
    readonly FrameHistory _history;
    readonly ArrayPool<byte> _pool;
    readonly int _longEdge;
    readonly List<GpuReadbackRing> _retiring = new();
    readonly Dictionary<long, Pending> _pending = new();
    GpuScaledCopy? _scaledCopy;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    GpuReadbackRing? _ring;
    byte[]? _spare;
    long _nextTag;

    readonly record struct Pending(TimeSpan CapturedAt, double RenderMilliseconds, int Width, int Height, int Generation);

    internal ClipCaptureTap(FrameHistoryRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _recorder = recorder;
        _history = recorder.History;
        _pool = recorder.Pool;
        _longEdge = recorder.Options.LongEdge;
    }

    public void Record(FrameCaptureContext context) => _recorder.ClipGuard.Run(() => RecordCore(context));

    /// <summary>Reads completed slots. Uses nothing from <paramref name="context"/>.</summary>
    public void AfterSubmit(FrameCaptureContext context) => _recorder.ClipGuard.Run(AfterSubmitCore);

    public void Detach(Action<IDisposable> retire)
    {
        ArgumentNullException.ThrowIfNull(retire);
        _pending.Clear();
        if (_spare is not null) _pool.Return(_spare);
        _spare = null;
        if (_framebuffer is not null) retire(_framebuffer);
        if (_target is not null) retire(_target);
        foreach (GpuReadbackRing ring in _retiring) retire(ring);
        if (_ring is not null) retire(_ring);
        if (_scaledCopy is not null) retire(_scaledCopy);
        _retiring.Clear();
        _framebuffer = null;
        _target = null;
        _ring = null;
        _scaledCopy = null;
    }

    void RecordCore(FrameCaptureContext context)
    {
        _recorder.ThrowIfEncodeFaulted();
        TimeSpan now = _recorder.Now;
        if (!_history.TryClaimTick(now)) return;
        IGpuTexture? image = context.AcquireSwapchainImage();
        if (image is null) return;

        (int width, int height) = CaptureSize.Fit((int)image.Width, (int)image.Height, _longEdge);
        EnsureSize(context, width, height);
        // Every slot in flight: drop this tick rather than wait.
        if (!_ring.HasFreeSlot) return;

        _scaledCopy ??= new GpuScaledCopy(context.Device);
        _scaledCopy.Record(context.Commands, image, _framebuffer, context.Retire);
        long tag = _nextTag++;
        if (!_ring.TryRecordCopy(context.Commands, _target, tag, out var fence)) return;
        // Straight away: a claimed slot whose fence misses the submit stays pending for good.
        context.SignalOnSubmit(fence);
        _pending[tag] = new Pending(now, context.FrameMilliseconds, width, height, _history.Generation);
    }

    [MemberNotNull(nameof(_ring), nameof(_target), nameof(_framebuffer))]
    void EnsureSize(FrameCaptureContext context, int width, int height)
    {
        if (_ring is not null && _target is not null && _framebuffer is not null
            && _ring.Width == width && _ring.Height == height)
        {
            return;
        }

        if (_framebuffer is not null) context.Retire(_framebuffer);
        if (_target is not null) context.Retire(_target);
        if (_ring is not null) _retiring.Add(_ring);
        _framebuffer = null;
        _target = null;
        _ring = null;

        IGpuResourceFactory factory = context.Device.Factory;
        _target = factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));
        _framebuffer = factory.CreateFramebuffer(null, _target);
        _ring = new GpuReadbackRing(context.Device, width, height);
    }

    void AfterSubmitCore()
    {
        _recorder.ThrowIfEncodeFaulted();

        // Oldest ring first, and a ring still waiting on the GPU holds back every newer one.
        bool drained = true;
        foreach (GpuReadbackRing ring in _retiring)
        {
            if (!Drain(ring))
            {
                drained = false;
                break;
            }
        }
        if (drained && _ring is not null) Drain(_ring);

        for (int i = _retiring.Count - 1; i >= 0; i--)
        {
            if (_retiring[i].PendingCount > 0) continue;
            _retiring[i].Dispose();
            _retiring.RemoveAt(i);
        }
    }

    /// <summary>Enqueues every completed slot of <paramref name="ring"/>. True when none is left pending.</summary>
    bool Drain(GpuReadbackRing ring)
    {
        int bytes = ring.Width * ring.Height * 4;
        while (ring.PendingCount > 0)
        {
            byte[] buffer = TakeBuffer(bytes);
            bool read;
            long tag;
            try
            {
                read = ring.TryReadCompleted(buffer, out tag);
            }
            catch (Exception)
            {
                _spare = buffer;
                throw;
            }

            if (!read)
            {
                _spare = buffer;
                return false;
            }

            if (_pending.Remove(tag, out Pending frame))
            {
                _recorder.Encoder.Enqueue(new RawClipFrame(buffer, frame.Width, frame.Height, frame.CapturedAt,
                    frame.RenderMilliseconds, frame.Generation));
            }
            else
            {
                _spare = buffer;
            }
        }
        return true;
    }

    // One buffer kept across frames, so polling a slot that has not completed rents nothing.
    byte[] TakeBuffer(int bytes)
    {
        byte[]? spare = _spare;
        _spare = null;
        if (spare is not null)
        {
            if (spare.Length >= bytes) return spare;
            _pool.Return(spare);
        }
        return _pool.Rent(bytes);
    }
}
