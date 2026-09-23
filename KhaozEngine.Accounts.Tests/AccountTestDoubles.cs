using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;

namespace KhaozEngine.Tests.Accounts;

/// <summary>A clock that moves only when told to.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>
/// An <see cref="IAccountStore"/> over the reference store that can be told to fail or to stall, which is how the
/// ban adapter's write order and reload serialization are observed from outside.
/// </summary>
internal sealed class ScriptedAccountStore(IAccountStore inner) : IAccountStore
{
    // Armed by StallNextList, consumed by the next ListBannedAsync, and completed by ReleaseList either way.
    private TaskCompletionSource? stall;
    private int stallConsumed;
    private readonly TaskCompletionSource listEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool FailWrites { get; set; }

    public bool FailListBanned { get; set; }

    /// <summary>Completes once a stalled <see cref="ListBannedAsync"/> has taken its snapshot.</summary>
    public Task ListEntered => listEntered.Task;

    /// <summary>Makes the next <see cref="ListBannedAsync"/> snapshot the store at once and return that snapshot
    /// only when <see cref="ReleaseList"/> is called, as a slow database read would.</summary>
    public void StallNextList()
    {
        stall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stallConsumed = 0;
    }

    public void ReleaseList() => stall?.TrySetResult();

    public Task<AccountRecord> FindOrCreateAsync(AccountSignIn signIn, CancellationToken ct = default) =>
        inner.FindOrCreateAsync(signIn, ct);

    public Task<AccountRecord?> FindAsync(string subject, CancellationToken ct = default) => inner.FindAsync(subject, ct);

    public Task<IReadOnlyList<AccountRecord>> ListAsync(string? afterSubject = null, int limit = 500,
        CancellationToken ct = default) => inner.ListAsync(afterSubject, limit, ct);

    public async Task<IReadOnlyList<AccountRecord>> ListBannedAsync(CancellationToken ct = default)
    {
        if (FailListBanned) throw new InvalidOperationException("list fault");
        IReadOnlyList<AccountRecord> snapshot = await inner.ListBannedAsync(ct).ConfigureAwait(false);
        TaskCompletionSource? gate = stall;
        if (gate is not null && Interlocked.Exchange(ref stallConsumed, 1) == 0)
        {
            listEntered.TrySetResult();
            await gate.Task.ConfigureAwait(false);
        }
        return snapshot;
    }

    public Task<AccountRecord?> SetWhitelistedAsync(string subject, bool whitelisted, CancellationToken ct = default) =>
        inner.SetWhitelistedAsync(subject, whitelisted, ct);

    public Task<AccountRecord?> BanAsync(string subject, string reason, DateTimeOffset? until,
        CancellationToken ct = default) =>
        FailWrites ? throw new InvalidOperationException("write fault") : inner.BanAsync(subject, reason, until, ct);

    public Task<AccountRecord?> UnbanAsync(string subject, CancellationToken ct = default) =>
        FailWrites ? throw new InvalidOperationException("write fault") : inner.UnbanAsync(subject, ct);
}
