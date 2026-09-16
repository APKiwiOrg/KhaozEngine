using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>unique_line</c> content type of spec 8.6, one mod at one tier that a unique template grants.
/// Child of <c>unique_template</c>, type id <see cref="InstanceContentTypeIds.UniqueLineTypeId"/>.
/// <para>
/// <b>A unique's lines are NOT a second line shape: each one NAMES an ordinary mod row.</b> That is the one
/// asymmetry in the format and spec 8.10 calls it the single most important consequence of the affix entry
/// for an author to understand, so it is written here rather than left in a design doc nobody reads at
/// authoring time.
/// </para>
/// <para>
/// Kind 131's entries are <c>(mod id, tier, position, flags)</c>, so a unique's line needs a mod id to sit
/// there. The answer is that a unique template's lines are authored as ORDINARY MOD ROWS with a SINGLE
/// tier, with NO <c>mod_tier_weight</c> row on any tier so the generator can never roll them onto an
/// ordinary item, and with a group that keeps them off a rare. This type points at that mod and that tier.
/// </para>
/// <para>
/// It costs one mod row per unique line and it buys a payload with no second affix shape, no second decode
/// path and no second remap story. The authoring trap it creates is worth naming too: an author who gives
/// one of those mods a <c>mod_tier_weight</c> row has quietly added it to the rare pool for every base
/// carrying that tag, which is spec 8.9 check 7's refusal at publish.
/// </para>
/// <para>
/// <see cref="TierOrdinalField"/> is the mod's own AUTHORED tier ordinal, never a <c>mod_tier</c> row id,
/// so the pair this row carries is the same pair the payload carries. Its bound is the one
/// <see cref="ModTierContentType"/> already holds, because kind 131's entry stores a tier in one byte.
/// </para>
/// </summary>
public static class UniqueLineContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child that outnumbers its parent.</summary>
    public const int DefaultChunkSlots = 16384;

    /// <summary>
    /// This type's own row cap, which its slot count requires: 16,384 slots at the 1,024 byte default would
    /// build a chunk past the uncompressed ceiling no reader loads. A line row is a key and four small
    /// varints, so 512 is already generous.
    /// </summary>
    public const int MaxRowBytes = 512;

    /// <summary>The type level visibility, spec 8.1. A client renders a unique's lines from these bytes.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The smallest legal tier ordinal, which is the mod tier type's own floor.</summary>
    public const int MinTierOrdinal = ModTierContentType.MinOrdinal;

    /// <summary>The largest legal tier ordinal, which is the width of the payload's tier slot.</summary>
    public const int MaxTierOrdinal = ModTierContentType.MaxOrdinal;

    /// <summary>The unique template this line belongs to.</summary>
    public const string UniqueTemplateIdField = "unique_template_id";

    /// <summary>The authored order, which is what makes "the unique's second line" a stable phrase.</summary>
    public const string SortField = "sort";

    /// <summary>The ordinary mod row this line IS.</summary>
    public const string ModIdField = "mod_id";

    /// <summary>Which of that mod's tiers the line grants, by the mod's own authored ordinal.</summary>
    public const string TierOrdinalField = "tier_ordinal";

    /// <summary>The ordered field list, spec 8.6's second table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            UniqueTemplateIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.UniqueTemplateTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            ModIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.ModTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(TierOrdinalField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The unique line row codec. It adds the one IN-ROW bound the generic walk cannot express, checked on
    /// BOTH sides. Whether the ordinal names a tier that EXISTS on that mod is a cross-row rule and belongs
    /// to the validator.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the unique line schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                TierOrdinalField when value.Number is < MinTierOrdinal or > MaxTierOrdinal
                    => ReasonFieldMalformed,
                _ => null,
            };
    }
}
