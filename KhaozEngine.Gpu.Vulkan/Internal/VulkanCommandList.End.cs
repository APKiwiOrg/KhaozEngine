using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    internal sealed partial class VulkanCommandList
    {
        /// <inheritdoc/>
        /// <remarks>
        /// <c>vkEndCommandBuffer</c>, and the seal that makes the record submittable.
        /// <para>
        /// THE RENDER PASS INSTANCE CLOSES FIRST (V-A4), and if the pass recorded clears that no draw consumed,
        /// they are flushed through a begin and end pair on the way out (V-A3). That is the clear-only case, which
        /// the incumbent forced at two sites and a golden depends on. Sealing a buffer with an instance still open
        /// is a call the driver refuses.
        /// </para>
        /// <para>
        /// THE RESTING-LAYOUT RESTORE GOES NEXT (V-F7), after that and before the native end. Every texture has a
        /// canonical resting layout assigned at creation, a list tracks its transitions LOCALLY, and <c>End</c>
        /// restores every texture it touched before sealing, which is what makes two lists composable in any
        /// submit order. It has to be before the native end: a barrier recorded after <c>vkEndCommandBuffer</c> is
        /// a call against a sealed buffer.
        /// </para>
        /// <para>
        /// AN <c>End</c> WITHOUT A <c>Begin</c> IS REFUSED, including a second <c>End</c> on an already sealed
        /// list, and the message says which of the two happened.
        /// </para>
        /// <para>
        /// A FAILED END ABANDONS THE RECORDING, including failures during rendering closure, layout restore,
        /// release, timestamps or native seal. The original exception escapes, Submit and another End refuse,
        /// and Begin advances to the next slot. The abandoned buffer is not recorded into again until Advance
        /// resets its pool on wrap. Timestamp pools stay quarantined through teardown without reset or reuse.
        /// </para>
        /// </remarks>
        public void End()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_recording)
            {
                throw new InvalidOperationException(_sealedSlot == NoSeal
                    ? "End was called on a native Vulkan command list that is not recording. Call Begin first."
                    : "End was called twice on a native Vulkan command list. The recording is already sealed and "
                        + "ready to submit, and a second vkEndCommandBuffer on a buffer in the executable state "
                        + "is a call the driver refuses.");
            }

            try
            {
                // THE INSTANCE CLOSES BEFORE ANYTHING ELSE, including the clear-only flush a pass with no draw owes.
                _rendering?.EndRendering(CurrentBuffer);

                // EVERY TOUCHED TEXTURE BACK TO REST (V-F7), as ONE batched barrier, after the instance closed and
                // before the native end. The remarks above say why that order is fixed rather than incidental.
                _layouts?.RestoreResting(CurrentBuffer);

                int slot = _ring.Slot;
                _ring.EndRecording(slot, _timestampEndPool, _timestampEndIndex);

                _sealedSlot = slot;
            }
            finally
            {
                // Begin cleared the seal. A failed finalization never publishes one and cannot be retried.
                _recording = false;
                if (_sealedSlot == NoSeal) _boundSwapchain = false;
                ClearTimestampEnd();
            }
        }
    }
}
