using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

// The SAVE half of StatePersistence: save-on-leave, the periodic dirty pass and its batched round trip, the
// per-key write publication a rejoin's load waits behind, and the flush that reaches quiescence. See
// StatePersistence.cs for the type doc.
public sealed partial class StatePersistence<TState>
{
    // The key this layer files a connection's record under. For a connection with a verified subject that IS the
    // account id. A TOKENLESS one arrives as guest:{slot} and a slot is a seat the next connection inherits, so that
    // key names a chair and a record filed under it lands on whoever sits down next (#647): it is therefore never a
    // store key. With config.PersistGuests off this answers false and the connection is not persisted, and with it on
    // the answer is the durable key minted for this session at join.
    private bool TryResolveKey(int slot, string accountId, out string key)
    {
        if (!PositionHintCache.IsGuestAccount(accountId)) { key = accountId; return true; }
        if (config.PersistGuests && guestKeys.TryGetValue(slot, out string? minted)) { key = minted; return true; }
        key = string.Empty;
        return false;
    }

    // Captures the game blob (on the server thread - the caller is on the server thread) and encodes the full record.
    // The capture hook is handed the RESOLVED key (TryResolveKey), never the id the head handed us, because that is
    // the durable id the record is keyed by and the game reads it as one. The two are the same string for every
    // connection with a verified subject. They diverge only for a tokenless one on a server that set PersistGuests,
    // where the head's id is the seat guest:{slot} and the key is the durable id minted for this session, so passing
    // the head's would hand the game back the seat identity #647 exists to keep out of the store.
    private byte[] BuildRecordBytes(int slot, string key, in TState state)
    {
        byte[]? game = config.CaptureGameState?.Invoke(slot, key);
        return binding.Encode(state, game);
    }

    private void OnPlayerLeaving(int slot, string accountId, TState finalState)
    {
        if (!TryResolveKey(slot, accountId, out string key)) return;   // a tokenless connection this layer does not persist (#647)
        if (loadsInFlight.ContainsKey(key)) return;         // load still outstanding: the stored record was never applied, so IT - not this pre-restore live state - is the truth worth keeping
        // Past that guard the live state IS the truth, so it is both what gets saved and what the next join is
        // seeded from. Deliberately not recorded above it: a hint taken from never-restored state would seed the
        // rejoin at the default spawn and then take the restore teleport, which is the bug this exists to fix. The
        // hint is filed under the id the HOST will ask with at the next join, which for a guest key the cache
        // refuses outright - correctly, since nothing can present a minted guest id again.
        hints.Record(accountId, binding.PositionOf(finalState));
        Task save = SaveIfDirtyAsync(key, BuildRecordBytes(slot, key, finalState));
        if (PositionHintCache.IsGuestAccount(key))
        {
            Track(RetireGuestSessionAsync(slot, key, save));   // a minted guest key is never loaded, so nothing waits on it
            return;
        }
        Track(save);
        Track(ReleaseWhenSavedAsync(key, PublishSave(key, save)));   // the rejoin's load waits behind this write
    }

    // Registers an outstanding write under one key and hands back the entry a join will await. Chains onto whatever
    // was already outstanding for that key rather than replacing it, so the entry always covers EVERY write issued
    // so far under it: a periodic batch and a save-on-leave can genuinely overlap for one account, and a join that
    // awaited only the newer of the two would still be reading while the older was in the air. Server thread only.
    private Task PublishSave(string key, Task save)
    {
        Task tail = savesInFlight.TryGetValue(key, out Task? prev) && !prev.IsCompleted
            ? Task.WhenAll(prev, save)
            : save;
        savesInFlight[key] = tail;
        return tail;
    }

    // Releases a published entry once its write has landed. Faults are swallowed HERE on purpose: the write itself is
    // tracked separately and surfaces through OnStoreError, so rethrowing would report one store outage twice. This
    // task's only job is to stop the key being guarded, which a failed write must do just as a successful one does.
    private async Task ReleaseWhenSavedAsync(string key, Task tail)
    {
        try { await tail.ConfigureAwait(false); }
        catch { /* already observed and surfaced by whoever tracked the underlying write */ }
        savesInFlight.TryRemove(new KeyValuePair<string, Task>(key, tail));
    }

    // A minted guest key belongs to one session and is unreachable once that session ends, so after its final save
    // there is nothing left to compare a future record against or to resolve the seat to. Drop both entries rather
    // than leak one of each per tokenless connection on a server that opted in. The pair overload is what makes this
    // safe against a successor that has already taken the seat: it removes only what THIS session put there.
    private async Task RetireGuestSessionAsync(int slot, string key, Task save)
    {
        try { await save.ConfigureAwait(false); }
        finally
        {
            lastSaved.TryRemove(key, out _);
            guestKeys.TryRemove(new KeyValuePair<int, string>(slot, key));
        }
    }

