using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.WorldStore;

// The LOAD-ON-JOIN half of StatePersistence: the per-session guard, the store read that waits behind the account's
// own outstanding write, and the server-thread drain that re-resolves identity, validates and then applies or
// quarantines the record. See StatePersistence.cs for the type doc.
public sealed partial class StatePersistence<TState>
{
    private void OnPlayerJoined(int slot, string accountId)
    {
        if (PositionHintCache.IsGuestAccount(accountId))
        {
            // A tokenless connection is handed to us as guest:{slot}, which names the chair and not the person
            // (#647). It is never a store key. Off (the default), nothing about this connection is persisted at all,
            // which is also why it needs no guard: there is no stored record to protect. On, the seat key is swapped
            // for one minted here and unique to THIS session, and no load is issued for it either - a key created
            // this instant cannot have a stored record waiting under it.
            if (config.PersistGuests)
                guestKeys[slot] = PositionHintCache.GuestAccountPrefix + Guid.NewGuid().ToString("N");
            return;
        }
        long token = Interlocked.Increment(ref nextJoinToken);
        loadsInFlight[accountId] = token;                   // guard the account until THIS session's record applies (or its load returns null)
        // Read the account's outstanding write, if any, on the SERVER THREAD, before the load task starts: the
        // save-on-leave that has to be waited for was published from this same drain, one event earlier.
        savesInFlight.TryGetValue(accountId, out Task? outstandingSave);
        Track(LoadOnJoinAsync(slot, accountId, token, outstandingSave));
    }

    private async Task LoadOnJoinAsync(int slot, string accountId, long token, Task? outstandingSave)
    {
        if (outstandingSave is not null)
        {
            // A write for this key is still in the air - on the kick path it is the session this one just displaced,
            // saving where the player actually was. Reading now would hand this session whatever the store held
            // BEFORE that write, restore the player onto it, and let the next periodic pass write the rollback back
            // down as the truth. A FAILED write must not strand the join: log it and read anyway, which yields the
            // older record and is the same outage shape a failed save already has.
            try { await outstandingSave.ConfigureAwait(false); }
            catch (Exception ex)
            {
                config.Diagnostic?.Invoke(
                    $"the save outstanding for account '{accountId}' failed, so its load-on-join on slot {slot} "
                    + "reads whatever the store still holds, which may predate that session's last state.", ex);
            }
        }
        byte[]? data = await store.LoadAsync(Key(accountId)).ConfigureAwait(false);
        if (data is null)                                  // no save -> keep wherever the join built them (spawn, or this account's hint)
        {
            ClearGuard(accountId, token);                  // brand-new player: nothing stored to clobber, drop the guard now
            return;
        }
        // The baseline is NOT set here any more: it moves to apply time (DrainApplyQueue), so a quarantined record is
        // never marked clean. A read fault propagates out of this task and the guard deliberately stays set (outage
        // retry). An undecodable record read fine but won't parse, so route it through quarantine instead of faulting
        // the task and stranding the guard forever.
        TState state;
        byte[]? game;
        try
        {
            // A decoder that answers false and one that throws are the same answer, and both land here rather than
            // faulting the task: a record that will not parse has to reach the drain to be quarantined.
            if (!binding.Decode(data, out state, out game))
            {
                applyQueue.Enqueue(new PendingApply(slot, accountId, token, data, default!, null, "undecodable record"));
                return;                                    // guard stays set until the drain quarantines and clears it
            }
        }
        catch (Exception ex)
        {
            applyQueue.Enqueue(new PendingApply(slot, accountId, token, data, default!, null, $"undecodable record: {ex.Message}"));
            return;                                        // guard stays set until the drain quarantines and clears it
        }
        applyQueue.Enqueue(new PendingApply(slot, accountId, token, data, state, game, null));   // validated + guard cleared in DrainApplyQueue
    }

    // Drops the guard only when it is still the one THIS session put there. A load left over from a superseded
    // session must never reopen the window its successor's load is holding open (#654), and the pair overload is what
    // makes that mechanical rather than a rule every call site has to remember: a stale token matches nothing and
    // removes nothing.
    private void ClearGuard(string accountId, long token) =>
        loadsInFlight.TryRemove(new KeyValuePair<string, long>(accountId, token));

