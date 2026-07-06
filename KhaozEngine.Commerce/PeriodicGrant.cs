using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Progression;

namespace KhaozEngine.Commerce;

/// <summary>Outcome of a periodic-grant claim.</summary>
public readonly record struct PeriodicGrantResult(bool Granted, long NewBalance, TimeSpan TimeUntilNext);

/// <summary>A server-clock daily/periodic reward routed through the wallet. The server instant is the
/// only clock; no client timestamp is trusted, which closes the clock-forward exploit. Non-stacking via
/// <see cref="WallClockRewardSchedule"/>; credited-once via the wallet idempotency key.</summary>
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
        WallClockRewardSchedule schedule = nextRaw is DateTimeOffset next
            ? new WallClockRewardSchedule { Interval = interval, NextAvailableUtc = next }
            : WallClockRewardSchedule.Start(interval, serverNowUtc, availableImmediately: true);

        if (!schedule.IsAvailable(serverNowUtc))
        {
            long bal = await wallet.BalanceAsync(account, currency, ct);
            return new PeriodicGrantResult(false, bal, schedule.TimeUntilAvailable(serverNowUtc));
        }

        // Idempotency key pins this grant to the scheduled instant, so a concurrent double-claim credits once.
        string key = $"{rewardId}:{account.Value}:{schedule.NextAvailableUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}";
        CreditResult credit = await wallet.GrantAsync(account, currency, amount, key, ct);

        WallClockRewardSchedule advanced = schedule.Claim(serverNowUtc);
        await schedules.SetNextAvailableAsync(account, rewardId, advanced.NextAvailableUtc, ct);
        return new PeriodicGrantResult(!credit.Replayed, credit.NewBalance, advanced.TimeUntilAvailable(serverNowUtc));
    }
}
