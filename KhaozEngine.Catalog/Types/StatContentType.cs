namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>stat</c> content type of spec 3.4, which is contracts 13.1's table with nothing added.
/// <para>
/// <c>scale</c> is a fixed power of ten and the stored integer is the value times the scale, so there is no
/// float stat and no float modifier anywhere in the content system. <c>min</c> and <c>max</c> are in scaled
/// units and are the inclusive clamp of the evaluation formula. The power-of-ten rule and the
/// <c>min &gt; max</c> refusal are the validator's (<c>KEC0020</c> and <c>KEC0021</c>), not the codec's.
/// </para>
/// <para>
/// <c>Client</c> throughout, because a client tooltip computes a displayed stat with the same integer
/// arithmetic the server uses, and it cannot do that without the scale and the clamp.
/// </para>
/// </summary>
public static class StatContentType
{
    /// <summary>Id slots per chunk.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>The derived display name.</summary>
    public const string NameField = "name";

    /// <summary>The fixed power of ten the stored integer is multiplied by.</summary>
    public const string ScaleField = "scale";

    /// <summary>The inclusive clamp floor, in scaled units.</summary>
    public const string MinField = "min";

    /// <summary>The inclusive clamp ceiling, in scaled units.</summary>
    public const string MaxField = "max";

    /// <summary>The stat's tags, in authored order.</summary>
    public const string TagsField = "tags";

    /// <summary>The derived display format key, which is where a translator moves a unit suffix.</summary>
    public const string DisplayFormatField = "display_format";

    /// <summary>The ordered field list, spec 3.4's table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ScaleField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MinField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MaxField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            TagsField,
            ContentFieldKind.TagList,
            ContentFieldEntry.TagReferenceTarget,
            ContentVisibility.Client,
            false),
        new ContentFieldEntry(
            DisplayFormatField,
            ContentFieldKind.LocalizedTextKey,
            null,
            ContentVisibility.Client,
            true),
    ]);

    /// <summary>The stat row codec, which is the generic positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the stat schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
