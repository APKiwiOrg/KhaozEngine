using System.Diagnostics;

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
    /// IMMUTABLE, with <see cref="Plus"/> returning the next value, so a host keeps one field and a reader gets a
    /// copy nothing can mutate behind it. The writers carry whatever thread contract their host already documents.
    /// This type adds no synchronisation of its own and claims none.
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

        /// <summary>This total plus one more wait lasting <paramref name="ticks"/>.</summary>
        internal D3D11WaitTotals Plus(long ticks) => new D3D11WaitTotals(Count + 1, Ticks + ticks);
    }
}
