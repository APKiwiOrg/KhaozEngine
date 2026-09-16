using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>rare_name_word_weight</c> content type of spec 8.8, one word's weight against one tag. Child of
/// <c>rare_name_word</c>, type id <see cref="InstanceContentTypeIds.RareNameWordWeightTypeId"/>.
/// <para>
/// <b>The WHOLE TYPE is <see cref="ContentVisibility.ServerOnly"/></b>, the third of spec 8.1's three and
/// for the same reason as the other two: a weight tells a client the exact odds of every outcome, and the
/// client chunk builder omits whole FIELDS, so a weight inside a mixed-visibility row had no way out. A
/// client downloads no name word weight row at all, while the word itself stays client visible because the
/// client composes the name.
/// </para>
/// <para>
/// <b>A weight is keyed by TAG, never by base id</b>, the same first-tag-wins rule the other two weight
/// types follow. A word with no weight row can never be drawn, which is legal and is how a word reserved
/// for one authored unique is written down.
/// </para>
/// <para>
/// There is no <c>sort</c>. The rows of this type have no order, so a child type carries none.
/// </para>
/// </summary>
public static class RareNameWordWeightContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child that outnumbers its parent.</summary>
    public const int DefaultChunkSlots = 16384;

    /// <summary>
    /// This type's own row cap, which its slot count requires: 16,384 slots at the 1,024 byte default would
    /// build a chunk past the uncompressed ceiling no reader loads. A weight row is a key and three
    /// varints, so 512 is already generous.
    /// </summary>
    public const int MaxRowBytes = 512;

    /// <summary>The type level visibility, spec 8.1. Odds are farmable rather than displayable.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.ServerOnly;

    /// <summary>The word this weight belongs to.</summary>
    public const string RareNameWordIdField = "rare_name_word_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int RareNameWordIdIndex = 0;

    /// <summary>The tag the weight is keyed by.</summary>
    public const string TagIdField = "tag_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int TagIdIndex = 1;

    /// <summary>The word's share of the weighted draw for a base carrying that tag.</summary>
    public const string WeightField = "weight";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int WeightIndex = 2;

    /// <summary>The ordered field list, spec 8.8's second table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            RareNameWordIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.RareNameWordTypeKey,
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
    /// The rare name word weight row codec, which is the generic positional walk with nothing added. Kind
    /// 134 stores a word id as a varint rather than a byte, so unlike a rarity id there is no format
    /// ceiling to hold the reference under, and a weight of zero is an ordinary row meaning the word cannot
    /// be drawn for that tag.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the rare name word weight schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
