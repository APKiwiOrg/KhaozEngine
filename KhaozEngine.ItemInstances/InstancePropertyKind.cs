namespace KhaozEngine.ItemInstances;

/// <summary>
/// The property kind ids of spec 3.3, one <c>public const ushort</c> per kind, inside the three reserved
/// ranges of contracts 9.2: <c>0</c> is reserved and never a valid kind, <c>1</c> to <c>127</c> are engine
/// generic fields at one varint byte, <c>128</c> to <c>1023</c> are Scope B fields at two, and <c>1024</c>
/// and above belong to the game.
/// <para>
/// Kinds 2, 5, 130, 131 and 132 are PINNED by the worked byte example of contracts 9.8, which the payload
/// goldens reproduce byte for byte, so none of them moves. Every other assignment here is equally durable
/// once an item carrying it is stored: the kind id is IN the bytes.
/// </para>
/// </summary>
public static class InstancePropertyKind
{
    /// <summary>The first Scope B kind, and the ceiling the engine band stops one below.</summary>
    public const ushort FirstScopeBKind = 128;

    /// <summary>The first GAME kind. A game registers here and nowhere else, which the band rule enforces.</summary>
    public const ushort FirstGameKind = 1024;

    /// <summary>Kind 1, a bitfield: bit 0 corrupted, bit 1 mirrored, bit 2 fractured, the rest reserved and 0.</summary>
    public const ushort Flags = 1;

    /// <summary>Kind 2, the item level, 1 to 65535.</summary>
    public const ushort ItemLevel = 2;

    /// <summary>Kind 3, the quality, in whole percentage points.</summary>
    public const ushort Quality = 3;

    /// <summary>Kind 4, the current and maximum charge count.</summary>
    public const ushort Charges = 4;

    /// <summary>Kind 5, the current and maximum durability.</summary>
    public const ushort Durability = 5;

    /// <summary>Kind 6, the character or account subject the item is bound to. A subject of 0 is illegal.</summary>
    public const ushort BoundTo = 6;

    /// <summary>Kind 7, the material inputs an item was made from, in AUTHORED order, each an item id and a part count.</summary>
    public const ushort Materials = 7;

    /// <summary>Kind 8, an upgrade rank, the Tibia plus-one shape.</summary>
    public const ushort Tier = 8;

    /// <summary>Kind 128, the identification state byte and the revealed mask the gated kinds index into.</summary>
    public const ushort Identification = 128;

    /// <summary>Kind 129, the unique template row this item is an instance of.</summary>
    public const ushort UniqueTemplate = 129;

    /// <summary>Kind 130, a rarity rule row's ordinal.</summary>
    public const ushort Rarity = 130;

    /// <summary>Kind 131, the affix list, ascending by mod id.</summary>
    public const ushort Affixes = 131;

    /// <summary>Kind 132, the socket list, in AUTHORED order, each socket carrying a nested payload.</summary>
    public const ushort Sockets = 132;

    /// <summary>Kind 133, the enchantment list, the same entry layout as <see cref="Affixes"/>.</summary>
    public const ushort Enchantments = 133;

    /// <summary>Kind 134, the composed rare name: the rarity rule that formatted it and the words it used.</summary>
    public const ushort RareName = 134;
}
