namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>monster_drop</c> content type: one creature kind and the <c>loot_table</c> it rolls on death.
/// </summary>
/// <remarks>
/// <b><c>ServerOnly</c> at the TYPE level</b>, which is what keeps the whole family out of every client
/// manifest rather than merely out of a client's view of a row. It is the same declaration the engine's
/// <c>loot_table</c> and <c>loot_entry</c> carry, for the same reason: a client that can read a drop table
/// knows every roll before it happens, and the server is authoritative over what falls.
/// <para>
/// The type holds the BINDING and nothing else. What actually drops is the engine's table, rolled by the
/// engine's <c>LootRoller</c>, which is the one implementation of the composition rule. A roll written here
/// would be a second answer to the first table that used both <c>guaranteed</c> and <c>roll_count</c>.
/// </para>
/// </remarks>
public static class MonsterDropContentType
{
    /// <summary>Id slots per chunk, the engine's floor. One row per creature kind is a small type.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.ServerOnly;

    /// <summary>The creature kind this rule belongs to, unique across live rows.</summary>
    public const string MonsterKindField = "monster_kind";

    /// <summary>The engine loot table the kind rolls.</summary>
    public const string LootTableField = "loot_table";

    internal const int MonsterKindIndex = 0;
    internal const int LootTableIndex = 1;
    internal const int FieldCount = 2;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(MonsterKindField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
        new ContentFieldEntry(
            LootTableField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.LootTableTypeKey,
            ContentVisibility.ServerOnly,
            true),
    ]);

    /// <summary>The drop row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the monster drop schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
