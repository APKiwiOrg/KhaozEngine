namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>store</c> content type: one shopkeeper kind and the two rates it trades at.
/// </summary>
/// <remarks>
/// A store row is what a shopkeeper IS to the economy. The npc kind binds it to the world, and the two rates
/// say what the shop pays and what it charges, both in basis points so nothing in the price path is ever a
/// float. The shelves are <c>store_shelf</c> rows rather than a repeated field here, by the rule that a
/// repeating child structure is its own content type with a key reference to its parent.
/// <para>
/// <c>Client</c> at the type level, because a shop panel prints a price beside every line before the player
/// commits to anything. A server-only rate would mean a round trip per drawn row.
/// </para>
/// <para>
/// <see cref="NpcKindField"/> is a raw number this package gives no meaning to. Which shopkeeper it names is
/// the game's, and the only rule here is that one kind resolves to one row.
/// </para>
/// </remarks>
public static class StoreContentType
{
    /// <summary>Id slots per chunk, the engine's floor. A world has a handful of shops.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The npc kind that opens this store, unique across live rows.</summary>
    public const string NpcKindField = "npc_kind";

    /// <summary>What the shop CHARGES, in basis points of the item's value.</summary>
    public const string SellRateBasisPointsField = "sell_rate_bp";

    /// <summary>What the shop PAYS, in basis points of the item's value.</summary>
    public const string BuyRateBasisPointsField = "buy_rate_bp";

    internal const int NpcKindIndex = 0;
    internal const int SellRateBasisPointsIndex = 1;
    internal const int BuyRateBasisPointsIndex = 2;
    internal const int FieldCount = 3;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(NpcKindField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(SellRateBasisPointsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(BuyRateBasisPointsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>The store row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the store schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
