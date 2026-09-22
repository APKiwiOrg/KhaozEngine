namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>item_category</c> content type, the coarse bucket an item base belongs to. It is the same shape as
/// <see cref="TagContentType"/>, an id, a key, a display name and a console sort, and it exists beside tags
/// rather than inside them because the two answer different questions.
/// <para>
/// A tag list is MANY per row and is what a predicate reads. A category is ONE per row and is what a grouped
/// listing reads: a collection log, a bank tab, a shop board. Modelling a category as a reserved tag would
/// make "exactly one" a rule nothing enforces, and modelling it as a string would put an ordered vocabulary
/// outside the catalog where no validator can follow it.
/// </para>
/// <para>
/// The category vocabulary is CONTENT, so the engine ships no rows for it. A game publishes the categories it
/// wants and points <c>item.category</c> at them, and an item that belongs to none leaves the field absent.
/// </para>
/// </summary>
public static class ItemCategoryContentType
{
    /// <summary>Id slots per chunk. Categories are few, so one chunk holds every category a game will author.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>The derived display name, <c>item_category.&lt;content key&gt;.name</c>, carrying no bytes.</summary>
    public const string NameField = "name";

    /// <summary>The console's authored list order, which is also the order a grouped listing reads.</summary>
    public const string SortField = "sort";

    /// <summary>The ordered field list, which is the tag type's list exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
    ]);

    /// <summary>The category row codec, which is the generic positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the category schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
