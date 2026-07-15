using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

/// <summary>
/// Async, keyed durable store for authoritative server state (accounts, characters, zones), shaped for a
/// database backend rather than the per-user local JSON files of <c>KhaozEngine.Persistence</c>. The game
/// serializes a record to bytes by its own means and persists it under a stable key. Implementations:
/// <see cref="InMemoryWorldStore"/> (this dependency-free core, for tests/dev); durable backends are the opt-in
/// sibling packages KhaozEngine.WorldStore.Sqlite (dev/test + single-node) and KhaozEngine.WorldStore.SqlServer
/// (production / Azure SQL).
/// </summary>
public interface IWorldStore
{
    /// <summary>Loads the record for <paramref name="key"/>, or null if absent.</summary>
    Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Stores (inserting or overwriting) the record for <paramref name="key"/>.</summary>
    Task SaveAsync(string key, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>Deletes the record for <paramref name="key"/>; returns true if one was present.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>True if a record exists for <paramref name="key"/>.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores every (key, data) pair in <paramref name="items"/> in one logical operation instead of one round trip
    /// per record. The default implementation just calls <see cref="SaveAsync"/> once per item in order, so every
    /// existing <see cref="IWorldStore"/> implementation - including a consumer-owned one written before this member
    /// existed - keeps compiling and behaving correctly unchanged; it just does not get the batching win until it
    /// overrides this member.
    ///
    /// <para>A backend that can genuinely batch (a single transaction, a multi-row statement) should override this
    /// for the real round-trip saving. When it does, prefer making the batch atomic - all rows land or none do -
    /// over a partial write: a caller that treats a faulted <see cref="SaveManyAsync"/> as "nothing in this batch is
    /// durably saved yet, keep every item dirty and retry the whole batch next pass" is then correct both against
    /// the default per-item loop (where some earlier items may have actually landed - retrying just re-saves
    /// unchanged data, which is harmless) and against an overridden atomic batch (where none did).</para>
    /// </summary>
    async Task SaveManyAsync(IReadOnlyList<(string Key, byte[] Data)> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach ((string key, byte[] data) in items)
            await SaveAsync(key, data, cancellationToken).ConfigureAwait(false);
    }
}
