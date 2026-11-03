using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalCommandList
{
    internal MetalEarlyFrameSubmission? EarlySubmission { get; set; }
    MetalRecordedReads _recordedReads = new();
    ulong _prefixFrame;
    bool _drawableResolved;
    MetalSwapchainFramebuffer? _deferredDrawable;
    GpuAttachmentStoreActions _deferredStores;

    internal void ProtectPrefix() => _prefixFrame = _rings.CpuReads.Protect(_segment, _recordedReads);

    // The outgoing framebuffer already owes this pass end. No render encoder is split in two.
    // A compute or blit encoder left open by preceding work is also ended before the next framebuffer can draw.
    void SplitBeforeDrawable(bool flushOutgoing = true)
    {
        if (_drawableResolved) return;
        _drawableResolved = true;
        if (EarlySubmission is null) return;
        try
        {
            if (flushOutgoing) _passes.EndPass();
            _encoders.EnsureNoEncoder();
            if (flushOutgoing && _passes.HasPendingClears) return; // A refused encoder has not consumed its clear.
            IntPtr prefix = _commandBuffer;
            if (!EarlySubmission.TryCommit(this, prefix)) return;
            _commandBuffer = IntPtr.Zero;
            _encoders.ForgetCommandBuffer();
            _uncommitted.Released();
            _buffers.Release(prefix);
            IntPtr tail = _buffers.Acquire();
            if (tail == IntPtr.Zero) throw new InvalidOperationException("Metal could not allocate the final buffer of a split frame.");
            _commandBuffer = tail;
            _uncommitted.Acquired();
            _encoders.BeginRecording(tail);
            foreach (var timestamps in _frameTimestampPools) timestamps.ContinueRecording(this, _timestampRecordingId, tail);
        }
        catch
        {
            AbandonFailedRecording();
            _recording = false;
            throw;
        }
    }

    internal void ResolveDeferredDrawable()
    {
        if (_deferredDrawable is not { } source) return;
        _deferredDrawable = null;
        SplitBeforeDrawable(flushOutgoing: false);
        _passes.ResolveFramebuffer(((IMetalBoundFramebufferSource)source).AsBound);
    }

    void RecordBufferAccess(MetalBuffer buffer, ulong offset, ulong length)
    {
        if (_prefixFrame != 0 || buffer.Handle.IsNull) return;
        _recordedReads.Add(buffer.CpuResource, offset, length);
    }
}
