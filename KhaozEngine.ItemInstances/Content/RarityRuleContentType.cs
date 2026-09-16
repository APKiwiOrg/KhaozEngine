using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>rarity_rule</c> content type of spec 8.5, how many affixes a rarity permits and its composed
/// name template. Type id <see cref="InstanceContentTypeIds.RarityRuleTypeId"/>.
/// <para>
/// <b>A rarity id is ONE BYTE forever, and that is a format constraint rather than a preference.</b> Kind
/// 130 is <c>[RarityId: byte]</c>, pinned by contracts 9.8's <c>82 01 01 03</c>, so
/// <see cref="MaxDefinitionId"/> is 255 and it counts the DEAD as well as the live: contracts 5.1 never
/// reuses an id, so every retired rarity keeps its number. That ceiling is generous against PoE's four and
/// Tibia's zero, and raising it is a payload format change rather than a schema edit.
/// </para>
/// <para>
/// The ceiling is declared at REGISTRATION, through the registry's own per-type id ceiling, because a row
/// codec never sees a row's own id: the chunk row table owns it and a decode hands back id 0. What the
/// codec CAN see is a rarity id sitting in a field, so <see cref="UpgradeFromField"/> here and the
/// <c>rarity_rule_id</c> of both child types are bounded on both sides against the same constant.
/// </para>
/// <para>
/// <b><see cref="UpgradeFromField"/> is a SINGLE parent, so the rarities form a forest.</b> It is what the
/// set rarity craft primitive walks and what a rarity-upgrade currency composes, and a list would make
/// "what does this rarity upgrade into" a question with several answers.
/// </para>
/// <para>
/// The five counts are capped at <see cref="MaxAffixCount"/> by the payload rather than by taste. Kind
/// 131's <c>Count</c> is a byte, so an item cannot carry more than 255 affixes, and kind 134's
/// <c>WordCount</c> is a byte, so a name cannot carry more than 255 words. A rarity with zero affixes and
/// zero name words is an ordinary row and is how a game with no affix system authors its rarities.
/// </para>
/// <para>
/// Every CROSS-FIELD rule is the validator's, because a codec sees one field at a time:
/// <c>min_affixes</c> at or below <c>max_affixes</c>, <c>max_prefixes + max_suffixes</c> at or above
/// <c>max_affixes</c>, and an <see cref="UpgradeFromField"/> chain with no cycle (spec 8.9 check 6).
/// </para>
/// </summary>
public static class RarityRuleContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table. Rarities are few, so the chunk is the smallest legal one.</summary>
    public const int DefaultChunkSlots = 256;

    /// <summary>This type's row cap, which the default covers for a row of six small fields.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client composes a rare's name from these bytes.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>
    /// The largest definition id a <c>rarity_rule</c> row may take, which is the width of kind 130's one
    /// byte rarity slot. It is handed to the registry as the per-type id ceiling, and the sweep reports
    /// <c>KEC0042</c> against any row over it.
    /// </summary>
    public const int MaxDefinitionId = 255;

    /// <summary>
    /// The largest affix count any of the four count fields may carry, which is the width of kind 131's
    /// count byte.
    /// </summary>
    public const int MaxAffixCount = 255;

    /// <summary>
    /// The largest number of name word positions a rarity may roll, which is the width of kind 134's
    /// <c>WordCount</c> byte. Zero means the item keeps its base name.
    /// </summary>
    public const int MaxNameWordPositions = 255;

    /// <summary>
    /// The composed name template of contracts 12.3, a marker whose derived key is
    /// <c>rarity_rule.&lt;content key&gt;.display_format</c>. Its ARGUMENTS are the item's
    /// <c>rare_name_word</c> texts in POSITION ORDER, which is the order kind 134 stores their ids in,
    /// followed by the base item's own name.
    /// <para>
    /// So a rare with two name words formats <c>{0}</c> and <c>{1}</c> from the words and <c>{2}</c> from
    /// the base, a language whose adjective follows its noun reorders the TEMPLATE rather than the payload,
    /// and a rarity with <see cref="NameWordPositionsField"/> of 0 formats <c>{0}</c> from the base name
    /// alone. This doc comment is the only place that argument order is written down in code, and the
    /// display layer that consumes it lives in another repo.
    /// </para>
    /// </summary>
    public const string DisplayFormatField = "display_format";

    /// <summary>The fewest affixes the rarity rolls, across every kind.</summary>
    public const string MinAffixesField = "min_affixes";

    /// <summary>The most affixes the rarity rolls, across every kind.</summary>
    public const string MaxAffixesField = "max_affixes";

    /// <summary>The most affixes of <c>mod</c> kind 1 the rarity permits.</summary>
    public const string MaxPrefixesField = "max_prefixes";

    /// <summary>The most affixes of <c>mod</c> kind 2 the rarity permits.</summary>
    public const string MaxSuffixesField = "max_suffixes";

    /// <summary>How many name words the rarity rolls, or 0 to keep the base name.</summary>
    public const string NameWordPositionsField = "name_word_positions";

    /// <summary>The single rarity this one upgrades from, or absent when it is a root of the forest.</summary>
    public const string UpgradeFromField = "upgrade_from";

    /// <summary>The ordered field list, spec 8.5's first table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            DisplayFormatField,
            ContentFieldKind.LocalizedTextKey,
            null,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(MinAffixesField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MaxAffixesField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MaxPrefixesField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MaxSuffixesField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(NameWordPositionsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            UpgradeFromField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.RarityRuleTypeKey,
            ContentVisibility.Client,
            false),
    ]);

    /// <summary>
    /// The rarity rule row codec. It adds the three IN-ROW bounds the generic walk cannot express, checked
    /// on BOTH sides so an encoder cannot write a row its own decoder refuses.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the rarity rule schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                MinAffixesField or MaxAffixesField or MaxPrefixesField or MaxSuffixesField
                    when value.Number is < 0 or > MaxAffixCount => ReasonFieldMalformed,
                NameWordPositionsField when value.Number is < 0 or > MaxNameWordPositions
                    => ReasonFieldMalformed,
                UpgradeFromField when value.Number is < 0 or > MaxDefinitionId => ReasonFieldMalformed,
                _ => null,
            };
    }
}
