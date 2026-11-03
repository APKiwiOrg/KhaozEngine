using System;
using System.Diagnostics;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal;

// Private archive instrumentation. Every native read and handler attachment is behind Sampling.
internal static class MetalFrameTimingProbe
{
    internal static volatile bool Sampling;
    static FrameTimingCapture? _capture;
    // Kept after stop so a late completion can return its resources without resolving counters.
    internal static MetalPassTimingCapture? PassTiming;

    internal static void Start(FrameTimingCapture capture)
    {
        if (Interlocked.CompareExchange(ref _capture, capture, null) is not null)
            throw new InvalidOperationException("Only one frame diagnostics capture may sample at a time.");
        Volatile.Write(ref PassTiming, capture.PassTiming);
        Sampling = true;
    }

    internal static IntPtr BeginEncoder(MetalEncoderKind kind, IntPtr commandBuffer, IntPtr descriptor)
    {
        var capture = Volatile.Read(ref _capture);
        // A stop racing the sink's Sampling check still opens the encoder, untimed, through the same native path.
        // PassTiming outlives the capture, and the sink only calls here once it is set.
        var passes = capture?.PassTiming ?? Volatile.Read(ref PassTiming)!;
        return passes.Begin(kind, commandBuffer, descriptor, capture?.CurrentCallback ?? -1, capture is not null && Sampling);
    }

    internal static void Stop(FrameTimingCapture capture)
    {
        if (!ReferenceEquals(Volatile.Read(ref _capture), capture)) return;
        Sampling = false;
        Interlocked.CompareExchange(ref _capture, null, capture);
    }

    internal static void PrefixCommitted(IGpuCommandList commands)
    {
        if (Sampling && Volatile.Read(ref _capture) is { } capture) capture.PrefixCommitted(commands);
    }

    internal static void Submit(IntPtr commandBuffer, IntPtr queue, FrameTimingCommandKind kind, IGpuCommandList? commands)
    {
        if (!Sampling || Volatile.Read(ref _capture) is not { } capture) return;
        capture.Submissions.Add(new(commandBuffer.ToInt64(), queue.ToInt64(), Stopwatch.GetTimestamp(),
            capture.CurrentCallback, capture.Kind(commands, kind)));
    }

    internal static void ObserveCompletion<T>(IntPtr commandBuffer, IntPtr queue, long status, ref T reader)
        where T : struct, IMetalFrameTimingReader
    {
        if (!Sampling || Volatile.Read(ref _capture) is not { } capture) return;
        long timestamp = Stopwatch.GetTimestamp();
        try
        {
            reader.ReadGpuTimes(commandBuffer, out double start, out double end);
            capture.Completions.Add(new(commandBuffer.ToInt64(), queue.ToInt64(), timestamp, status, start, end));
        }
        catch { Interlocked.Increment(ref capture.NativeReadFailures); }
    }

    internal static void ObservePresent<T>(IntPtr commandBuffer, IntPtr drawable, ref T reader)
        where T : struct, IMetalFrameTimingReader
    {
        if (!Sampling || Volatile.Read(ref _capture) is not { } capture) return;
        try
        {
            ulong id = reader.DrawableId(drawable);
            if (!reader.AttachPresentedHandler(drawable)) Interlocked.Increment(ref capture.NativeReadFailures);
            capture.PresentCalls.Add(new(id, drawable.ToInt64(), commandBuffer.ToInt64(), Stopwatch.GetTimestamp(), capture.CurrentCallback));
        }
        catch { Interlocked.Increment(ref capture.NativeReadFailures); }
    }

    internal static void ObservePresented<T>(IntPtr drawable, ref T reader) where T : struct, IMetalFrameTimingReader
    {
        if (!Sampling || Volatile.Read(ref _capture) is not { } capture) return;
        long timestamp = Stopwatch.GetTimestamp();
        try { capture.Presented.Add(new(reader.DrawableId(drawable), timestamp, reader.PresentedTime(drawable))); }
        catch { Interlocked.Increment(ref capture.NativeReadFailures); }
    }
}

internal interface IMetalFrameTimingReader
{
    void ReadGpuTimes(IntPtr commandBuffer, out double start, out double end);
    ulong DrawableId(IntPtr drawable);
    double PresentedTime(IntPtr drawable);
    bool AttachPresentedHandler(IntPtr drawable);
}
