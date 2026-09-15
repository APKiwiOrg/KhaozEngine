namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>loot_entry</c> content type of spec 3.5, one weighted row of one loot table.
/// <para>
/// Entries outnumber tables by roughly the branching factor, so the type takes a larger chunk than its
/// parent and declares a row cap of its own: a chunk is a transport unit sized for download economics
/// rather than an authoring unit, and 16,384 slots at the 1,024-byte default would break the registration
/// bound. A real entry row is about 20 bytes, so 512 is already generous.
/// </para>
/// <para>
/// An entry names its draw exactly ONE of three ways, <c>item</c> set, <c>nested_table</c> set, or a
/// non-empty <c>required_tags</c>. That is <c>KEC0023</c>'s refusal at publish rather than the codec's: a
/// row carrying two of them has to reach the validator intact to be reported, and a precedence order would
/// be a silent choice between two things an author wrote down.
/// </para>
/// </summary>
public static class LootEntryContentType
{
    /// <summary>Id slots per chunk, sized for a child that outnumbers its parent.</summary>
    public const int DefaultChunkSlots = 16384;

    /// <summary>This type's own row cap, which its slot count requires.</summary>
    public const int MaxRowBytes = 512;

    /// <summary>The table this entry belongs to.</summary>
    public const string TableField = "table";

    /// <summary>The item this entry draws, when the entry is an item draw.</summary>
    public const string ItemField = "item";

    /// <summary>The table this entry recurses into, when the entry is a nested draw.</summary>
    public const string NestedTableField = "nested_table";

    /// <summary>The entry's share of the weighted draw.</summary>
    public const string WeightField = "weight";

    /// <summary>The entry's own chance, in basis points out of 10,000.</summary>
    public const string ChanceBasisPointsField = "chance_bp";

    /// <summary>The smallest count a drawn line carries.</summary>
    public const string MinCountField = "min_count";

    /// <summary>The largest count a drawn line carries.</summary>
    public const string MaxCountField = "max_count";

    /// <summary>The authored order, which fixes the order guaranteed entries roll in.</summary>
    public const string SortField = "sort";

    /// <summary>The tag filter, when the entry draws a CLASS of item rather than naming one.</summary>
    public const string RequiredTagsField = "required_tags";

    /// <summary>The ordered field list, spec 3.5's second table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            TableField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.LootTableTypeKey,
            ContentVisibility.ServerOnly,
            true),
        new ContentFieldEntry(
            ItemField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.ServerOnly,
            false),
        new ContentFieldEntry(
            NestedTableField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.LootTableTypeKey,
            ContentVisibility.ServerOnly,
            false),
        new ContentFieldEntry(WeightField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
        new ContentFieldEntry(ChanceBasisPointsField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
        new ContentFieldEntry(MinCountField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
        new ContentFieldEntry(MaxCountField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
        new ContentFieldEntry(
            RequiredTagsField,
            ContentFieldKind.TagList,
            ContentFieldEntry.TagReferenceTarget,
            ContentVisibility.ServerOnly,
            false),
    ]);

    /// <summary>The loot entry row codec, which is the generic positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the loot entry schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
