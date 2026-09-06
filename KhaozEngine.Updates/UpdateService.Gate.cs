using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace KhaozEngine.Updates;

public sealed partial class UpdateService
{
    /// <summary>Default ceiling on the feed check inside <see cref="EnsureUpToDateAsync"/> (the download/apply that
    /// follows is deliberately unbounded - the player watches it progress). Picked so a down/slow feed does not
    /// stall startup for long before falling through to <see cref="UpdateGateOutcome.FeedUnreachable"/>.</summary>
    public static readonly TimeSpan DefaultGateCheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One awaitable startup gate composing <see cref="CheckForUpdateAsync"/> + <see cref="StartDownloadAsync"/> +
    /// <see cref="ApplyUpdate"/>, for a caller to run ONCE BEFORE connecting so an out-of-date client self-heals
    /// rather than connecting on a stale build. If a newer signed build exists it downloads, verifies, applies, and
    /// relaunches (the process exits into the new version - this method does not return), so the caller never
    /// proceeds to connect on the old build. Otherwise it returns a <see cref="UpdateGateResult"/> to branch on.
    ///
    /// Non-fatal and bounded: the feed check is capped by <paramref name="checkTimeout"/> (default
    /// <see cref="DefaultGateCheckTimeout"/>); a down/slow/unreachable feed yields
    /// <see cref="UpdateGateOutcome.FeedUnreachable"/> and a failed download/apply yields
    /// <see cref="UpdateGateOutcome.Failed"/> - in both cases the caller should continue on the current build and
    /// rely on the connect-time version handshake as the backstop. Never blocks past the timeout waiting on the feed.
    /// </summary>
    /// <param name="progress">Optional progress sink for a "Downloading update..." screen (phase + byte/file counts).</param>
    /// <param name="checkTimeout">Ceiling on the feed check; <c>null</c> uses <see cref="DefaultGateCheckTimeout"/>,
    /// <see cref="TimeSpan.Zero"/> or negative disables the cap.</param>
    /// <param name="cancellationToken">Caller cancellation (distinct from the internal timeout): cancelling throws
    /// <see cref="OperationCanceledException"/> rather than returning a result.</param>
    public async Task<UpdateGateResult> EnsureUpToDateAsync(
        IProgress<UpdateGateProgress>? progress = null,
        TimeSpan? checkTimeout = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan timeout = checkTimeout ?? DefaultGateCheckTimeout;

        void Report()
        {
            progress?.Report(new UpdateGateProgress(
                MapPhase(state), BytesDownloaded, totalDownloadBytes, filesDownloaded, TotalFilesToDownload));
        }

        if (progress is not null) StateChanged += Report;
        try
        {
            // Bound the feed check: a separate linked CTS the timeout cancels. CheckForUpdateAsync swallows the
            // cancellation internally (it is offline-safe -> Idle), so we detect a timeout afterwards via
            // lastCheckReachedFeed rather than catching here; the caller's own token is re-checked below so a real
            // caller cancellation still propagates.
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout > TimeSpan.Zero) checkCts.CancelAfter(timeout);
            await CheckForUpdateAsync(checkCts.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Could not reach the feed (down / slow / timed out): proceed on the current build.
            if (!lastCheckReachedFeed)
                return new UpdateGateResult(UpdateGateOutcome.FeedUnreachable, remoteVersion, errorMessage);

            switch (state)
            {
                case UpdateState.Idle:
                    // Reached the feed; nothing newer to install.
                    return new UpdateGateResult(UpdateGateOutcome.UpToDate, null, null);

                case UpdateState.UpdateAvailable:
                    await StartDownloadAsync(cancellationToken).ConfigureAwait(false);
                    return state == UpdateState.ReadyToApply
                        ? ApplyOrFail()
                        : new UpdateGateResult(UpdateGateOutcome.Failed, remoteVersion, errorMessage);

                case UpdateState.ReadyToApply:
                    // Files were already staged (e.g. a prior interrupted run); apply straight away.
                    return ApplyOrFail();

                case UpdateState.Failed:
                case UpdateState.Untrusted:
                    return new UpdateGateResult(UpdateGateOutcome.Failed, remoteVersion, errorMessage);

                default:
                    return new UpdateGateResult(UpdateGateOutcome.Failed, remoteVersion,
                        errorMessage ?? $"Unexpected update state {state}.");
            }
        }
        finally
        {
            if (progress is not null) StateChanged -= Report;
        }
    }

    private UpdateGateResult ApplyOrFail()
    {
        // ApplyUpdate launches the shim and exits the process on success (so in production this never returns).
        bool launched = ApplyUpdate();
        return launched
            ? new UpdateGateResult(UpdateGateOutcome.Updating, remoteVersion, null)
            : new UpdateGateResult(UpdateGateOutcome.Failed, remoteVersion, errorMessage);
    }

    private static UpdateGatePhase MapPhase(UpdateState s) => s switch
    {
        UpdateState.UpdateAvailable or UpdateState.Downloading => UpdateGatePhase.Downloading,
        UpdateState.ReadyToApply or UpdateState.Applying => UpdateGatePhase.Applying,
        _ => UpdateGatePhase.Checking,
    };
}
