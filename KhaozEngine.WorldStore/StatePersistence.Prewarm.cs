using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

// The BOOT half of StatePersistence: filling the resume hints from the store before the first join, so the quiet
// rejoin survives a process restart. See StatePersistence.cs for the type doc.
public sealed partial class StatePersistence<TState>
{
    /// <summary>
    /// Fills <see cref="Hints"/> from the store, newest record first, so the first rejoin after a process restart is
    /// seeded where the player left rather than at the configured spawn. Returns how many accounts were seeded, for
    /// a host that wants to log it.
    ///
    /// <para>Call it at BOOT, on the server thread, and await it before the head starts polling.
    /// <see cref="PositionHintCache"/> is not thread-safe and this method writes to it from its own continuations,
    /// so a pre-warm racing a live join is a data race. It is the same rule and the same call site as
    /// <c>CellPersistence.PreloadAsync</c>.</para>
    ///
    /// <para>A store that is not an <see cref="IEnumerableWorldStore"/> is a no-op returning 0, so this is safe to
    /// call unconditionally. <paramref name="max"/> at or below zero means
    /// <see cref="PersistenceCoreConfig.ResumeHintCapacity"/>, which is also the ceiling worth reading: the cache
    /// holds that many and evicts past it, so a bigger read is work thrown away.</para>
    ///
    /// <para>Every record is put through the same checks the LOAD path applies before it becomes a hint, and that
    /// is the part a consumer-written pre-warm gets wrong. The join seed builds the player ON the hint, and nothing
    /// validates a hint (<see cref="PersistenceBinding{TState}.Validate"/> vets the loaded record, never the hint),
    /// so seeding a record the load path would quarantine puts a player exactly where the quarantine exists to stop
    /// them standing. A record that will not decode, or that the binding rejects, is skipped and does not count
    /// against <paramref name="max"/>. The one load-path check NOT applied is
    /// <see cref="PersistenceCoreConfig.ValidateGameState"/>, which is documented to read the live per-player object
    /// by slot and there is no slot at boot. A record whose blob that hook would reject is therefore seeded, and
    /// then quarantined at the join it seeded, which forgets the hint and resets the player: the same outcome as no
    /// pre-warm at all, one join later.</para>
    ///
    /// <para>Guest keys are skipped (<see cref="PositionHintCache.IsGuestAccount"/>): the cache refuses them anyway,
    /// so reading them is work whose result is thrown away. So are keys under
    /// <see cref="PersistenceCoreConfig.QuarantineKeyPrefix"/>, which the key-prefix filter already excludes unless
    /// the prefix is empty, and re-seeding a rejected position out of its own quarantine copy is the worst way for
    /// this to fail.</para>
    /// </summary>
    /// <param name="max">How many accounts to seed at most, or zero for
    /// <see cref="PersistenceCoreConfig.ResumeHintCapacity"/>.</param>
    /// <param name="cancellationToken">Cancels the enumeration and the record reads.</param>
    public async Task<int> PrewarmHintsAsync(int max = 0, CancellationToken cancellationToken = default)
    {
        if (store is not IEnumerableWorldStore enumerable) return 0;
        int limit = max > 0 ? max : config.ResumeHintCapacity;
        if (limit <= 0 || hints.Capacity <= 0) return 0;

        var candidates = new List<WorldStoreEntry>();
        await foreach (WorldStoreEntry entry in enumerable.EnumerateAsync(config.KeyPrefix, cancellationToken)
            .ConfigureAwait(false))
        {
            if (AccountIdOf(entry.Key) is not null) candidates.Add(entry);
        }
        // Newest first, because that is the half worth keeping when there are more records than the cache holds.
        candidates.Sort(static (a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));

        var accepted = new List<(string AccountId, TState State)>();
        foreach (WorldStoreEntry entry in candidates)
        {
            if (accepted.Count >= limit) break;
            if (AccountIdOf(entry.Key) is not string accountId) continue;
            byte[]? data = await store.LoadAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            if (data is null) continue;   // deleted between the enumeration and the read
            if (TryDecodeForHint(data, out TState state)) accepted.Add((accountId, state));
        }

        // Recorded OLDEST first, so the cache's recency order matches the store's. Recording newest first would put
        // the newest account at the eviction end and make it the first casualty of the next live save.
        for (int i = accepted.Count - 1; i >= 0; i--)
            hints.Record(accepted[i].AccountId, binding.PositionOf(accepted[i].State));
        return accepted.Count;
    }

    // The account id a stored key names, or null when the key is not one this layer would ever have written a
    // hintable record under.
    private string? AccountIdOf(string key)
    {
        string prefix = config.KeyPrefix ?? string.Empty;
        if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        // A quarantine copy is {QuarantineKeyPrefix}{KeyPrefix}{accountId}, so it only survives the check above when
        // the key prefix is empty. Excluded explicitly rather than by luck.
        string quarantine = config.QuarantineKeyPrefix ?? string.Empty;
        if (quarantine.Length > 0 && key.StartsWith(quarantine, StringComparison.Ordinal)) return null;
        string accountId = key[prefix.Length..];
        return accountId.Length > 0 && !PositionHintCache.IsGuestAccount(accountId) ? accountId : null;
    }

    // Decodes a stored record and applies the state-shaped half of the load path's validation. A decoder that
    // answers false and one that throws are the same answer here, exactly as they are on the load path, and both
    // mean "not a hint" rather than "fail the boot".
    private bool TryDecodeForHint(byte[] data, out TState state)
    {
        try
        {
            if (!binding.Decode(data, out state, out byte[]? game)) return false;
            if (binding.Validate(state, game) is not null)
            {
                state = default!;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            config.Diagnostic?.Invoke("a stored record was skipped by the resume-hint pre-warm: it would not decode.", ex);
            state = default!;
            return false;
        }
    }
}
