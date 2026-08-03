using System.Threading;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE DRAIN'S SPIN POLICY, IN ONE PLACE, so "no iteration of the drain ever sleeps a millisecond" is a
    /// constant a reviewer can see change in a diff rather than a property a test has to infer from a clock.
    /// <para>
    /// <see cref="D3D11FenceSubsystem.WaitForIdle"/> is the only caller, and this is the only spin on that path.
    /// The type exists because the property it carries is invisible at the call site: a
    /// <c>spin.SpinOnce()</c> with the threshold left off, or with any non-negative number in it, compiles
    /// cleanly, reads like the same line, and quietly turns the drain into a sleeper. Naming the threshold once
    /// and routing the single call through it means that mistake has to edit a documented constant.
    /// </para>
    /// <para>
    /// WHY MINUS ONE IS LOAD-BEARING. <see cref="SpinWait.SpinOnce()"/> escalates to <c>Thread.Sleep(1)</c> once
    /// it has spun <c>sleep1Threshold</c> times, and its default threshold is 20. One such sleep is more than the
    /// whole 0.2 ms per-frame drain budget the M2 measurement is taken against (M2 targets an Apple M2 class
    /// frame), and more again at Windows' default timer resolution, where a one-millisecond sleep really lasts
    /// about 15.6 ms. A drain that escalated would therefore price the scheduler instead of the drain and settle
    /// decision C6 on a number about nothing. Minus one is the documented value that disables the escalation
    /// entirely, so the spin yields forever and never sleeps.
    /// </para>
    /// <para>
    /// It is the FALLBACK mechanism that reaches this at all. The monotonic Direct3D 11.4 fence blocks on the
    /// fence itself through <c>TryWaitForValue</c>, which wakes on the GPU's own signal at no granularity cost,
    /// and only the event-query timeline, which has no blocking primitive, spins instead.
    /// </para>
    /// </summary>
    internal static class D3D11DrainSpin
    {
        /// <summary>The <c>sleep1Threshold</c> every drain spin is taken with. Minus one disables
        /// <see cref="SpinWait"/>'s escalation to <c>Thread.Sleep(1)</c>, which is the whole point: see the type
        /// note for why one such sleep exceeds the entire per-frame budget the drain is measured against. Any
        /// non-negative value here would reintroduce it.</summary>
        internal const int Sleep1Threshold = -1;

        /// <summary>
        /// One iteration of the drain's spin. <paramref name="spin"/> is taken by reference because
        /// <see cref="SpinWait"/> is a mutable struct that carries the iteration count the backoff is computed
        /// from, so a copy would spin at iteration zero forever.
        /// </summary>
        /// <param name="spin">The caller's spin state, advanced by one iteration.</param>
        internal static void SpinOnce(ref SpinWait spin) => spin.SpinOnce(sleep1Threshold: Sleep1Threshold);
    }
}
