using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.ServerStatus;

namespace KhaozEngine.Game
{
    /// <summary>
    /// The built-in boot step that consults the server-status endpoint and enforces the min-version gate. It WRAPS
    /// the existing <see cref="ServerStatusClient.PollOnceAsync"/> + <see cref="ServerStatusEvaluator.Evaluate"/>
    /// machinery: one blocking fetch (bounded by the source's own timeout) folded into a
    /// <see cref="ServerStatusView"/>. The fetch never throws, so an unreachable endpoint evaluates to
    /// <see cref="ServerStatusState.StatusUnknown"/> and the step proceeds - the boot degrades gracefully rather than
    /// hanging. A state in <see cref="BlockingStates"/> fails the boot with a localized message (a
    /// <see cref="BootStepException"/>), which the boot screen surfaces with a quit affordance. The default blocking
    /// set is just <see cref="ServerStatusState.UpdateRequired"/> (the client is below the server's minimum): the
    /// min-version gate becomes a boot failure, not a silent hang.
    /// </summary>
    public sealed class ServerStatusBootStep : IBootStep
    {
        readonly ServerStatusClient _client;
        readonly string _localClientVersion;
        readonly Func<DateTimeOffset> _clock;
        readonly ServerStatusEvaluationOptions? _evaluationOptions;

        /// <summary>The set of evaluated states that fail the boot (default: <see cref="ServerStatusState.UpdateRequired"/>).
        /// Add <see cref="ServerStatusState.ServerDown"/> / <see cref="ServerStatusState.ServerRestarting"/> to also
        /// block launch while the server is unavailable.</summary>
        public IReadOnlySet<ServerStatusState> BlockingStates { get; }

        /// <summary>
        /// Wrap <paramref name="client"/> as a boot step. <paramref name="localClientVersion"/> is this build's
        /// version (compared against the server's min / latest). <paramref name="weight"/> sizes the slice.
        /// <paramref name="blockingStates"/> overrides which states fail the boot (default
        /// <see cref="ServerStatusState.UpdateRequired"/>). <paramref name="clock"/> supplies "now" for staleness
        /// (default <see cref="DateTimeOffset.UtcNow"/>, a test seam). <paramref name="name"/> overrides the label
        /// (default <see cref="BootStrings.StepServerStatus"/>).
        /// </summary>
        public ServerStatusBootStep(
            ServerStatusClient client,
            string localClientVersion,
            float weight = 1f,
            IReadOnlySet<ServerStatusState>? blockingStates = null,
            Func<DateTimeOffset>? clock = null,
            ServerStatusEvaluationOptions? evaluationOptions = null,
            LocalizedText? name = null)
        {
            if (weight <= 0f) throw new ArgumentOutOfRangeException(nameof(weight), "Step weight must be positive.");
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _localClientVersion = localClientVersion ?? throw new ArgumentNullException(nameof(localClientVersion));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _evaluationOptions = evaluationOptions;
            BlockingStates = blockingStates ?? new HashSet<ServerStatusState> { ServerStatusState.UpdateRequired };
            Weight = weight;
            Name = name ?? (LocalizedText)BootStrings.StepServerStatus;
        }

        /// <inheritdoc />
        public LocalizedText Name { get; }

        /// <inheritdoc />
        public float Weight { get; }

        /// <inheritdoc />
        public async Task<BootStepResult> RunAsync(IBootProgress progress, CancellationToken cancellationToken)
        {
            progress.ReportIndeterminate();
            ServerStatusSnapshot snapshot = await _client.PollOnceAsync(cancellationToken);
            ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, _localClientVersion, _clock(), _evaluationOptions);
            if (BlockingStates.Contains(view.State))
                throw new BootStepException(MessageFor(view.State));
            return BootStepResult.Proceed;
        }

        static LocalizedText MessageFor(ServerStatusState state) => state == ServerStatusState.UpdateRequired
            ? BootStrings.ErrorUpdateRequired
            : BootStrings.ErrorServerUnavailable;
    }
}
