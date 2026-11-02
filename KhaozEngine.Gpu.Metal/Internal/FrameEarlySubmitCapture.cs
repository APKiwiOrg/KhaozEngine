using System;
using System.Linq;

namespace KhaozEngine.Gpu.Metal.Internal;

internal readonly record struct FrameEarlySubmitBlock(int StartCallback, int EndCallbackExclusive,
    long StartTimestamp, long EndTimestamp, bool Enabled);
internal sealed record FrameEarlySubmitSnapshot(int BlockCallbacks, FrameEarlySubmitBlock[] Schedule, bool[] AppliedPerCallback);

/// <summary>Private recorder experiment. Blocks alternate off then on, on the frame thread only.</summary>
internal sealed class FrameEarlySubmitCapture(int capacity)
{
    internal const int BlockCallbacks = 72;
    readonly FrameEarlySubmitBlock[] _blocks = new FrameEarlySubmitBlock[capacity / BlockCallbacks + 1];
    readonly bool[] _applied = new bool[capacity];
    int _count;
    bool _stopped;
    internal bool Enabled { get; private set; }

    internal void Boundary(int callback, long timestamp)
    {
        if (_stopped || callback % BlockCallbacks != 0) return;
        Close(callback, timestamp);
        Enabled = callback / BlockCallbacks % 2 == 1;
        _blocks[_count++] = new(callback, callback, timestamp, timestamp, Enabled);
    }

    internal void Applied(int callback)
    {
        if ((uint)callback < (uint)_applied.Length) _applied[callback] = true;
    }

    internal void End(int callback, long timestamp) { Close(callback, timestamp); Stop(); }
    internal void Stop() { Enabled = false; _stopped = true; }
    void Close(int callback, long timestamp)
    {
        if (_count > 0) _blocks[_count - 1] = _blocks[_count - 1] with { EndCallbackExclusive = callback, EndTimestamp = timestamp };
    }
    internal FrameEarlySubmitSnapshot Snapshot(int count) => new(BlockCallbacks, _blocks.Take(_count).ToArray(), _applied.Take(count).ToArray());
}
