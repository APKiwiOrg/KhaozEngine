using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>rarity_kind_limit</c> content type of spec 8.5, how many of one mod kind above 2 a rarity
/// permits. Child of <c>rarity_rule</c>, type id
/// <see cref="InstanceContentTypeIds.RarityKindLimitTypeId"/>.
/// <para>
/// <b>It is <see cref="ContentVisibility.Client"/> rather than <c>ServerOnly</c>, despite reading like a
/// generation knob.</b> Spec 8.1 marks exactly three types <c>ServerOnly</c> and this is not one of them:
/// a limit is a COUNT the client needs to compute the same tooltip the server does, while a weight is the
/// odds of an outcome. Having "limit" and "weight" land on different sides of that line is the whole
/// distinction spec 8.1 draws.
/// </para>
/// <para>
/// <see cref="ModKindField"/> carries a <c>mod.kind</c> ABOVE 2, because kinds 1 and 2 are already counted
/// by the rarity rule's own <c>max_prefixes</c> and <c>max_suffixes</c> and a third answer for the same
/// question would be two rules over one number. Spec 8.2 leaves 3 to 255 free for the game, so that is the
/// domain, and it is the one bound this type's codec adds.
/// </para>
/// <para>
/// There is no <c>sort</c>. The rows of this type have no order, so a child type carries none. Every
/// CROSS-ROW rule belongs to the validator: a parent that resolves, and a kind that is not claimed twice
/// for one rarity.
/// </para>
/// </summary>
public static class RarityKindLimitContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child of a type with few rows.</summary>
    public const int DefaultChunkSlots = 1024;

    /// <summary>This type's row cap, which the default covers for a row of three small fields.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client counts the same limits the server does.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The smallest mod kind a limit may name, which is the first kind above prefix and suffix.</summary>
    public const int MinModKind = ModContentType.SuffixKind + 1;

    /// <summary>The largest mod kind a limit may name, which is the top of spec 8.2's game range.</summary>
    public const int MaxModKind = 255;

    /// <summary>The rarity this limit belongs to.</summary>
    public const string RarityRuleIdField = "rarity_rule_id";

    /// <summary>The mod kind the limit counts, above prefix and suffix.</summary>
    public const string ModKindField = "mod_kind";

    /// <summary>How many affixes of that kind the rarity permits.</summary>
    public const string MaxCountField = "max_count";

    /// <summary>The ordered field list, spec 8.5's second table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            RarityRuleIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.RarityRuleTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(ModKindField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MaxCountField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The rarity kind limit row codec. It adds the two IN-ROW bounds the generic walk cannot express,
    /// checked on BOTH sides so an encoder cannot write a row its own decoder refuses. A
    /// <see cref="MaxCountField"/> of zero is an ordinary row meaning the rarity permits none of that kind.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the rarity kind limit schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                RarityRuleIdField when value.Number is < 0 or > RarityRuleContentType.MaxDefinitionId
                    => ReasonFieldMalformed,
                ModKindField when value.Number is < MinModKind or > MaxModKind => ReasonFieldMalformed,
                _ => null,
            };
    }
}
