namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>loot_table</c> content type of spec 3.5, a named drop or reward table.
/// <para>
/// <c>ServerOnly</c> at the TYPE level, so the whole family is omitted from every client manifest. A drop
/// table is exactly the content a client must not have.
/// </para>
/// <para>
/// A table's entries are <c>loot_entry</c> rows rather than a repeated field, by the rule that any
/// repeating child structure is its own content type with a key reference to its parent and never a blob
/// field. A blob would cost the generic editor, the field-level audit and the field-level diff, which is
/// everything the field schema exists to give.
/// </para>
/// </summary>
public static class LootTableContentType
{
    /// <summary>Id slots per chunk.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>How many weighted picks a roll draws over the non-guaranteed entries.</summary>
    public const string RollCountField = "roll_count";

    /// <summary>The table's tags, in authored order.</summary>
    public const string TagsField = "tags";

    /// <summary>Whether the table's entries roll their own chance independently.</summary>
    public const string GuaranteedField = "guaranteed";

    /// <summary>The ordered field list, spec 3.5's first table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(RollCountField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
        new ContentFieldEntry(
            TagsField,
            ContentFieldKind.TagList,
            ContentFieldEntry.TagReferenceTarget,
            ContentVisibility.ServerOnly,
            false),
        new ContentFieldEntry(GuaranteedField, ContentFieldKind.Bool, null, ContentVisibility.ServerOnly, true),
    ]);

    /// <summary>The loot table row codec, which is the generic positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the loot table schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
