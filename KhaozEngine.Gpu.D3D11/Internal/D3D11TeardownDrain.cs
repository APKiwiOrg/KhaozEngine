using System.Diagnostics;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// HOW LONG A DRAIN AT TEARDOWN IS ALLOWED TO TAKE BEFORE IT GIVES UP AND CARRIES ON
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/505), and the one place that number is written down.
    /// <para>
    /// THE FRAME DRAIN STILL HAS NO TIMEOUT AND MUST NOT GET ONE. A GPU that never reaches a signalled point has
    /// hung, and blocking is the honest answer there: a timeout mid-frame would turn a hang into silent forward
    /// progress over work that has not happened, which is the failure this backend exists to avoid.
    /// </para>
    /// <para>
    /// TEARDOWN IS A DIFFERENT QUESTION, and it is the difference that earns the bound. Nothing after the
    /// teardown drain reads a rendered result, so there is no wrong picture to produce. What the drain buys there
    /// is only that the GPU has stopped touching memory about to be freed, and the process is on its way out
    /// anyway. Against that, <c>GpuDeviceContext.Dispose</c> calls
    /// <c>IGpuDeviceLifecycle.MarkDeviceDisposed</c> INSIDE the process-wide lifecycle gate, so an unbounded wait
    /// there wedges every other device create and dispose in the process behind one hung GPU, with no name on the
    /// hang and nothing to time it out.
    /// </para>
    /// <para>
    /// A DEVICE THAT DIES MID-DRAIN ALREADY RELEASES THE CALLER, which is why this bound is not the common case's
    /// safety net. It covers the one case nothing else does: a GPU that is LIVE and hung. That case cannot be
    /// reproduced on a software rasterizer or on any leg the suite runs, so what is tested here is the POLICY
    /// (the bound is taken, the drain latches, teardown continues) rather than the wedge itself, which needs
    /// hardware in that state.
    /// </para>
    /// <para>
    /// THE BUDGET IS DELIBERATELY GENEROUS. Two seconds is far longer than any real drain, several times the
    /// Windows TDR that would have reset a hung device long before, so a machine that reaches it is a machine
    /// where waiting further gains nothing. Undershooting would be the real hazard: a bound short enough to
    /// expire on a legitimately slow drain would free memory while the GPU was still reading it.
    /// </para>
    /// </summary>
    internal static class D3D11TeardownDrain
    {
        /// <summary>The budget a teardown drain gets, in milliseconds. See the type note for why two seconds and
        /// not less.</summary>
        internal const int BudgetMs = 2000;

        /// <summary>The budget that never expires, which is what every drain outside teardown takes and what
        /// keeps the frame path's behaviour exactly what it was.</summary>
        internal const int Unbounded = -1;

        /// <summary>
        /// Whether a drain that has been running for <paramref name="elapsedTicks"/> (in
        /// <see cref="Stopwatch"/> ticks) has spent a budget of <paramref name="budgetMs"/>. A negative budget is
        /// <see cref="Unbounded"/> and is never spent, and a zero budget is spent immediately, which is what lets
        /// a test drive the latch with no wall clock in it at all.
        /// </summary>
        internal static bool BudgetSpent(long elapsedTicks, int budgetMs)
            => budgetMs >= 0 && elapsedTicks >= TicksFor(budgetMs);

        /// <summary>A millisecond budget as <see cref="Stopwatch"/> ticks.</summary>
        internal static long TicksFor(int budgetMs) => (long)(Stopwatch.Frequency * (budgetMs / 1000d));

        /// <summary>What the device logs when a teardown drain spent its budget. It names the consequence rather
        /// than only the fact, because the read this line has to support is "the process did not wedge, and here
        /// is what it gave up to avoid wedging".</summary>
        internal static string LatchedWarning(int budgetMs)
            => $"The native Direct3D 11 device did not go idle within {budgetMs} ms at teardown, so the drain "
                + "gave up and the release continued. The GPU is hung or extraordinarily far behind. Teardown "
                + "does not wait longer, because this drain runs inside the process-wide device lifecycle gate "
                + "and every other device create and dispose in the process would wait behind it.";
    }
}
