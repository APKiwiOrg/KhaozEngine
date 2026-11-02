using System;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed class FrameTimingCapture : IDisposable
{
    readonly IGpuCommandList? _frameCommands;
    int _thread;
    internal int CallbackIndex;
    internal readonly FrameTimingBuffer<FrameTimingSubmit> Submissions;
    internal readonly FrameTimingBuffer<FrameTimingCompletion> Completions;
    internal readonly FrameTimingBuffer<FrameTimingPresentCall> PresentCalls;
    internal readonly FrameTimingBuffer<FrameTimingPresented> Presented;
    internal long NativeReadFailures;
    internal readonly MetalPassTimingCapture? PassTiming;

    internal FrameTimingCapture(IGpuCommandList? frameCommands, int capacity = 65536, MetalPassTimingCapture? passTiming = null)
    {
        _frameCommands = frameCommands;
        PassTiming = passTiming;
        Submissions = new(capacity);
        Completions = new(capacity);
        PresentCalls = new(Math.Min(capacity, 8192));
        Presented = new(Math.Min(capacity, 8192));
    }

    internal int CurrentCallback => Environment.CurrentManagedThreadId == _thread ? CallbackIndex : -1;
    internal FrameTimingCommandKind Kind(IGpuCommandList? commands, FrameTimingCommandKind fallback) =>
        commands is not null && ReferenceEquals(commands, _frameCommands) ? FrameTimingCommandKind.FrameRecording : fallback;

    internal void Start()
    {
        _thread = Environment.CurrentManagedThreadId;
        MetalFrameTimingProbe.Start(this);
    }

    internal void Stop() => MetalFrameTimingProbe.Stop(this);
    internal FrameTimingSnapshot Snapshot() => new(Submissions.Snapshot(), Completions.Snapshot(),
        PresentCalls.Snapshot(), Presented.Snapshot(), Interlocked.Read(ref NativeReadFailures));
    public void Dispose() { Stop(); PassTiming?.Dispose(); }
}
