using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Audio;

/// <summary>
/// The thread-affinity half of <see cref="AudioSystem"/>: the owning thread it latches at construction and the
/// guard every mutating entry point runs first. Kept apart from the playback state so the contract is readable
/// on its own.
/// </summary>
public sealed partial class AudioSystem
{
    // The thread that constructed this instance. A field initializer, so it latches for EVERY constructor
    // overload without any of them having to remember. Read-only: an AudioSystem never changes owner.
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    /// <summary>
    /// Throws when the caller is not on the thread that constructed this <see cref="AudioSystem"/>.
    /// </summary>
    /// <remarks>
    /// <para>An int compare against a thread-local read, so it costs the same in Release as in Debug. That
    /// matters: CI builds Release, where a <c>Debug.Assert</c> does not exist at all, and a contract that only
    /// holds on a developer machine is not a contract.</para>
    /// <para><see cref="AudioSystem.Dispose"/> deliberately does NOT call this. Shutdown paths (a process-exit
    /// handler, a host tearing the game down) legitimately run somewhere else, and turning a clean teardown into
    /// an unhandled exception buys nothing at the point where the state is about to be dropped anyway. The
    /// contract still says do not race a live tick with a dispose, it is just not enforced there.</para>
    /// </remarks>
    /// <param name="member">
    /// The public member that was called. Filled in by the compiler at each call site, so a guard placed on a
    /// shared private helper passes the public name it is standing in for explicitly.
    /// </param>
    private void EnsureOwningThread([CallerMemberName] string member = "")
    {
        int current = Environment.CurrentManagedThreadId;
        if (current == _ownerThreadId)
        {
            return;
        }

        throw new InvalidOperationException(
            $"AudioSystem.{member} was called from thread {current}, but this AudioSystem is owned by thread " +
            $"{_ownerThreadId}. AudioSystem is main-thread-only: register, load, play, tick and change volumes " +
            "from the thread that constructed it. To trigger audio from a background job, hand the request to " +
            "the main thread and call from there.");
    }
}
