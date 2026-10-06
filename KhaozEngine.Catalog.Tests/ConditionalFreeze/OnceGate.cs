using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.Catalog.ConditionalFreeze;

/// <summary>
/// One forced pause in a race reproduction. A participant's decorator calls <see cref="PauseAsync"/> at the
/// one entry or exit point the test armed, the test awaits <see cref="Entered"/>, moves the other participants
/// on, and then calls <see cref="Resume"/>. Nothing here measures time, so an interleaving is forced exactly
/// once rather than hoped for.
/// <para>
/// <b>It fires once.</b> The armed state is cleared before the pause is awaited, so a later legitimate call
/// on the same path, such as a runner's next attempt, passes straight through. A decorator awaits it outside
/// every store lock, transaction and lease, because it only ever wraps a call that has not started or one that
/// has already returned.
/// </para>
/// </summary>
internal sealed class OnceGate : IDisposable
{
    readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _armed = 1;

    /// <summary>Completes when a participant has reached the gate and is parked there.</summary>
    public Task Entered => _entered.Task;

    /// <summary>
    /// Parks the caller until <see cref="Resume"/>, the first time only. Every later call returns at once.
    /// The caller's own token still ends the pause, which is how a participant is cancelled while parked.
    /// </summary>
    /// <param name="cancellationToken">The participant's token.</param>
    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return Task.CompletedTask;
        }

        _entered.TrySetResult();
        return _resumed.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Lets the parked participant continue.</summary>
    public void Resume() => _resumed.TrySetResult();

    /// <summary>Disarms the gate and resumes anything parked on it, which is the test's cleanup.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _armed, 0);
        _resumed.TrySetResult();
        _entered.TrySetCanceled();
    }
}
