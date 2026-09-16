namespace KhaozEngine.ItemInstances;

/// <summary>
/// Spec 11.4's ORDINAL half, which is the part of the source table that is arithmetic rather than a
/// constant. The KINDS themselves are not restated here: they are the constants on
/// <see cref="StatSourceKey"/> (<see cref="StatSourceKey.WornItemKind"/> and its five siblings) and a
/// second copy of them would be a second thing to keep true.
/// <para>
/// <b>Kind 1's ordinal is the worn slot itself and kinds 2, 3 and 4 pack a slot and an index.</b> Spec
/// 11.4 says "worn slot index, then affix index in the sorted list" and leaves the packing open, so it is
/// fixed HERE and nowhere else: <c>ordinal = (wornSlot * 256) + entryIndex</c>. The stride is 256 because
/// an affix list's count is a BYTE on the wire, so 255 is the widest list a payload can carry, and a
/// socket list is bounded well under that by the 512 byte payload cap. Slot 0 index 255 is 255 and slot 1
/// index 0 is 256, so no two entries of different slots can land on one ordinal, and that is what stops
/// two worn items' affixes folding as one source with one of them silently gone.
/// </para>
/// <para>
/// <b>Changing the packing changes a DISPLAYED number.</b> The fold order is
/// <c>(SourceKind, Ordinal, InstanceId, ModifierIndex)</c> and integer multiplication with rounding at
/// each step is not associative (contracts 13.2), so a different packing reorders the More loop and can
/// move a value by one unit. It is durable in the same sense the ordinals themselves are.
/// </para>
/// </summary>
public static class InstanceStatSourceKind
{
    /// <summary>How many entry ordinals one worn slot owns, which is the affix count's byte width plus one.</summary>
    public const int EntryStride = 256;

    /// <summary>The widest entry index one worn slot can carry.</summary>
    public const int MaxEntryIndex = EntryStride - 1;

    /// <summary>The highest worn slot the packing can state without leaving int range.</summary>
    public const int MaxWornSlot = (int.MaxValue / EntryStride) - 1;

    /// <summary>
    /// The ordinal of a worn item's own base and implicit lines, <see cref="StatSourceKey.WornItemKind"/>,
    /// which is the worn slot UNPACKED because a worn item has exactly one set of them.
    /// </summary>
    /// <param name="wornSlot">The worn slot index, ascending.</param>
    public static int WornItemOrdinal(int wornSlot) => wornSlot;

    /// <summary>
    /// One packed ordinal for <see cref="StatSourceKey.AffixKind"/>,
    /// <see cref="StatSourceKey.EnchantmentKind"/> or <see cref="StatSourceKey.SocketedItemKind"/>.
    /// </summary>
    /// <param name="wornSlot">The worn slot index, 0 to <see cref="MaxWornSlot"/>.</param>
    /// <param name="entryIndex">The entry index inside that slot, 0 to <see cref="MaxEntryIndex"/>.</param>
    public static int EntryOrdinal(int wornSlot, int entryIndex) => (wornSlot * EntryStride) + entryIndex;

    /// <summary>The worn slot one packed ordinal names.</summary>
    /// <param name="ordinal">An ordinal from <see cref="EntryOrdinal"/>.</param>
    public static int WornSlotOf(int ordinal) => ordinal / EntryStride;

    /// <summary>The entry index one packed ordinal names.</summary>
    /// <param name="ordinal">An ordinal from <see cref="EntryOrdinal"/>.</param>
    public static int EntryIndexOf(int ordinal) => ordinal % EntryStride;
}
