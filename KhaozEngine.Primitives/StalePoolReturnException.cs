using System;

namespace KhaozEngine.Primitives;

/// <summary>
/// The refusal a finished rental gets instead of a silently stolen live one. Thrown by
/// <see cref="ObjectPool{T}.Return(in PoolRental{T})"/> when the handed-back
/// <see cref="PoolRental{T}"/> is not the rental that currently owns its slot: it was already returned, or
/// the slot has since been rented out again, or it came from a different pool, or it is the empty rental a
/// failed <c>TryRent</c> wrote.
/// <para>
/// It fails loud because the alternative is worse and much harder to find. A stale return that lands frees a
/// slot somebody else is actively using, so the pool then hands the SAME object to a second owner, and the
/// two of them scribble over each other with nothing logged and no exception until the corrupted state
/// surfaces somewhere unrelated. That is the failure mode
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/149">#149</see> describes.
/// </para>
/// <para>
/// Use <see cref="ObjectPool{T}.TryReturn"/> where a refusal is expected rather than exceptional: an
/// idempotent dispose that may return the same rental twice, or a <c>finally</c> block, where throwing would
/// replace whatever exception was already unwinding.
/// </para>
/// </summary>
public sealed class StalePoolReturnException : InvalidOperationException
{
    /// <summary>Build the refusal. <paramref name="slot"/> is the pool slot the rental named, or -1 when the
    /// rental did not come from the pool it was handed to (including the empty rental).</summary>
    public StalePoolReturnException(int slot)
        : base(BuildMessage(slot))
    {
        Slot = slot;
    }

    /// <summary>The pool slot the refused rental named, or -1 when the rental was not this pool's to return.</summary>
    public int Slot { get; }

    /// <summary>The message text, built here so a test can assert the wording without catching anything.</summary>
    public static string BuildMessage(int slot) =>
        slot < 0
            ? "A pooled item was returned to a pool it did not come from, or an empty rental was returned. "
              + "A PoolRental only ever goes back to the ObjectPool that handed it out, and a rental from a "
              + "TryRent that returned false is not a rental at all. Use TryReturn when a refusal is expected."
            : $"A finished rental of pool slot {slot} was returned. That rental is over: it was already "
              + "returned, or the slot has been rented out again since. Returning it would free the CURRENT "
              + "renter's item out from under it and let the pool hand the same object to a second owner, so "
              + "the return is refused instead. The usual cause is a reference kept past its Return (both a "
              + "catch and a finally path returning the same item is the classic one). Hold the PoolRental for "
              + "exactly as long as the rental lasts and return it once, or call TryReturn where a second "
              + "return is legitimate.";
}
