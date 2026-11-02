using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalCommandList
{
    internal MetalEarlyFrameSubmit? EarlySubmit { get; set; }
    bool _earlyProtected;
    internal bool HasEarlyPrefix => _earlyProtected;
    bool _timestampBatchRecorded;
    ulong _earlyRecordingEpoch;

    internal void ProtectEarlyFrame()
    {
        _rings.EarlyFrames.Protect(_segment);
        _earlyProtected = true;
    }

    /// <summary>Called only at a framebuffer change, before resolving an unacquired drawable. EndPass is the same
    /// clear flush and encoder end the change already owes. No pass or encoder is split in two.</summary>
    internal void SplitBeforeDrawableAcquire()
    {
        if (_earlyProtected || _timestampBatchRecorded || EarlySubmit is null || !MetalFrameTimingProbe.ShouldEarlySubmit(this)) return;
        RequireRecording("Splitting the frame before drawable acquisition");
        if (_encoders.Epoch == _earlyRecordingEpoch && !_passes.HasPendingClears) return;
        // SetFramebuffer ends a render pass, but can leave a blit or compute encoder open. Splitting those could
        // create an extra encoder if more copies or dispatches follow the bind. Keep that shape unsplit.
        if (_encoders.Open is MetalEncoderKind.Blit or MetalEncoderKind.Compute) return;
        try
        {
            _passes.EndPass();
            _encoders.EnsureNoEncoder();
            // A refused clear-only encoder still owes a clear. Leave it on the ordinary single-buffer path.
            if (_passes.HasPendingClears) return;
            IntPtr prefix = _commandBuffer;
            if (!EarlySubmit.TryCommit(this, prefix)) return;
            MetalFrameTimingProbe.EarlySubmitted(this);
            _commandBuffer = IntPtr.Zero;
            _encoders.ForgetCommandBuffer();
            _uncommitted.Released();
            _buffers.Release(prefix);
            // No new Begin: the segment, arena, pipeline records and outstanding pass-label stack belong to this frame.
            IntPtr tail = _buffers.Acquire();
            if (tail == IntPtr.Zero) throw new InvalidOperationException("Metal could not allocate the final buffer of an early frame.");
            _commandBuffer = tail;
            _uncommitted.Acquired();
            _encoders.BeginRecording(tail);
            MetalFrameTimingProbe.ContinueFrame(this, prefix, tail);
        }
        catch
        {
            AbandonFailedRecording();
            _recording = false;
            throw;
        }
    }
}
