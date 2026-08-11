using System.Diagnostics;
using System.Threading;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// A COUNT AND A DURATION ACCUMULATED SINCE THE DEVICE WAS CREATED, which is the shape every backend's soak
    /// gates report in. Each of the three native backends accumulates its drains into one of these, its uniform
    /// ring's segment stalls into another, and (on the two that have an acquire boundary to time) its swapchain
    /// acquire waits into a third.
    /// <para>
    /// CUMULATIVE RATHER THAN PER FRAME, AND IT EXISTS BESIDE THE PER-FRAME ROLLS RATHER THAN INSTEAD OF THEM. A
    /// per-frame struct describes the frame that has ended, which is what a debug overlay wants and what a test
    /// asserts on. Neither survives SAMPLING: a telemetry session writes a row on its own cadence, so a per-frame
    /// value read a few times a second reports the frames it happened to land on and says nothing about the
    /// hundreds it skipped. The exit criterion for a stall count is ZERO ACROSS A WHOLE CAPTURE WINDOW, which a
    /// sampled per-frame number cannot establish and a cumulative one settles by subtracting the first row from
    /// the last. A per-frame drain figure is the same subtraction divided by the frames the window covers.
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
    /// <para>
    /// <b>ONE TYPE FOR THREE BACKENDS, WHICH IS #531's SECOND EXTRACTION AND WHY V-P4's DECLINE WAS RIGHT AT THE
    /// TIME.</b> Phase 3 ruled against a shared home because the rule of three is not satisfied by two and a home
    /// built at two implementations has its shape guessed from one of them. The third arrived with the Metal
    /// backend and was code-identical to both, so row 18 of the Metal design took it. What did NOT come with it is
    /// the ACCUMULATION SITES: the counting rule differs per backend at every counter but the off-timeline pair,
    /// each difference is argued in the code that holds it, and <c>DrainCount</c> is a shipped
    /// <c>GpuDeviceCounters</c> channel, so a shared drain picking one rule would change a number in the field on
    /// two backends. The carriers extract, the sites do not.
    /// </para>
    /// </summary>
    internal readonly struct WaitTotals
    {
        internal WaitTotals(long count, long ticks)
        {
            Count = count;
            Ticks = ticks;
        }

        /// <summary>Waits that actually blocked, since the device was created. The one caller that records a
        /// non-blocking call too is the Metal drawable acquire, which has no way to ask whether it would block,
        /// and that exception is documented where it is made rather than here.</summary>
        internal long Count { get; }

        /// <summary><see cref="Stopwatch"/> ticks those waits spent blocked, summed and unconverted.</summary>
        internal long Ticks { get; }

        /// <summary>Wall-clock milliseconds those waits spent blocked, summed. 0 when <see cref="Count"/> is
        /// 0.</summary>
        internal double TotalMs => Ticks * 1000d / Stopwatch.Frequency;

        /// <summary>
        /// A host's running pair, read a field at a time. Hosts accumulate lock-free, on the frame thread or on
        /// whichever thread called the drain, and are read by diagnostics on any thread, so the volatile pair of
        /// reads lives here once instead of being spelled out at each property. See the paragraphs above for what
        /// the pair does and does not promise a concurrent reader.
        /// </summary>
        /// <param name="count">The host's running wait count.</param>
        /// <param name="ticks">The host's running <see cref="Stopwatch"/> tick total.</param>
        internal static WaitTotals Sample(ref long count, ref long ticks)
            => new WaitTotals(Volatile.Read(ref count), Volatile.Read(ref ticks));
    }
}
