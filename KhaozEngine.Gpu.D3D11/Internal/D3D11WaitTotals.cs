using System.Diagnostics;
using System.Threading;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// A COUNT AND A DURATION ACCUMULATED SINCE THE DEVICE WAS CREATED, which is the shape both soak gates report
    /// in. <see cref="D3D11FenceSubsystem.TotalDrain"/> accumulates M2's real drains into one, and
    /// <see cref="D3D11RingAllocator.TotalBackpressure"/> accumulates M3's segment stalls into another.
    /// <para>
    /// CUMULATIVE RATHER THAN PER FRAME, AND IT EXISTS BESIDE THE PER-FRAME ROLLS RATHER THAN INSTEAD OF THEM.
    /// <see cref="D3D11DrainStats"/> and <see cref="D3D11BackpressureStats"/> describe the frame that has ended,
    /// which is what a debug overlay wants and what a test asserts on. Neither survives SAMPLING: a telemetry
    /// session writes a row on its own cadence, so a per-frame value read a few times a second reports the frames
    /// it happened to land on and says nothing about the hundreds it skipped. M3's exit criterion is that the
    /// stall count is ZERO across a whole capture window, which a sampled per-frame number cannot establish and a
    /// cumulative one settles by subtracting the first row from the last. M2's per-frame figure is the same
    /// subtraction divided by the frames the window covers.
    /// </para>
    /// <para>
    /// TICKS IN THE FIELD, MILLISECONDS ON READ. A week-long soak at the 125 fps baseline is tens of millions of
    /// frames, and summing a converted double per wait accumulates rounding the raw counter does not.
    /// </para>
    /// <para>
    /// A SNAPSHOT RATHER THAN THE ACCUMULATOR, which is what makes the read safe from any thread. A host keeps the
    /// running pair in two plain fields and hands them to <see cref="Sample"/>, which reads each ONE AT A TIME so a
    /// diagnostic on another thread can never see half of a 64-bit value a writer was part way through. The reader
    /// then holds a copy nothing can mutate behind it, and the writers keep whatever thread contract their host
    /// already documents rather than taking a lock for a counter.
    /// </para>
    /// <para>
    /// WHAT PER-FIELD READS CANNOT DO is make the PAIR atomic. A sample taken while a wait is being recorded can
    /// carry the new count beside the old ticks, so it is off by ONE entry and never torn. Over a capture window
    /// measured in millions of frames that is noise, and the alternative costs the wait path a lock.
    /// </para>
    /// </summary>
    internal readonly struct D3D11WaitTotals
    {
        internal D3D11WaitTotals(long count, long ticks)
        {
            Count = count;
            Ticks = ticks;
        }

        /// <summary>Waits that actually blocked, since the device was created.</summary>
        internal long Count { get; }

        /// <summary><see cref="Stopwatch"/> ticks those waits spent blocked, summed and unconverted.</summary>
        internal long Ticks { get; }

        /// <summary>Wall-clock milliseconds those waits spent blocked, summed. 0 when <see cref="Count"/> is
        /// 0.</summary>
        internal double TotalMs => Ticks * 1000d / Stopwatch.Frequency;

        /// <summary>
        /// A host's running pair, read a field at a time. Both hosts accumulate lock-free on the frame thread and
        /// both are read by diagnostics on any thread, so the volatile pair of reads lives here once instead of
        /// being spelled out at each property. See the paragraphs above for what the pair does and does not
        /// promise a concurrent reader.
        /// </summary>
        /// <param name="count">The host's running wait count.</param>
        /// <param name="ticks">The host's running <see cref="Stopwatch"/> tick total.</param>
        internal static D3D11WaitTotals Sample(ref long count, ref long ticks)
            => new D3D11WaitTotals(Volatile.Read(ref count), Volatile.Read(ref ticks));
    }
}
