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
/// keys on a fixed per (account, reward) sentinel retained in the wallet ledger, a permanent one-shot: do not
/// clear the schedule store while retaining the wallet ledger unless denying the re-grant is intended.</summary>
public sealed class PeriodicGrant
{
    private readonly Wallet wallet;
    private readonly IGrantScheduleStore schedules;
    private readonly TimeSpan interval;
    private readonly string rewardId;
    private readonly CurrencyId currency;
    private readonly long amount;

    public PeriodicGrant(Wallet wallet, IGrantScheduleStore schedules, TimeSpan interval,
        string rewardId, CurrencyId currency, long amount)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(rewardId)) throw new ArgumentException("rewardId required", nameof(rewardId));
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
        string key = bootstrap
            ? $"{rewardId}:{account.Value}:bootstrap"
            : $"{rewardId}:{account.Value}:{schedule.NextAvailableUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
        CreditResult credit = await wallet.GrantAsync(account, currency, amount, key, ct);

        WallClockRewardSchedule advanced = schedule.Claim(serverNowUtc);
        await schedules.SetNextAvailableAsync(account, rewardId, advanced.NextAvailableUtc, ct);
        return new PeriodicGrantResult(!credit.Replayed, credit.NewBalance, advanced.TimeUntilAvailable(serverNowUtc));
    }
}
