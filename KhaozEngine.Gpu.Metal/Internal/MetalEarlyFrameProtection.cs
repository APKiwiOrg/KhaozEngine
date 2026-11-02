using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>An early buffer owns the recording's storage until its final buffer gets the ordinary timeline value.
/// Abandonment quarantines the slot instead of inventing a signal, encoder, wait or completion ordering promise.</summary>
internal sealed class MetalEarlyFrameProtection(int depth)
{
    readonly ulong[] _recordings = new ulong[depth];
    readonly ulong[] _pending = new ulong[depth];
    internal void RequireNoPending()
    {
        foreach (ulong recording in _pending)
            if (recording != 0) throw new InvalidOperationException("An early frame must finish submission before off-timeline writes, maps or foreign submissions.");
    }
    internal bool Pending(int segment) => _pending[segment] != 0;
    internal ulong Recording(int segment) => _recordings[segment];
    internal void Begin(int segment, ulong recording)
    {
        if (Pending(segment)) throw new InvalidOperationException("An early frame has no final submission. Its uniform slot is quarantined until device teardown.");
        _recordings[segment] = recording;
    }
    internal void Protect(int segment) => _pending[segment] = _recordings[segment];
    internal void Submitted(int segment) => _pending[segment] = 0;
    internal void RequireWritable(int segment, ulong readRecording)
    {
        if (readRecording != 0 && _pending[segment] == readRecording)
            throw new InvalidOperationException("The final part of an early frame cannot overwrite a uniform buffer read by its committed prefix.");
    }
}
