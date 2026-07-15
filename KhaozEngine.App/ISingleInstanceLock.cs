using System;

namespace KhaozEngine.App;

/// <summary>
/// The OS-level operations behind <see cref="SingleInstanceGuard"/>: claim ownership of a named key,
/// signal whoever owns it to come to the foreground, and (for the owner) wait for such a signal.
/// Abstracted so the guard is headless-testable with a fake; <see cref="SystemSingleInstanceLock"/> is the
/// real implementation (a named <see cref="System.Threading.Mutex"/> for ownership plus a polled marker
/// file for the foreground signal - see that type's remarks for why not a named event/semaphore).
/// </summary>
public interface ISingleInstanceLock : IDisposable
{
    /// <summary>
    /// Attempts to become the sole owner of <paramref name="key"/>. Returns true immediately when no other
    /// live process holds it. When another process already owns it, waits up to
    /// <paramref name="predecessorWait"/> in case that owner is a predecessor mid-exit (see
    /// <see cref="SingleInstanceGuard"/>'s <c>AppRelaunch</c> composition note) rather than a genuine second
    /// live instance. Returns false only when the key is still held once the wait elapses.
    /// </summary>
    bool TryAcquire(string key, TimeSpan predecessorWait);

    /// <summary>
    /// Signals whichever process currently owns <paramref name="key"/> to come to the foreground. Called by
    /// the LOSING side of a conflict; a best-effort fire-and-forget, safe to call even if no owner is
    /// listening (the signal is simply missed).
    /// </summary>
    void RequestForeground(string key);

    /// <summary>
    /// Blocks the calling thread (intended to be a dedicated background thread) until a foreground request
    /// arrives for the key this lock currently owns, or <paramref name="timeout"/> elapses. Returns true
    /// when a request arrived. Only meaningful after <see cref="TryAcquire"/> returned true; implementers
    /// should return false immediately otherwise (nothing to wait on).
    /// </summary>
    bool WaitForForegroundRequest(TimeSpan timeout);
}
