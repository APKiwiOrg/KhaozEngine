namespace KhaozEngine.ItemInstances;

/// <summary>
/// What one set of modifier lines came from, spec 11.4, and the key the fold is ORDERED by. The order is
/// <c>(SourceKind, Ordinal, InstanceId, ModifierIndex)</c>, always, where the modifier index is the line's
/// position inside its own source.
/// <para>
/// <b>The order is the displayed number.</b> Integer multiplication with rounding at each step is not
/// associative, so <c>(a * x) * y</c> and <c>(a * y) * x</c> can differ by one unit (contracts 13.2). That
/// is why the key is fixed here rather than left to whichever order a caller happened to equip in.
/// </para>
/// <para>
/// <b><see cref="InstanceId"/> is in the key so a tie still orders.</b> The engine's own four kinds cannot
/// tie on kind and ordinal, and a game source can, which is the same defensive choice a world hash makes by
/// sorting before it digests.
/// </para>
/// </summary>
/// <param name="SourceKind">One of the kinds named by the constants on this type.</param>
/// <param name="Ordinal">The position within the kind, which spec 11.4's table fixes per kind.</param>
/// <param name="InstanceId">The item instance the lines came from, or 0 where no instance owns them.</param>
public readonly record struct StatSourceKey(byte SourceKind, int Ordinal, long InstanceId)
{
    /// <summary>Base and implicit lines of a worn item. The ordinal is the worn slot index, ascending.</summary>
    public const byte WornItemKind = 1;

    /// <summary>Affixes of a worn item. The ordinal is the worn slot, then the index in the sorted list.</summary>
    public const byte AffixKind = 2;

    /// <summary>Enchantments of a worn item, ordered the same way affixes are.</summary>
    public const byte EnchantmentKind = 3;

    /// <summary>
    /// Lines of an item SOCKETED into a worn item. The ordinal is the worn slot, then the socket index in
    /// AUTHORED order, because socket order is authored and never sorted.
    /// </summary>
    public const byte SocketedItemKind = 4;

    /// <summary>
    /// RESERVED for passives, and deliberately unassigned in v1. Reserving it is what lets a passive tree
    /// arrive later without moving a single stored or displayed number, which is why this is a base rather
    /// than a system.
    /// </summary>
    public const byte ReservedPassiveKind = 5;

    /// <summary>RESERVED for buffs and auras, unassigned in v1 for the same reason.</summary>
    public const byte ReservedBuffKind = 6;

    /// <summary>
    /// The first kind a GAME may register. Everything from here to 255 is the game's, and its ordinals are
    /// the game's own and must be deterministic.
    /// </summary>
    public const byte FirstGameKind = 7;
}
