using System.Diagnostics;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// A COUNT AND A DURATION ACCUMULATED SINCE THE DEVICE WAS CREATED, which is the shape the soak gates report
    /// in. <see cref="MetalTimeline.TotalDrain"/> accumulates M-F5's drains into one, and row 8's ring segment
    /// stalls will accumulate into another (https://github.com/APKiwiOrg/KhaozEngine/issues/574).
    /// <para>
    /// CUMULATIVE RATHER THAN PER FRAME. A telemetry session writes a sample row on its own cadence, so a
    /// per-frame value read a few times a second reports the frames it happened to land on and says nothing
    /// about the hundreds it skipped. The gate for a stall count is ZERO ACROSS A WHOLE CAPTURE WINDOW, which a
    /// sampled per-frame number cannot establish and a cumulative one settles by subtracting the first row from
    /// the last.
    /// </para>
    /// <para>
    /// TICKS IN THE FIELD, MILLISECONDS ON READ. A week-long soak is tens of millions of frames, and summing a
    /// converted double per wait accumulates rounding the raw counter does not.
    /// </para>
    /// <para>
    /// A SNAPSHOT RATHER THAN THE ACCUMULATOR, which is what makes the read safe from any thread. A host keeps
    /// the running pair in two plain fields and hands them to <see cref="Sample"/>, which reads each ONE AT A
    /// TIME so a diagnostic on another thread can never see half of a 64-bit value a writer was part way
    /// through. What per-field reads cannot do is make the PAIR atomic: a sample taken while a wait is being
    /// recorded can carry the new count beside the old ticks, so it is off by ONE entry and never torn. Over a
    /// capture window measured in millions of frames that is noise, and the alternative costs the wait path a
    /// lock.
    /// </para>
    /// <para>
    /// <b>THE THIRD DELIBERATE COPY OF THESE SIX LINES, and the third is what makes it extractable.</b>
    /// <c>D3D11WaitTotals</c> and <c>VulkanWaitTotals</c> are the same type with the same reasoning, and phase
    /// 3's V-P4 ruled against a shared home because the rule of three is not satisfied by two and a home built
    /// at two implementations has its shape guessed from one of them. M-P4 answers that ruling: the counter
    /// accumulators are one of the five things #531's extraction takes, and it lands in row 18
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/584) AFTER rollout gate 3, deliberately, because
    /// extracting while the backend is being written gives a golden failure two candidate causes. So this copy
    /// is written to be deleted, and writing it anyway is what earns the right to delete all three.
    /// </para>
    /// </summary>
    internal readonly struct MetalWaitTotals
    {
        internal MetalWaitTotals(long count, long ticks)
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
        /// A host's running pair, read a field at a time. The host accumulates on whichever thread called the
        /// drain and is read by diagnostics on any thread, so the volatile pair of reads lives here once instead
        /// of being spelled out at each property. See the paragraphs above for what the pair does and does not
        /// promise a concurrent reader.
        /// </summary>
        /// <param name="count">The host's running wait count.</param>
        /// <param name="ticks">The host's running <see cref="Stopwatch"/> tick total.</param>
        internal static MetalWaitTotals Sample(ref long count, ref long ticks)
            => new MetalWaitTotals(Volatile.Read(ref count), Volatile.Read(ref ticks));
    }
}
