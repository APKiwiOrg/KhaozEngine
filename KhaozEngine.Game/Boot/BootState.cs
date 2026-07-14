using KhaozEngine.App;

namespace KhaozEngine.Game
{
    /// <summary>The lifecycle state of a <see cref="BootPipeline"/>.</summary>
    public enum BootState
    {
        /// <summary>Not started yet (before <see cref="BootPipeline.Start"/>).</summary>
        Pending,

        /// <summary>Running the steps.</summary>
        Running,

        /// <summary>All steps finished successfully. The boot screen hands off to the game's first scene.</summary>
        Completed,

        /// <summary>A step failed. <see cref="BootView.FailureMessage"/> carries the localized reason.</summary>
        Failed,

        /// <summary>A step is handing the process off to a relaunch (an update is being applied). Not a failure - the
        /// app exits by design.</summary>
        Restarting,

        /// <summary>The boot was cancelled (e.g. the window closed) before finishing.</summary>
        Cancelled,
    }

    /// <summary>
    /// An immutable snapshot of the <see cref="BootPipeline"/>'s progress for rendering, taken each frame via
    /// <see cref="BootPipeline.Snapshot"/>. Being a value type it is safe to read on the render thread while steps
    /// advance on the pump. <see cref="Fraction"/> is the whole-bar fill in 0..1, <see cref="Indeterminate"/> is true
    /// while the current step's slice has no measurable fraction, <see cref="StepLabel"/> is that step's localized
    /// name, and <see cref="FailureMessage"/> is set only in <see cref="BootState.Failed"/>.
    /// </summary>
    public readonly record struct BootView(
        BootState State,
        float Fraction,
        bool Indeterminate,
        LocalizedText StepLabel,
        LocalizedText? FailureMessage);
}
