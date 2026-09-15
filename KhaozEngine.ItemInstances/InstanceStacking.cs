using System;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Spec 4.6's stacking rule, as a byte compare. Two occupied entries merge when the definition matches, the
/// game's predicate says yes for it, neither is quarantined, and the two payloads are byte identical.
/// <para>
/// <b>Rule 4 is a memcmp rather than a structural comparison ONLY because the payload is canonical</b>
/// (spec 3.2): one set of properties has exactly one byte form, so equal bytes IS equal properties and
/// nothing here decodes two payloads to compare them. Two items differing only in a field neither build
/// understands do not merge, which is the conservative answer contracts 9.4 requires.
/// </para>
/// <para>
/// The rules are checked cheapest first rather than in the numbered order of 4.6, because the answer is the
/// same either way and the game's predicate is a delegate call. The one genuinely expensive check, the walk
/// that looks for durability and sockets, runs last and over ONE side, which the byte equality above it has
/// already made the same side as the other.
/// </para>
/// </summary>
public static class InstanceStacking
{
    /// <summary>
    /// Whether two occupied entries merge, which is all four rules of spec 4.6 plus the runtime
    /// belt-and-braces of the same section: an entry carrying kind 5 or kind 132 never merges whatever the
    /// predicate says, because a definition can gain durability or sockets AFTER its items exist and
    /// publish can only see the definitions it publishes.
    /// </summary>
    /// <param name="left">One entry. An empty slot is not an entry and never merges.</param>
    /// <param name="right">The other.</param>
    /// <param name="stackable">The game's rule for whether a definition merges into one slot. Consulted per
    /// operation and never cached, so a game whose rule reads its catalog sees catalog edits live. It
    /// arrives as an argument rather than as a field for exactly that reason.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stackable"/> is null.</exception>
    public static bool CanMerge(in ItemSlot left, in ItemSlot right, Func<int, bool> stackable)
    {
        ArgumentNullException.ThrowIfNull(stackable);

        if (left.IsEmpty || right.IsEmpty) return false;
        if (left.Stack.ItemId != right.Stack.ItemId) return false;
        if (left.Quarantined || right.Quarantined) return false;
        if (!stackable(left.Stack.ItemId)) return false;
        if (!ItemInstancePayload.SequenceEqual(left.Payload.Span, right.Payload.Span)) return false;

        // One side only: the bytes are equal, so the two walks would read the same kinds.
        return !CarriesUnstackableProperty(left.Payload.Span);
    }

    /// <summary>
    /// Whether a payload carries durability (kind 5) or sockets (kind 132), which spec 4.6 makes an
    /// unconditional refusal to merge. A payload that does not decode answers true, because a merge is a
    /// destructive operation and bytes nobody can read are not bytes to merge on.
    /// <para>
    /// It runs the payload's OWN field walk rather than a second TLV reader, and reads no field body: the
    /// question is which kinds are present, which is the one thing a registry-free walk already answers.
    /// </para>
    /// </summary>
    /// <param name="payload">The canonical payload, which may be empty. An empty payload carries neither.</param>
    public static bool CarriesUnstackableProperty(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return false;

        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(payload, fields, out int fieldCount, out _)) return true;

        for (int index = 0; index < fieldCount; index++)
        {
            if (fields[index].Kind is InstancePropertyKind.Durability or InstancePropertyKind.Sockets)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The ARITHMETIC of a merge, and never its rules: ask <see cref="CanMerge"/> first. The surviving
    /// instance id is the numerically LOWER of the two, so the merge is commutative and a replay in the
    /// other order produces the same id, and the count saturates at <see cref="int.MaxValue"/> exactly as
    /// <c>ItemContainer.Add</c> does rather than overflowing.
    /// <para>
    /// A merge DESTROYS an instance id, which is the one place this design weakens the contracts'
    /// traceability argument. The mitigation is an event rather than a field: a merge emits
    /// <c>stack-merged</c> naming BOTH ids and the resulting count, so the destroyed id is answerable from
    /// the journal for the retention window.
    /// </para>
    /// </summary>
    /// <param name="destination">The entry that survives. Its payload is the merged entry's, which costs
    /// nothing to choose because rule 4 has already made the two byte identical.</param>
    /// <param name="source">The entry merging in.</param>
    /// <param name="remainder">The units that did not fit, which are the caller's to drop, refuse or spill.
    /// That is a game rule this kernel deliberately does not have.</param>
    /// <exception cref="ArgumentException">Either side is empty, which is a caller bug rather than a fact
    /// about the items.</exception>
    public static ItemSlot Merge(in ItemSlot destination, in ItemSlot source, out int remainder)
    {
        if (destination.IsEmpty) throw new ArgumentException("An empty slot is not a merge destination.", nameof(destination));
        if (source.IsEmpty) throw new ArgumentException("An empty slot is not a merge source.", nameof(source));

        long room = int.MaxValue - (long)destination.Stack.Count;
        int moved = (int)Math.Min(room, source.Stack.Count);
        remainder = source.Stack.Count - moved;

        return destination with
        {
            Stack = new ItemStack(
                destination.Stack.ItemId,
                destination.Stack.Count + moved,
                ItemStack.MergeInstanceId(destination.Stack.InstanceId, source.Stack.InstanceId)),
        };
    }
}
