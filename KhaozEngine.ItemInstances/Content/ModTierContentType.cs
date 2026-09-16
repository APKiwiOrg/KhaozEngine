using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>mod_tier</c> content type of spec 8.3, one tier of one mod with its item level gate. Child of
/// <c>mod</c>, type id <see cref="InstanceContentTypeIds.ModTierTypeId"/>.
/// <para>
/// <b>The payload stores the pair (mod id, tier ordinal), never a <c>mod_tier</c> id</b>, and that
/// narrowing is the reason this type exists in this shape. A payload naming a row id would need its own
/// remap rule for every retired tier, and a retire of one tier would become a rule kind of its own. The
/// ordinal is the key the bytes carry, the row is where the tier's data lives, and the validator refuses
/// to let the two drift.
/// </para>
/// <para>
/// <b>The engine assigns tier ordinals no ordering meaning.</b> Whether ordinal 1 is the best or the worst
/// is the author's convention. What gates a tier is its own <see cref="ItemLevelMinField"/> and
/// <see cref="ItemLevelMaxField"/>, inclusive, with an
/// <see cref="ItemLevelMaxField"/> of <see cref="MaxItemLevel"/> meaning no ceiling.
/// </para>
/// <para>
/// There is no <c>sort</c>. A child type carries one where its ORDER matters, and a tier's order is its
/// ordinal, so a second ordering field would be a second answer to the same question.
/// </para>
/// <para>
/// The bounds below are the only rules this type checks. Every CROSS-ROW rule belongs to the validator: a
/// parent that resolves, an ordinal unique within its mod, <c>item_level_min</c> at or below
/// <c>item_level_max</c>, and the publish-only refusal of a reorder since the previous published snapshot.
/// Keeping that split clean is what lets the codec stay a positional walk.
/// </para>
/// </summary>
public static class ModTierContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child that outnumbers its parent.</summary>
    public const int DefaultChunkSlots = 16384;

    /// <summary>
    /// This type's own row cap, which its slot count requires: 16,384 slots at the 1,024 byte default
    /// would build a chunk past the uncompressed ceiling no reader loads. A real tier row is about 20
    /// bytes, so 512 is already generous.
    /// </summary>
    public const int MaxRowBytes = 512;

    /// <summary>The type level visibility, spec 8.1. A tooltip computes a tier's range on both sides.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The smallest legal tier ordinal.</summary>
    public const int MinOrdinal = 1;

    /// <summary>The largest legal tier ordinal, which is the width of the payload's tier slot.</summary>
    public const int MaxOrdinal = 255;

    /// <summary>The smallest legal item level.</summary>
    public const int MinItemLevel = 1;

    /// <summary>The largest legal item level, which on <see cref="ItemLevelMaxField"/> means no ceiling.</summary>
    public const int MaxItemLevel = 65535;

    /// <summary>The mod this tier belongs to.</summary>
    public const string ModIdField = "mod_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int ModIdIndex = 0;

    /// <summary>The tier's ordinal, unique within the mod and IMMUTABLE once published.</summary>
    public const string OrdinalField = "ordinal";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int OrdinalIndex = 1;

    /// <summary>The inclusive item level floor this tier is reachable at.</summary>
    public const string ItemLevelMinField = "item_level_min";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int ItemLevelMinIndex = 2;

    /// <summary>The inclusive item level ceiling this tier is reachable at.</summary>
    public const string ItemLevelMaxField = "item_level_max";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int ItemLevelMaxIndex = 3;

    /// <summary>The ordered field list, spec 8.3's first table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            ModIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.ModTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(OrdinalField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ItemLevelMinField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ItemLevelMaxField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The mod tier row codec. It adds the two IN-ROW bounds the generic walk cannot express, checked on
    /// BOTH sides so an encoder cannot write a row its own decoder refuses.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the mod tier schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                OrdinalField when value.Number is < MinOrdinal or > MaxOrdinal => ReasonFieldMalformed,
                ItemLevelMinField or ItemLevelMaxField when value.Number is < MinItemLevel or > MaxItemLevel
                    => ReasonFieldMalformed,
                _ => null,
            };
    }
}
