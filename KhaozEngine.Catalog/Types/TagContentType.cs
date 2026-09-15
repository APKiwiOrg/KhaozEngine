namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>tag</c> content type of spec 3.2, the tag vocabulary contracts 4.6 makes content rather than
/// strings. A tag row is an id, a key and a display name, and nothing else.
/// <para>
/// <c>sort</c> exists only so a console can list tags in an authored order rather than by id. It is the
/// single case in the engine schemas where a field exists for the editor's benefit, and it is named so
/// nobody later mistakes it for a gameplay field.
/// </para>
/// </summary>
public static class TagContentType
{
    /// <summary>Id slots per chunk. Tags are small and numerous, so the chunk is a large one.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>The derived display name, <c>tag.&lt;content key&gt;.name</c>, carrying no bytes.</summary>
    public const string NameField = "name";

    /// <summary>The console's authored list order.</summary>
    public const string SortField = "sort";

    /// <summary>The ordered field list, spec 3.2's table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
    ]);

    /// <summary>The tag row codec, which is the generic positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the tag schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
