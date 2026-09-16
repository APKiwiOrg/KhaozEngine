using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>mod_tier_weight</c> content type of spec 8.3, one tier's spawn weight against one tag. Child of
/// <c>mod_tier</c>, type id <see cref="InstanceContentTypeIds.ModTierWeightTypeId"/>.
/// <para>
/// <b>The WHOLE TYPE is <see cref="ContentVisibility.ServerOnly"/>, and that is the point of the split.</b>
/// A weight tells a client the exact odds of every outcome, which is farmable rather than displayable. An
/// earlier draft made a weight a sub-field of a row whose other bytes were <c>Client</c>, and the client
/// chunk builder omits whole FIELDS, so a weight in the middle of a mixed-visibility row had no way out.
/// A whole <c>ServerOnly</c> type makes it a publish-time property again: a client downloads no weight row
/// at all, while everything else about a tier stays client visible so a tooltip is computed from the same
/// bytes on both sides.
/// </para>
/// <para>
/// <b>A weight is keyed by TAG, never by base id.</b> A base's weight for a tier is the weight of the
/// FIRST tag in the base's authored tag list that has a row for that tier, or zero when none does. First
/// rather than sum, because the base's tag order is authored information and a sum would make the order
/// meaningless. A tier with NO weight rows can never spawn, which is legal and is how a tier reachable
/// only through crafting is authored.
/// </para>
/// <para>
/// There is no <c>sort</c>. The rows of this type have no order, so a child type carries none.
/// </para>
/// </summary>
public static class ModTierWeightContentType
{
    /// <summary>
    /// Id slots per chunk, spec 8.1's table. Weights are the most numerous rows in the family, one per
    /// tier per tag, so the type takes the largest chunk contracts 4.5 permits.
    /// </summary>
    public const int DefaultChunkSlots = 65536;

    /// <summary>
    /// This type's own row cap, which its slot count requires: 65,536 slots at the 1,024 byte default
    /// would build a chunk past the uncompressed ceiling no reader loads. A weight row is a key and three
    /// varints, so 128 is already generous.
    /// </summary>
    public const int MaxRowBytes = 128;

    /// <summary>The type level visibility, spec 8.1. Odds are farmable rather than displayable.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.ServerOnly;

    /// <summary>The tier this weight belongs to.</summary>
    public const string ModTierIdField = "mod_tier_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int ModTierIdIndex = 0;

    /// <summary>The tag the weight is keyed by.</summary>
    public const string TagIdField = "tag_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int TagIdIndex = 1;

    /// <summary>The tier's share of the weighted draw for a base carrying that tag.</summary>
    public const string WeightField = "weight";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int WeightIndex = 2;

    /// <summary>The ordered field list, spec 8.3's second table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            ModTierIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.ModTierTypeKey,
            ContentVisibility.ServerOnly,
            true),
        new ContentFieldEntry(
            TagIdField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.TagTypeKey,
            ContentVisibility.ServerOnly,
            true),
        new ContentFieldEntry(WeightField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
    ]);

    /// <summary>
    /// The mod tier weight row codec, which is the generic positional walk with nothing added. A weight of
    /// zero is an ordinary row meaning the tier cannot spawn for that tag, so there is no floor to check.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the mod tier weight schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
