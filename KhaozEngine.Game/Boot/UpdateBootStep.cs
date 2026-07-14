using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Updates;

namespace KhaozEngine.Game
{
    /// <summary>
    /// The built-in boot step that connects to the update feed, checks for a newer build, and downloads + applies it
    /// (relaunching the app) when one is found. It WRAPS the existing <see cref="UpdateService.EnsureUpToDateAsync"/>
    /// composed gate rather than re-driving check / download / apply itself, so the Windows updater self-relocation
    /// and exit-and-relaunch behaviour is unchanged. Outcomes map to boot results:
    /// <list type="bullet">
    /// <item><see cref="UpdateGateOutcome.UpToDate"/>, <see cref="UpdateGateOutcome.FeedUnreachable"/>,
    /// <see cref="UpdateGateOutcome.Failed"/> -&gt; <see cref="BootStepResult.Proceed"/> (continue on the current build:
    /// a feed that is down or a failed download must never block launch).</item>
    /// <item><see cref="UpdateGateOutcome.Updating"/> -&gt; <see cref="BootStepResult.Restarting"/> (the process is
    /// exiting into the new build, and in production it has already exited by the time this returns). This is NOT a
    /// failure.</item>
    /// </list>
    /// The step reports the download as a determinate fraction and the check / apply phases as indeterminate.
    /// </summary>
    public sealed class UpdateBootStep : IBootStep
    {
        readonly UpdateService _service;
        readonly TimeSpan? _checkTimeout;

        /// <summary>
        /// Wrap <paramref name="service"/> as a boot step. <paramref name="checkTimeout"/> bounds the version check so
        /// a slow / unreachable feed degrades to proceeding (null uses <see cref="UpdateService.DefaultGateCheckTimeout"/>).
        /// <paramref name="weight"/> sizes this step's slice of the bar. <paramref name="name"/> overrides the label
        /// (default <see cref="BootStrings.StepUpdate"/>).
        /// </summary>
        public UpdateBootStep(UpdateService service, float weight = 1f, TimeSpan? checkTimeout = null, LocalizedText? name = null)
        {
            if (weight <= 0f) throw new ArgumentOutOfRangeException(nameof(weight), "Step weight must be positive.");
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _checkTimeout = checkTimeout;
            Weight = weight;
            Name = name ?? (LocalizedText)BootStrings.StepUpdate;
        }

        /// <inheritdoc />
        public LocalizedText Name { get; }

        /// <inheritdoc />
        public float Weight { get; }

        /// <inheritdoc />
        public async Task<BootStepResult> RunAsync(IBootProgress progress, CancellationToken cancellationToken)
        {
            progress.ReportIndeterminate();
            var relay = new RelayProgress(progress);
            UpdateGateResult result = await _service.EnsureUpToDateAsync(relay, _checkTimeout, cancellationToken);
            return result.Outcome == UpdateGateOutcome.Updating ? BootStepResult.Restarting : BootStepResult.Proceed;
        }

        // Maps the update gate's phase/byte progress onto the boot step's slice: a determinate fraction while
        // downloading (when the total is known), indeterminate for the check / apply phases.
        sealed class RelayProgress : IProgress<UpdateGateProgress>
        {
            readonly IBootProgress _inner;
            public RelayProgress(IBootProgress inner) => _inner = inner;

            public void Report(UpdateGateProgress value)
            {
                if (value.Phase == UpdateGatePhase.Downloading && value.TotalBytes > 0)
                    _inner.Report((float)(value.BytesDownloaded / (double)value.TotalBytes));
                else
                    _inner.ReportIndeterminate();
            }
        }
    }
}
