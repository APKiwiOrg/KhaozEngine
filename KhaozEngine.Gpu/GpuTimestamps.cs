using System;

namespace KhaozEngine.Gpu
{
    /// <summary>The outcome of a non-blocking timestamp batch read.</summary>
    public enum GpuTimestampStatus
    {
        /// <summary>The submission or its native query data is not ready. No ticks or period are available.</summary>
        Pending,
        /// <summary>The complete batch has usable elapsed ticks and a finite positive tick period.</summary>
        Ready,
        /// <summary>The completed batch is unusable, for example after disjoint counters or cumulative overflow.</summary>
        Invalid,
    }

    /// <summary>
    /// A fixed-capacity, device-owned timestamp resource. Allocate a fixed caller-owned ring, one pool per
    /// retained frame. If every pool is pending, record no timestamps that frame. Use a pool from one thread.
    /// <para>
    /// Begin, write and end through <see cref="IGpuCommandList.BeginTimestamps"/>,
    /// <see cref="IGpuCommandList.WriteTimestamp"/> and <see cref="IGpuCommandList.EndTimestamps"/> in the
    /// caller's open list. They encode work only and never submit. Each batch writes increasing indices from
    /// zero exactly once. One unresolved batch may occupy a pool. Only a consumed Ready or Invalid result
    /// permits the next begin, which clears the prior result.
    /// </para>
    /// <para>
    /// Completed-frame timing additionally requires <see cref="IGpuCommandListTimestampEnd"/>. Its armed tail
    /// writes the final index and ends this batch after all End finalization, before the native list seal.
    /// Failed or abandoned recordings clear only their tail, leaving the pool quarantined through teardown.
    /// </para>
    /// </summary>
    public interface IGpuTimestampPool : IDisposable
    {
        /// <summary>The maximum number of queries in a batch, fixed at creation.</summary>
        uint Capacity { get; }

        /// <summary>
        /// Poll the complete ended batch without waiting, flushing, submitting, synchronous mapping or a
        /// blocking lock. Pending and Invalid leave <paramref name="ticks"/> unchanged and set
        /// <paramref name="tickPeriodNanoseconds"/> to zero. Ready writes the batch's count of elapsed ticks
        /// relative to index zero, with a zero first tick and a finite positive nanosecond period. A larger
        /// destination keeps its unused tail. Equal adjacent ticks are valid.
        /// <para>
        /// COMPLETION HAS THE SAME CONTRACT AS <see cref="IGpuDevice.TryReadBuffer"/>. The supplied fence
        /// belongs to this device and to the submission containing EndTimestamps. Keep the pool and fence
        /// alive, and never reset, resubmit or dispose the fence until resolution completes and every
        /// associated readback ring has consumed its entries. A reset, reused or unrelated submission's fence
        /// is a caller error. A never-submitted fence returns Pending. Encoding does not signal completion.
        /// </para>
        /// <para>
        /// Backends read into private preallocated scratch and validate the complete result before publishing.
        /// Adjacent counter differences use the device's valid-bit mask with checked cumulative addition.
        /// Every adjacent interval must be shorter than a full counter wrap. Multiple wraps in one interval
        /// cannot be recovered. Unusable counters, disjoint data, unusable calibration or cumulative overflow
        /// produce Invalid. Pending retains the batch. Ready and Invalid consume it, and repeated resolution
        /// returns that same terminal status and identical ticks and period until the next begin.
        /// </para>
        /// <para>
        /// A too-small destination is refused before any fence poll or native access. Null, foreign and
        /// disposed fences, and disposed or lost devices, follow the validation and exception contract of
        /// <see cref="IGpuDevice.TryReadBuffer"/>. Direct3D 11 Submit does not flush. A presenting loop's next
        /// Present or a caller drain hands work to the driver. This member adds no headless flush policy.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentException">The destination cannot hold the batch.</exception>
        /// <exception cref="InvalidOperationException">No batch has ended yet.</exception>
        GpuTimestampStatus TryResolve(IGpuFence completion, Span<ulong> ticks, out double tickPeriodNanoseconds);
    }
}
