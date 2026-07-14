using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;

namespace KhaozEngine.Game
{
    /// <summary>
    /// One stage of the <see cref="BootPipeline"/>: a named unit of startup work with a relative
    /// <see cref="Weight"/> that decides how much of the single overall progress bar its slice occupies. Steps run
    /// sequentially in registration order, each mapped onto its own weighted slice of the bar. A step reports fine
    /// progress inside its slice through <see cref="IBootProgress"/>, or marks the slice indeterminate when it
    /// cannot (a blocking fetch of unknown duration). Returning <see cref="BootStepResult.Proceed"/> advances to the
    /// next step. Throwing <see cref="BootStepException"/> (or any exception) fails the boot with a localized error.
    /// Returning <see cref="BootStepResult.Restarting"/> stops the pipeline because the process is handing off to a
    /// relaunch (the update-apply path), which is NOT a failure. The built-in wrappers are <see cref="UpdateBootStep"/>
    /// and <see cref="ServerStatusBootStep"/>. A game adds its own asset-warm-up steps via <see cref="BootStep"/>.
    /// </summary>
    public interface IBootStep
    {
        /// <summary>The localized label shown while this step runs (resolves through the catalog at draw time).</summary>
        LocalizedText Name { get; }

        /// <summary>Relative weight of this step's slice of the overall bar. Values are normalized across all steps,
        /// so only the ratios matter (a step twice this one's weight fills twice the bar). Must be positive.</summary>
        float Weight { get; }

        /// <summary>
        /// Run the step. Report progress in 0..1 within this step's slice through <paramref name="progress"/>, or call
        /// <see cref="IBootProgress.ReportIndeterminate"/> for an activity-only slice. Honour
        /// <paramref name="cancellationToken"/> (it fires when the boot is cancelled, e.g. the window is closing).
        /// Return <see cref="BootStepResult.Proceed"/> to continue, or <see cref="BootStepResult.Restarting"/> when the
        /// process is about to relaunch. Throw <see cref="BootStepException"/> to fail with a localized message.
        /// </summary>
        Task<BootStepResult> RunAsync(IBootProgress progress, CancellationToken cancellationToken);
    }

    /// <summary>The terminal signal a completed <see cref="IBootStep.RunAsync"/> hands back to the pipeline.</summary>
    public enum BootStepResult
    {
        /// <summary>The step finished, advance to the next step (the common case).</summary>
        Proceed,

        /// <summary>The step is handing off to an application relaunch (e.g. an update is being applied), so the
        /// pipeline stops without running the remaining steps. This is NOT a failure. In production the process has
        /// usually already exited by the time this returns, so it only surfaces on the test/no-exit path.</summary>
        Restarting,
    }

    /// <summary>
    /// The progress sink a step reports through, mapping onto that step's weighted slice of the single overall boot
    /// bar. A step either reports a determinate fraction (<see cref="Report"/>) or marks its slice indeterminate
    /// (<see cref="ReportIndeterminate"/>) when the work has no measurable fraction (a blocking network fetch). Both
    /// are safe to call from any thread.
    /// </summary>
    public interface IBootProgress
    {
        /// <summary>Report determinate progress in 0..1 WITHIN this step's slice (clamped). 0 is the start of the
        /// slice, 1 its end. Clears any indeterminate marker for the slice.</summary>
        void Report(float fraction);

        /// <summary>Mark this step's slice indeterminate: the bar shows activity across the slice rather than a fixed
        /// fill, for work whose duration is unknown (a one-shot fetch). Stays indeterminate until the next
        /// <see cref="Report"/> or the step completes.</summary>
        void ReportIndeterminate();
    }
}
