using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The three STANDING rules of spec 10.3: refusals no currency can opt out of, and which therefore cannot
/// live in an authored guard set.
/// <para>
/// <b>1. A corrupted item cannot be modified at all.</b> Every primitive that writes any part of the
/// payload refuses an item whose kind 1 bit 0 is set, whatever the currency's guard set says, because
/// "corrupted" means "cannot be modified further" and a refusal a currency can FORGET is not that. It is
/// seated at the working copy's ONE write door, so it covers every primitive and every
/// <c>ICraftOperation</c> by construction rather than by fourteen remembered checks.
/// </para>
/// <para>
/// <b>2. A legacy affix ENTRY is frozen, and that is a second rule rather than a restatement of the
/// first.</b> No primitive and no game operation rewrites any part of an entry whose mod row carries
/// <c>legacy</c>: not its roll position, not its tier, not its flags. Contracts 5.4 reads "a legacy id that
/// can never be generated or crafted again" conservatively, and the reason is worth keeping next to the
/// code: an earlier draft scoped the refusal to primitives that ADD a mod, which left <c>RerollValues</c>
/// free to draw a fresh position against a legacy tier's preserved range, over and over, until the roll sat
/// at 65,535. The mechanism chosen to FREEZE old rolls would have become a farm for them. A legacy entry
/// can still be REMOVED, which is a loss rather than a gain and which no currency can undo, because the
/// generator can never place the row again.
/// </para>
/// <para>
/// <b>3. A legacy mod ROW can never be added.</b> Every primitive that would add a mod refuses a legacy row
/// regardless of the guard set. The candidate tables leave legacy rows out entirely, so a DRAW can never
/// reach one, and this is the same refusal stated where the WRITE is, for the primitives that name a mod id
/// outright and for a game operation writing its own list.
/// </para>
/// <para>
/// Rules 2 and 3 share one door, <see cref="CheckAffixWrite"/>, because they are two readings of the same
/// comparison: what the incoming list says about an entry the item already has, and what it says about one
/// it does not.
/// </para>
/// </summary>
public static class CraftStandingRules
{
    /// <summary>Kind 1 bit 0, corrupted, which is the bit rule 1 reads.</summary>
    public const int CorruptedFlagBit = 0;

    /// <summary>
    /// Whether kind 1 bit 0 is set, which is rule 1's question and is
    /// <see cref="CraftGuardKind.IsCorruptible"/> read the other way up.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    public static bool IsCorrupted(in CraftWorkingCopy copy)
        => copy.TryGetScalar(InstancePropertyKind.Flags, out ulong flags)
            && (flags & (1ul << CorruptedFlagBit)) != 0;

    /// <summary>
    /// Rule 1 at the door of a craft, for a caller that wants the refusal BEFORE a step runs rather than at
    /// the write the step ends in. The working copy checks the same thing at every write, so this is the
    /// early answer and never the only one.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <returns>The refusal, or null when the item may still be modified.</returns>
    public static CraftRefusal? RefuseCorrupted(ref CraftWorkingCopy copy)
        => IsCorrupted(in copy)
            ? copy.Refuse(CraftGuardEvaluator.Failure(CraftGuardKind.IsCorruptible))
            : null;

    /// <summary>
    /// Whether one entry is frozen, which is rule 2's question: its mod row carries <c>legacy</c> at this
    /// content version.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="modId">The entry's mod id.</param>
    public static bool IsFrozen(in CraftWorkingCopy copy, int modId) => copy.IsLegacyMod(modId);

    /// <summary>
    /// Rule 3 at the point of an ADD, for a primitive that names a mod id outright and would rather refuse
    /// before it builds a list.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="modId">The mod the step would add.</param>
    /// <returns>The refusal, or null when the row may be added.</returns>
    public static CraftRefusal? RefuseLegacyAddition(ref CraftWorkingCopy copy, int modId)
        => IsFrozen(in copy, modId)
            ? copy.Refuse(CraftGuardEvaluator.Failure(CraftGuardKind.NotLegacy))
            : null;

    /// <summary>
    /// Rules 2 and 3 over one affix list write, comparing the incoming list against the decoded one ENTRY
    /// BY ENTRY for the entries that name a legacy mod.
    /// <para>
    /// An entry the item already carries may be written back only IDENTICALLY, which is rule 2, and an
    /// entry naming a legacy row the item does NOT carry is an addition, which is rule 3. An entry the
    /// incoming list drops is a removal, which both rules permit.
    /// </para>
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="kind">131 for affixes or 133 for enchantments.</param>
    /// <param name="incoming">The list the craft wants to write.</param>
    /// <returns>The refusal, or null when the write is allowed.</returns>
    public static CraftRefusal? CheckAffixWrite(
        ref CraftWorkingCopy copy,
        ushort kind,
        scoped ReadOnlySpan<InstanceAffix> incoming)
    {
        Span<InstanceAffix> current = stackalloc InstanceAffix[CraftWorkingCopy.MaxAffixes];
        int held = copy.ReadAffixes(kind, current);
        foreach (InstanceAffix entry in incoming)
        {
            if (!IsFrozen(in copy, entry.ModId))
            {
                continue;
            }

            int seat = -1;
            for (int index = 0; index < held; index++)
            {
                if (current[index].ModId == entry.ModId)
                {
                    seat = index;
                    break;
                }
            }

            if (seat < 0)
            {
                return copy.Refuse(CraftGuardEvaluator.Failure(CraftGuardKind.NotLegacy));
            }

            if (current[seat] != entry)
            {
                return copy.Refuse(new CraftRefusal(CraftRefusalKind.LegacyEntryFrozen, entry.ModId));
            }
        }

        return null;
    }
}
