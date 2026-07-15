using System;

namespace KhaozEngine.App;

/// <summary>Outcome of <see cref="SingleInstanceGuard.TryAcquire"/>.</summary>
public enum SingleInstanceOutcome
{
    /// <summary>No other live instance held the key: this process is now the sole owner.</summary>
    Acquired,

    /// <summary>
    /// Another live instance already holds the key. It was asked (best-effort) to come to the foreground;
    /// this process should exit cleanly without ever creating a window.
    /// </summary>
    AlreadyRunning
}

/// <summary>Result of <see cref="SingleInstanceGuard.TryAcquire"/>.</summary>
public readonly struct SingleInstanceAcquireResult
{
    /// <summary>Constructs a result.</summary>
    public SingleInstanceAcquireResult(SingleInstanceOutcome outcome, ISingleInstanceLock? instanceLock)
    {
        Outcome = outcome;
        Lock = instanceLock;
    }

    /// <summary>Whether this process acquired the key or found it already held.</summary>
    public SingleInstanceOutcome Outcome { get; }

    /// <summary>
    /// The acquired lock, non-null only when <see cref="Outcome"/> is <see cref="SingleInstanceOutcome.Acquired"/>.
    /// Owns the OS mutex: keep it alive for the process lifetime and <see cref="IDisposable.Dispose"/> it on
    /// shutdown to release the key promptly (a process crash also releases it, just not as promptly). Also
    /// exposes <see cref="ISingleInstanceLock.WaitForForegroundRequest"/> so the owner can react when a
    /// later conflicting launch asks it to come forward.
    /// </summary>
    public ISingleInstanceLock? Lock { get; }
}

/// <summary>
/// Ensures only one live instance of an app runs at a time, via a named OS mutex claimed BEFORE any window
/// or GPU device is created. Opt-in: <c>KhaozEngine.Game</c>'s <c>GameApp</c> calls
/// <see cref="TryAcquire"/> at the very top of its constructor when <c>GameAppOptions.SingleInstance</c> is
/// set, keyed by <c>GameAppOptions.SingleInstanceId</c> (falling back to <c>AppUserModelId</c>). On
/// <see cref="SingleInstanceOutcome.AlreadyRunning"/> the losing process logs one line and exits cleanly
/// (code 0) without ever constructing a window - see <c>GameApp</c>'s constructor.
/// </summary>
/// <remarks>
/// <para>
/// Composes with <see cref="AppRelaunch.Restart"/>'s forced-restart handshake: a successor is launched by
/// the still-running predecessor BEFORE that predecessor shuts down (see <see cref="AppRelaunch"/>), so a
/// naive mutex acquire in the successor would race a key the dying predecessor has not released yet - and
/// lose, mistaking a legitimate relaunch for a second live instance. <see cref="TryAcquire"/>'s
/// <c>predecessorWait</c> (default <see cref="AppRelaunch.DefaultPredecessorTimeout"/>, the same bound
/// <see cref="AppRelaunch.AwaitPredecessor"/> already uses for the same predecessor) rides out exactly that
/// window: the predecessor releases the mutex as part of its normal shutdown, so the successor picks it up
/// as soon as that happens rather than being rejected by its own dying predecessor.
/// </para>
/// <para>
/// Also resolves the auto-updater's relaunch-stacking gap
/// (<c>KhaozEngine.Updates.UpdateApplier.ResilientRelaunch</c>): if a post-update relaunch lands on top of a
/// surviving sibling instance, the freshly-started process finds the guard already held, asks the survivor
/// to come forward, and exits itself (reported to the updater as
/// <c>RelaunchStartupOutcome.ExitedEarly</c>) - no second window, and no special-casing needed in the
/// updater itself.
/// </para>
/// </remarks>
public static class SingleInstanceGuard
{
    /// <summary>
    /// Attempts to become the sole owner of <paramref name="key"/>.
    /// </summary>
    /// <param name="key">
    /// The single-instance key: a stable per-app identifier (e.g. an AppUserModelId like
    /// "APKiwi.Nullwake"). Must be non-empty.
    /// </param>
    /// <param name="instanceLock">The lock seam; null uses a fresh <see cref="SystemSingleInstanceLock"/>. Injected in tests.</param>
    /// <param name="predecessorWait">
    /// How long to wait for a conflicting owner to release the key before treating it as a genuine second
    /// live instance; null uses <see cref="AppRelaunch.DefaultPredecessorTimeout"/>.
    /// </param>
    /// <returns>
    /// <see cref="SingleInstanceOutcome.Acquired"/> with the owning <see cref="ISingleInstanceLock"/>, or
    /// <see cref="SingleInstanceOutcome.AlreadyRunning"/> (the existing owner has already been asked to
    /// come to the foreground; the lock is disposed and not returned).
    /// </returns>
    public static SingleInstanceAcquireResult TryAcquire(
        string key,
        ISingleInstanceLock? instanceLock = null,
        TimeSpan? predecessorWait = null)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("A single-instance key is required.", nameof(key));
        }

        ISingleInstanceLock lockImpl = instanceLock ?? new SystemSingleInstanceLock();
        TimeSpan wait = predecessorWait ?? AppRelaunch.DefaultPredecessorTimeout;

        if (lockImpl.TryAcquire(key, wait))
        {
            return new SingleInstanceAcquireResult(SingleInstanceOutcome.Acquired, lockImpl);
        }

        lockImpl.RequestForeground(key);
        lockImpl.Dispose();
        return new SingleInstanceAcquireResult(SingleInstanceOutcome.AlreadyRunning, null);
    }
}
