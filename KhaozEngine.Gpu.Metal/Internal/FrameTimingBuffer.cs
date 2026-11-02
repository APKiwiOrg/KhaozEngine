using System;
using System.Threading;
using System.Linq;

namespace KhaozEngine.Gpu.Metal.Internal;

// Append-only bounded storage. A producer reserves one slot, writes it, then publishes its ready bit.
// The background snapshot never waits for a producer and never reads a partially written struct.
internal sealed class FrameTimingBuffer<T> where T : struct
{
    readonly T[] _records;
    readonly int[] _ready;
    int _reserved;
    long _overflow;

    internal FrameTimingBuffer(int capacity)
    {
        if (capacity < 1 || capacity > 1048576) throw new ArgumentOutOfRangeException(nameof(capacity));
        _records = new T[capacity];
        _ready = new int[capacity];
    }

    internal void Add(T record)
    {
        int index = Interlocked.Increment(ref _reserved) - 1;
        if ((uint)index >= (uint)_records.Length)
        {
            Interlocked.Increment(ref _overflow);
            return;
        }
        _records[index] = record;
        Volatile.Write(ref _ready[index], 1);
    }

    internal FrameTimingBufferSnapshot<T> Snapshot()
    {
        int reserved = Math.Min(Volatile.Read(ref _reserved), _records.Length);
        var records = new T[reserved];
        int count = 0;
        for (int i = 0; i < reserved; i++)
            if (Volatile.Read(ref _ready[i]) != 0) records[count++] = _records[i];
        Array.Resize(ref records, count);
        return new(records, Interlocked.Read(ref _overflow), reserved - count);
    }
}

internal sealed record FrameTimingBufferSnapshot<T>(T[] Records, long Overflow, int IncompleteWrites) where T : struct;
internal enum FrameTimingCommandKind { Other, FrameRecording, SetupFlush, PresentBuffer, EarlyFrameRecording }
internal readonly record struct FrameTimingSubmit(long CommandBuffer, long Queue, long Timestamp, int CallbackIndex, FrameTimingCommandKind Kind);
internal readonly record struct FrameTimingCompletion(long CommandBuffer, long Queue, long Timestamp, long Status, double GpuStartTime, double GpuEndTime);
internal readonly record struct FrameTimingPresentCall(ulong DrawableId, long DrawableHandle, long CommandBuffer, long Timestamp, int CallbackIndex);
internal readonly record struct FrameTimingPresented(ulong DrawableId, long Timestamp, double PresentedTime);
internal sealed record FrameTimingSnapshot(FrameTimingBufferSnapshot<FrameTimingSubmit> Submissions,
    FrameTimingBufferSnapshot<FrameTimingCompletion> Completions, FrameTimingBufferSnapshot<FrameTimingPresentCall> PresentCalls,
    FrameTimingBufferSnapshot<FrameTimingPresented> Presented, long NativeReadFailures)
{
    // Native producers can race the final boundary. Clip by their entry timestamps after copying ready slots.
    internal FrameTimingSnapshot Window(long start, long end) => new(
        Submissions with { Records = Submissions.Records.Where(r => r.Timestamp >= start && r.Timestamp <= end).ToArray() },
        Completions with { Records = Completions.Records.Where(r => r.Timestamp >= start && r.Timestamp <= end).ToArray() },
        PresentCalls with { Records = PresentCalls.Records.Where(r => r.Timestamp >= start && r.Timestamp <= end).ToArray() },
        Presented with { Records = Presented.Records.Where(r => r.Timestamp >= start && r.Timestamp <= end).ToArray() },
        NativeReadFailures);
}
