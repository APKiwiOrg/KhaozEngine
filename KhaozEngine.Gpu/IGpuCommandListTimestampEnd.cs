using System;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Optional completed-frame timestamp support. An <see cref="IGpuCommandList"/> without this interface
    /// cannot prove completed-frame GPU time, because its End can emit work after the last ordinary marker.
    /// </summary>
    public interface IGpuCommandListTimestampEnd
    {
        /// <summary>
        /// Arm the final marker and batch end for the current recording without submitting, flushing or waiting.
        /// The list must be recording, and <paramref name="pool"/> must belong to its device and have a batch
        /// begun on this list in this recording. Every earlier index must already be written exactly once.
        /// <paramref name="finalIndex"/> must be the batch count minus one and must still be unwritten.
        /// Validation completes before any tail is stored. Only one tail may be armed per recording.
        /// <para>
        /// Once armed, manual WriteTimestamp and EndTimestamps for this pool on this list are refused.
        /// The ordinary End writes the final marker, then ends the same batch, after all GPU finalization
        /// (including pending clears and layout or memory barriers) and before the native recording is sealed.
        /// The tail adds no submission, flush, wait or per-frame allocation. Without a tail, End is unchanged.
        /// </para>
        /// <para>
        /// A failed End or abandoned recording clears the list's tail without resetting or reusing the pool.
        /// Its Recording or Pending batch remains quarantined by the caller through teardown. This is not a
        /// cancellation API. Device loss follows the backend's sealing policy and cannot supply valid timing.
        /// Keep the pool and its submitted completion fence alive under <see cref="IGpuTimestampPool"/>'s
        /// resolution contract.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentNullException">The pool is null.</exception>
        /// <exception cref="ArgumentException">The pool belongs to another device or backend.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The index is not the batch's last index.</exception>
        /// <exception cref="InvalidOperationException">The list or batch is not recording, an earlier index
        /// is missing, the final index is already written, or a tail is already armed.</exception>
        void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex);
    }
}
