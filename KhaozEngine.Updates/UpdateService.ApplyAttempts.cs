using System.Threading;

#nullable enable

namespace KhaozEngine.Updates;

public sealed partial class UpdateService
{
    // Failed apply attempts observed since this service was constructed, i.e. this session (the service is
    // built once per process by every consumer, so a new object is a new session and the count starts at 0).
    // Written by SetApplyError, which can run on the game-loop thread (ApplyUpdate) or on a pool thread (a
    // repair that applies itself), so the write is interlocked and the read is a Volatile.Read.
    private int failedApplyAttempts;

    /// <summary>
    /// How many apply attempts have failed this session. An attempt counts only when
    /// <see cref="ApplyUpdate"/> actually got as far as trying (a call refused up front because the state was
    /// not <see cref="UpdateState.ReadyToApply"/> is not an attempt), and a failed DOWNLOAD is not one either.
    /// </summary>
    public int FailedApplyAttempts => Volatile.Read(ref failedApplyAttempts);

    /// <summary>
    /// True once <see cref="FailedApplyAttempts"/> has reached
    /// <see cref="UpdateServiceOptions.MaxApplyAttemptsPerSession"/>, which is the signal for an overlay to
    /// stop offering a retry (<see cref="UpdateOverlayActions.ResolveAction(IUpdateStatus)"/> returns
    /// <see cref="OverlayAction.None"/> from then on). Same reasoning as
    /// <see cref="UpdateOverlayActions.AutoAdvanceRequired"/>'s refusal to auto-retry a failed update: an
    /// environment that cannot apply (a read-only install dir, an AV lock on the shim, a full disk) fails the
    /// same way every time, so re-offering the retry only loops the player through another download. Always
    /// false when the cap is configured non-positive, which turns the cap off.
    /// </summary>
    public bool ApplyAttemptsExhausted =>
        options.MaxApplyAttemptsPerSession > 0 && FailedApplyAttempts >= options.MaxApplyAttemptsPerSession;

    /// <summary>
    /// <see cref="SetError"/> for a failure raised by <see cref="ApplyUpdate"/>: counts the attempt first, so a
    /// <see cref="StateChanged"/> subscriber woken by the <see cref="UpdateState.Failed"/> transition already
    /// reads the final <see cref="ApplyAttemptsExhausted"/>. Logs at warning exactly once, on the attempt that
    /// spends the budget, rather than on every failure.
    /// </summary>
    private void SetApplyError(string message)
    {
        int attempts = Interlocked.Increment(ref failedApplyAttempts);
        int cap = options.MaxApplyAttemptsPerSession;
        if (cap > 0 && attempts == cap)
        {
            log.Warn($"Apply failed {attempts} time(s) this session ({message}). " +
                "No further retry will be offered until the game is restarted.");
        }

        SetError(message);
    }
}
