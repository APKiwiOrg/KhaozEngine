using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>An early buffer owns the recording's storage until its final buffer gets the ordinary timeline value.
/// Abandonment quarantines the slot instead of inventing a signal, encoder, wait or completion ordering promise.
/// Work that a real frame does while its tail is open is counted rather than refused: SpriteBatch uploads its quads
/// through the device after Render3D's final blit has bound the drawable, so a refusal would fail every split frame.
/// Ring-backed device writes already defer the pending segment in MetalRingAllocator, and an ordinary buffer write has
/// the same in-flight rules it has against any earlier frame.</summary>
internal sealed class MetalEarlyFrameProtection(int depth)
{
    readonly ulong[] _recordings = new ulong[depth];
    readonly ulong[] _pending = new ulong[depth];

    /// <summary>Device-level writes, maps, waits and foreign submissions while an early frame's tail is open.</summary>
    internal void NoteDeviceWork()
    {
        foreach (ulong recording in _pending)
            if (recording != 0) { MetalFrameTimingProbe.TailDeviceWork(); return; }
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

    /// <summary>A tail write into a uniform buffer the committed prefix read. Unsplit, the prefix would have read the
    /// new bytes, so the frame is marked contaminated for the analysis rather than failed.</summary>
    internal void NoteUniformWrite(int segment, ulong readRecording)
    {
        if (readRecording != 0 && _pending[segment] == readRecording) MetalFrameTimingProbe.PrefixUniformRewrite();
    }
}
