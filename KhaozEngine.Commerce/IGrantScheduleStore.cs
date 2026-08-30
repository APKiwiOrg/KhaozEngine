using System;
using System.Threading;
using System.Threading.Tasks;
namespace KhaozEngine.Commerce;

/// <summary>Persists the next-available instant per (account, reward). Last-write-wins is safe because
/// the credit idempotency key is the real double-grant guard. A cleared row with a retained wallet ledger
/// replays the bootstrap sentinel and denies the grant, so clear both together or neither. To re-open a reward
/// without clearing anything, call <see cref="PeriodicGrant.ResetAsync"/>, which writes a row instead.</summary>
public interface IGrantScheduleStore
{
    Task<DateTimeOffset?> GetNextAvailableAsync(AccountId account, string rewardId, CancellationToken ct = default);
    Task SetNextAvailableAsync(AccountId account, string rewardId, DateTimeOffset nextUtc, CancellationToken ct = default);
}
