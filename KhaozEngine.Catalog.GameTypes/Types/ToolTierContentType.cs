namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>tool_tier</c> content type: one tier of one ranked tool family, and the two rates that rank buys.
/// </summary>
/// <remarks>
/// <see cref="FamilyField"/> is a TAG reference, which collapses two checks into one authored fact the
/// publish validator already resolves: the family gate and the "can this item be a tool of that family"
/// predicate become the same reference. It points at an engine <c>tag</c> row and has nothing to do with the
/// authoring FAMILY of the engine's key rules, which travels in no pack.
/// <para>
/// Both scales are basis points, so there is no float in a tool's arithmetic and a tier reads the same on the
/// server and on the client.
/// </para>
/// </remarks>
public static class ToolTierContentType
{
    /// <summary>Id slots per chunk, the engine's floor. A tier per tool per family is tens of rows.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The item that IS this tier of tool.</summary>
    public const string ItemField = "item";

    /// <summary>The tag naming the tool family.</summary>
    public const string FamilyField = "family";

    /// <summary>The tier's place within its family, ascending.</summary>
    public const string RankField = "rank";

    /// <summary>The success rate multiplier, in basis points.</summary>
    public const string SuccessScaleBasisPointsField = "success_scale_bp";

    /// <summary>The action time multiplier, in basis points.</summary>
    public const string TimeScaleBasisPointsField = "time_scale_bp";

    internal const int ItemIndex = 0;
    internal const int FamilyIndex = 1;
    internal const int RankIndex = 2;
    internal const int SuccessScaleBasisPointsIndex = 3;
    internal const int TimeScaleBasisPointsIndex = 4;
    internal const int FieldCount = 5;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            ItemField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(
            FamilyField,
            ContentFieldKind.KeyReference,
            ContentFieldEntry.TagReferenceTarget,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(RankField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            SuccessScaleBasisPointsField,
            ContentFieldKind.Int,
            null,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(TimeScaleBasisPointsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>The tool tier row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the tool tier schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
