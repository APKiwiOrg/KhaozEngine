using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Progression;

namespace KhaozEngine.Commerce;

/// <summary>Outcome of a periodic-grant claim.</summary>
public readonly record struct PeriodicGrantResult(bool Granted, long NewBalance, TimeSpan TimeUntilNext);

/// <summary>A server-clock daily/periodic reward routed through the wallet. The server instant is the
/// only clock. No client timestamp is trusted, which closes the clock-forward exploit. Non-stacking via
/// <see cref="WallClockRewardSchedule"/>, credited-once via the wallet idempotency key. The first-ever claim
/// keys on a fixed per (account, reward) sentinel retained in the wallet ledger, a permanent one-shot: never
/// clear the schedule store while retaining the wallet ledger, because the next claim reads null, takes the
/// bootstrap path again, replays that retained sentinel and reports <c>Granted=false</c> with no error.
/// <see cref="ResetAsync"/> is the sanctioned way to re-open a reward (a wiped-progress account, a seasonal
/// re-issue on the same reward id), and it works precisely because it WRITES a schedule row rather than
/// deleting one, which keeps the bootstrap path unreachable.
/// <para><c>rewardId</c> may not contain <c>':'</c>, the separator the idempotency key is built from
/// (see <see cref="KeySeparator"/>).</para></summary>
public sealed class PeriodicGrant
{
    private readonly Wallet wallet;
    private readonly IGrantScheduleStore schedules;
    private readonly TimeSpan interval;
    private readonly string rewardId;
    private readonly CurrencyId currency;
    private readonly long amount;

    /// <summary>The character the idempotency key joins its segments with. The reward id may not contain it.</summary>
    private const char KeySeparator = ':';

    public PeriodicGrant(Wallet wallet, IGrantScheduleStore schedules, TimeSpan interval,
        string rewardId, CurrencyId currency, long amount)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(rewardId)) throw new ArgumentException("rewardId required", nameof(rewardId));
        // The idempotency key below is three segments joined by KeySeparator, and only an unambiguous split keeps two
        // different (rewardId, account) pairs off one key. Constraining the FIRST segment is enough to get that: the
        // first separator ends the reward id, the last one begins a suffix that is itself separator-free ("bootstrap"
        // or a decimal tick count), and whatever sits between the two is the account however many separators it holds.
        // Rejecting the reward id is what buys that without touching the account, which the fleet writes with colons
        // ("acct:1"). An escaping encoding would instead have rewritten every key already sitting in a wallet ledger,
        // and since a first-ever claim keys on a retained sentinel, every account would have taken one more bootstrap
        // grant on the upgrade. A reward id is operator-authored, never player-derived, so this refuses loudly at
        // construction rather than silently changing what an existing deployment pays out (#209).
        if (rewardId.IndexOf(KeySeparator) >= 0)
            throw new ArgumentException($"rewardId may not contain '{KeySeparator}'.", nameof(rewardId));
        this.wallet = wallet; this.schedules = schedules; this.interval = interval;
        this.rewardId = rewardId; this.currency = currency; this.amount = amount;
    }

    /// <summary>Claim if the server clock has reached availability. First-ever call is available immediately.</summary>
    public async Task<PeriodicGrantResult> TryClaimAsync(AccountId account, DateTimeOffset serverNowUtc,
        CancellationToken ct = default)
    {
        DateTimeOffset? nextRaw = await schedules.GetNextAvailableAsync(account, rewardId, ct);
        bool bootstrap = nextRaw is null;
        WallClockRewardSchedule schedule = nextRaw is DateTimeOffset next
            ? new WallClockRewardSchedule { Interval = interval, NextAvailableUtc = next }
            : WallClockRewardSchedule.Start(interval, serverNowUtc, availableImmediately: true);

        if (!schedule.IsAvailable(serverNowUtc))
        {
            long bal = await wallet.BalanceAsync(account, currency, ct);
            return new PeriodicGrantResult(false, bal, schedule.TimeUntilAvailable(serverNowUtc));
        }

        // Idempotency key pins this grant so a concurrent double-claim credits once. On the first-ever claim
        // the scheduled instant is the caller's own serverNowUtc, so two concurrent bootstraps would derive
        // two different keys and both credit. Pin the bootstrap to a fixed per-(account, reward) sentinel
        // instead: there is exactly one legitimate first grant, so concurrent first claims collide on one key
        // and the wallet credits exactly once. Every later claim reads a persisted NextAvailableUtc, already
        // stable across callers, and keys on its ticks.
        // The three segments split unambiguously because the ctor keeps KeySeparator out of the reward id and the
        // suffix is separator-free by construction. See the ctor for why the account segment is left alone.
        string key = bootstrap
            ? $"{rewardId}:{account.Value}:bootstrap"
            : $"{rewardId}:{account.Value}:{schedule.NextAvailableUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
        CreditResult credit = await wallet.GrantAsync(account, currency, amount, key, ct);

        WallClockRewardSchedule advanced = schedule.Claim(serverNowUtc);
        await schedules.SetNextAvailableAsync(account, rewardId, advanced.NextAvailableUtc, ct);
        return new PeriodicGrantResult(!credit.Replayed, credit.NewBalance, advanced.TimeUntilAvailable(serverNowUtc));
    }

    /// <summary>Re-opens this reward for <paramref name="account"/> from <paramref name="availableFromUtc"/>, so the
    /// next <see cref="TryClaimAsync"/> at or after that instant grants again. The sanctioned reset for a wiped
    /// progress account, an admin or player rewards reset, or a seasonal re-issue that reuses a reward id.
    /// <para>It WRITES a schedule row rather than deleting one, and that is the whole point. A deleted row sends the
    /// next claim down the bootstrap path, where it replays the sentinel the wallet ledger still holds and reports
    /// <c>Granted=false</c> with no error, silently denying a legitimate grant (#208). Writing a row keeps the
    /// bootstrap path unreachable, and the claim that follows keys on the written instant's ticks, which no earlier
    /// claim can have used. Resetting twice to the SAME instant is therefore one re-grant, not two, which is the
    /// right answer for a double-clicked admin button.</para>
    /// <para>Pass a server instant, never a client one, for the same reason <see cref="TryClaimAsync"/> does.</para></summary>
    public Task ResetAsync(AccountId account, DateTimeOffset availableFromUtc, CancellationToken ct = default)
        => schedules.SetNextAvailableAsync(account, rewardId, availableFromUtc, ct);
}