    // Re-resolves identity, then validates, then applies (or quarantines) loaded records on the server thread. A
    // record whose slot has been recycled to another account is dropped outright (see the loop below and #646).
    // Past that, a record fails validation if it
    // carries a decode failure, its position is out of bounds, or the game's blob verdict rejects it - checked in that
    // order, first hit wins. On failure the WHOLE record is copied verbatim to the quarantine key, the resume hint is
    // forgotten, the player is RESET to the host's configured spawn as a teleport, the guard clears and the baseline
    // is untouched (so that fresh state overwrites the bad primary next pass), and OnRecordQuarantined fires. On
    // success: position first (as a teleport only when it actually moves the player - see below), then the opaque
    // game blob (only when present and a hook is set), then advance the baseline to the loaded bytes, record the
    // resume hint, then clear the guard. Shared by Update and FlushAsync so both paths validate identically.
    private void DrainApplyQueue()
    {
        while (applyQueue.TryDequeue(out PendingApply a))
        {
            // SESSION first, because it is the narrower of the two identities: an account can be sitting in the seat
            // and still not be the party this record was read for. loadsInFlight holds the token of the account's
            // CURRENT join, so a load issued by a session that has since ended (a leave and rejoin inside one store
            // read) fails to match, and a load whose account has no guard at all is stale by construction - the only
            // thing that clears a guard is that same session's own load landing. Either way the record is dropped:
            // it carries what the store held before the live session started, so applying it writes over everything
            // that session has done since, and clearing the guard on the way out would reopen the save window its
            // sibling load is still holding (#654). The guard is deliberately left exactly as it is.
            bool guarded = loadsInFlight.TryGetValue(a.AccountId, out long current);
            if (!guarded || current != a.Token)
            {
                // Name the state actually found, the way the seat check below does. The two halves reach here for
                // different reasons and leave the account in different places: a live session's own load is still
                // outstanding and still guarding the record, or there is no guard left at all because that load has
                // already landed (or the account has since left). Reporting the first for both read as a guarantee
                // in the landing-last case, where the guard is long gone by the time the stale load arrives.
                config.Diagnostic?.Invoke(
                    $"load-on-join for account '{a.AccountId}' dropped: it was read on slot {a.Slot} for an "
                    + "earlier session that has since been superseded, so the stored record was not applied over "
                    + "newer live state, and "
                    + (guarded
                        ? "the current session's own load is still outstanding and still guards the account."
                        : "the account carries no guard any more, its current session's load having already landed."),
                    null);
                OnLoadApplyDropped?.Invoke(a.AccountId, a.Slot);
                continue;
            }

            // Then the SEAT. PendingApply carries the slot the account joined on, and
            // that slot is a seat rather than a name: the SlotAllocator hands the lowest free one to the next
            // connection, so a leave during the store read frees it and the load can land on a stranger. Re-resolve
            // the seat's current occupant and drop the record when it is not this account's any more (#646). It has
            // to run BEFORE validation because the quarantine path PLACES the slot at the configured spawn, which on
            // a recycled slot would teleport the new occupant for a record that was never theirs. A bad record
            // belonging to a departed account simply stays in the store, un-quarantined, and is re-read (and then
            // quarantined) on that account's next join, which is the same shape a store outage already has.
            bool held = server.TryGetAccountId(a.Slot, out string occupant);
            if (!held || !string.Equals(occupant, a.AccountId, StringComparison.Ordinal))
            {
                // Deliberately NOT clearing loadsInFlight: the record was never applied, so it is still the truth for
                // that account and the guard is what stops live state overwriting it. The account is not connected
                // under this slot any more, so the guard costs nothing until it rejoins and the retry clears it.
                config.Diagnostic?.Invoke(
                    $"load-on-join for account '{a.AccountId}' dropped: slot {a.Slot} now holds "
                    + (held ? $"account '{occupant}'" : "no player")
                    + ", so the stored record was not applied and stays guarded for that account's next join.", null);
                OnLoadApplyDropped?.Invoke(a.AccountId, a.Slot);
                continue;
            }

            // The STATE's shape first, through the binding (a play area, a facing in range), then the opaque blob's
            // own verdict, which is the only one of the two that needs to know whose blob it is. Ordered, not
            // combined: first hit wins and the second check never runs, exactly as before.
            string? failure = a.DecodeFailure;
            failure ??= binding.Validate(a.State, a.Game);
            if (failure is null && a.Game is { Length: > 0 } && config.ValidateGameState is { } validate)
                failure = validate(a.Slot, a.AccountId, a.Game);

            if (failure is not null)
            {
                Track(store.SaveAsync(config.QuarantineKeyPrefix + Key(a.AccountId), a.Raw));   // copy the bad record verbatim, awaited by FlushAsync
                // Reset to the configured spawn RATHER than simply declining to place the player. Before the join
                // seed, "not applied" meant the player kept the spawn the join built them at and quarantine needed
                // no placement at all. It does not mean that any more: a rejoin is built at the resume hint, which
                // no check here ever saw (the binding validates the LOADED record, never the hint), so
                // declining would leave a rejected record's player standing on an unvalidated position - and the
                // decode-failure and ValidateGameState triggers reach that state with no bounds divergence at all.
                // Forget the hint first, so a further rejoin cannot re-seed the position this record was rejected
                // for, and place as a genuine teleport: policy moved the player, and the client should cut.
                hints.Forget(a.AccountId);
                if (server.TryGetConfiguredSpawn(a.Slot, out TState spawn))
                    server.SetPlayerState(a.Slot, spawn, teleport: true);
                ClearGuard(a.AccountId, a.Token);              // quarantined, so the account is now free to dirty-save the fresh spawn over the bad primary
                OnRecordQuarantined?.Invoke(a.AccountId, failure);
                continue;                                      // NOT applied, baseline NOT advanced
            }

            // Placing a loaded player is a teleport only when it MOVES them. A rejoiner whose join was seeded from
            // the resume hint is already standing on the loaded position, and advancing the epoch there would
            // report a second teleport for a move of nothing - the half of the double-teleport the seed cannot fix
            // on its own (#642). Both sides are absolute world metres (TryGetPlayerState hands out absolute, and a
            // stored record is written absolute), so the comparison needs no frame conversion. A host that cannot
            // read the state back is treated as a move, which is the pre-17.37.0 behaviour.
            bool moved = !server.TryGetPlayerState(a.Slot, out TState live)
                || Vector3.Distance(binding.PositionOf(live), binding.PositionOf(a.State)) > config.QuietRestoreDistance;
            server.SetPlayerState(a.Slot, a.State, teleport: moved);
            if (a.Game is { Length: > 0 } && config.ApplyGameState is { } apply)
                apply(a.Slot, a.AccountId, a.Game);
            lastSaved[a.AccountId] = a.Raw;                 // loaded == clean baseline, set at apply time (never for a quarantined record)
            hints.Record(a.AccountId, binding.PositionOf(a.State));   // the restored position is now the account's last known one
            ClearGuard(a.AccountId, a.Token);              // restore applied, the account is now safe to dirty-save
        }
    }
}
