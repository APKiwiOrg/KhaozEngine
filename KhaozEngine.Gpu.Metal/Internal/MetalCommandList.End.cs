using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    internal sealed partial class MetalCommandList
    {
        /// <inheritdoc/>
        /// <remarks>
        /// Closes any open encoder and seals the record for submission.
        /// Failure abandons the buffer without committing it. Submit and another End refuse it, and Begin
        /// takes a fresh buffer. The original exception is rethrown with its stack preserved. Secondary
        /// descriptor, encoder, boundary bookkeeping and buffer cleanup failures are discarded.
        /// <para>
        /// THE ENCODER CLOSES FIRST AND UNCONDITIONALLY. Committing a command buffer with an encoder still open
        /// is a call Metal refuses, and there is no equivalent of <c>vkEndCommandBuffer</c> to seal against: the
        /// seal here is this backend's own bookkeeping, so the native obligation is exactly one
        /// <c>-endEncoding</c> and no more.
        /// </para>
        /// <para>
        /// AND THE CLEAR-ONLY FLUSH COMES FIRST (M-A3), which is the SECOND of the incumbent's two forcing sites
        /// (the other is a framebuffer change). A framebuffer plus clears plus an <c>End</c> with no draw must
        /// still CLEAR, and a golden depends on it. <see cref="MetalRenderPassSchedule.EndPass"/> is the one
        /// helper that decides it: a begin CONSUMES the pending array, so a pending clear still sitting there is
        /// itself the proof that no draw came, and no second flag has to be kept in step.
        /// </para>
        /// <para>
        /// THE UNCONDITIONAL <c>EnsureNoEncoder</c> STAYS BEHIND IT rather than being replaced by it, because the
        /// two answer different questions. The flush closes a RENDER pass. What must not survive an <c>End</c> is
        /// an encoder of ANY kind, including the blit encoder a record-time upload left open, and that is the
        /// native obligation a committable command buffer has.
        /// </para>
        /// <para>
        /// AN <c>End</c> WITHOUT A <c>Begin</c> IS REFUSED, including a second <c>End</c> on an already sealed
        /// list, and the message says which of the two happened.
        /// </para>
        /// </remarks>
        public void End()
        {
            // M-N5's one pool for this member. See the type remarks.
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_recording)
            {
                throw new InvalidOperationException(_sealed
                    ? "End was called twice on a native Metal command list. The recording is already sealed and "
                        + "ready to submit."
                    : "End was called on a native Metal command list that is not recording. Call Begin first.");
            }

            try
            {
                _passes.EndPass();
                _encoders.EnsureNoEncoder();
                EmitTimestampEnd();
                _sealed = true;
            }
            catch
            {
                AbandonFailedRecording();
                throw;
            }
            finally
            {
                _recording = false;
                ClearTimestampEnd();
            }
        }

        void AbandonFailedRecording()
        {
            _sealed = false;
            _boundSwapchain = false;
            _segment = -1;
            _passes.Reset();
            // Each owner drops its handle before the native release. Cleanup must not replace End's exception
            // or retry an encoder that may already have ended. The buffer is never handed to commit.
            try { _encoders.EnsureNoEncoder(); }
            catch { }
            try { ReleaseHeldBuffer(); }
            catch { }
        }
    }
}