    private async Task SaveIfDirtyAsync(string key, byte[] data)
    {
        if (lastSaved.TryGetValue(key, out byte[]? prev) && prev.AsSpan().SequenceEqual(data))
            return;                                        // unchanged since last save
        await store.SaveAsync(Key(key), data).ConfigureAwait(false);
        lastSaved[key] = data;
    }

    /// <summary>Saves every joined player whose state changed since its last save.</summary>
    public void SaveDirtyPass()
    {
        List<(string key, byte[] data)>? dirty = null;
        foreach (int slot in new List<int>(server.JoinedSlots))
            if (server.TryGetAccountId(slot, out string accountId) &&
                TryResolveKey(slot, accountId, out string key) &&   // a tokenless connection this layer does not persist (#647)
                !loadsInFlight.ContainsKey(key) &&             // load outstanding: skip so this pass can't overwrite the stored record with pre-restore state
                server.TryGetPlayerState(slot, out TState state))
            {
                byte[] data = BuildRecordBytes(slot, key, state);
                if (lastSaved.TryGetValue(key, out byte[]? prev) && prev.AsSpan().SequenceEqual(data))
                    continue;                                    // unchanged since last save
                (dirty ??= new List<(string, byte[])>()).Add((key, data));
            }
        if (dirty is null) return;
        Task batch = SaveManyDirtyAsync(dirty);
        Track(batch);
        // The batch is an outstanding write for every account in it, so a rejoin landing while it is in the air waits
        // behind it exactly as it waits behind a save-on-leave.
        foreach ((string key, byte[] _) in dirty) Track(ReleaseWhenSavedAsync(key, PublishSave(key, batch)));
    }

    // Batches every dirty account's record into one store round trip (one SaveManyAsync call instead of N
    // SaveAsync calls). lastSaved is advanced per account only AFTER the whole batch lands, so a faulted/canceled
    // batch leaves every account in it dirty for the next pass - the same "never mark a record clean before it is
    // actually saved" guarantee the old per-account SaveIfDirtyAsync gave, just at batch grain: one failed round
    // trip means the whole pass retries next interval, not only the one record that actually caused the fault.
    private async Task SaveManyDirtyAsync(List<(string key, byte[] data)> dirty)
    {
        var items = new List<(string Key, byte[] Data)>(dirty.Count);
        foreach ((string key, byte[] data) in dirty) items.Add((Key(key), data));
        await store.SaveManyAsync(items).ConfigureAwait(false);
        foreach ((string key, byte[] data) in dirty) lastSaved[key] = data;
    }

    /// <summary>Awaits all in-flight loads/saves, then applies any pending loaded state. Call on shutdown (or in
    /// tests) to reach a quiescent, fully-persisted point. Invoke from the server thread / when the loop is idle.</summary>
    public async Task FlushAsync()
    {
        // Loop rather than a single drain-then-await: DrainApplyQueue can itself Track a quarantine SaveAsync (a
        // loaded record that fails validation), and awaiting pending can complete a LoadOnJoinAsync that enqueues a
        // fresh applyQueue item. Either one leaves work this call has not yet awaited/applied, so keep going until a
        // drain finds nothing new to track - only then is the call actually quiescent, mirroring CellPersistence's
        // FlushAsync shape.
        while (true)
        {
            DrainApplyQueue();

            Task[] tasks;
            lock (pendingLock) { tasks = pending.ToArray(); pending.Clear(); }
            if (tasks.Length == 0) break;

            await AwaitAndObserve(tasks).ConfigureAwait(false);
        }
    }

    // Awaits every given task to completion, then observes it. Unlike a bare Task.WhenAll (which rethrows the
    // first fault and, having already cleared those tasks out of pending, would unwind FlushAsync's loop before
    // it reaches quiescence - the caller gets an exception instead of ever finding out the flush actually
    // landed) this surfaces EVERY faulted/canceled task through OnStoreError and never throws, so a store outage
    // during shutdown still lets the loop keep draining. A faulted save's account stays dirty and is retried on
    // the next pass. Mirrors CellPersistence.AwaitPendingAsync.
    private async Task AwaitAndObserve(Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch { /* individual faults are observed + surfaced per-task below, not rethrown */ }
        List<Exception>? failures = null;
        foreach (Task t in tasks)
            if (t.IsFaulted || t.IsCanceled)
                (failures ??= new List<Exception>()).Add(t.Exception?.GetBaseException() ?? new TaskCanceledException());
        if (failures is not null)
            foreach (Exception ex in failures) OnStoreError?.Invoke(ex);
    }
}
