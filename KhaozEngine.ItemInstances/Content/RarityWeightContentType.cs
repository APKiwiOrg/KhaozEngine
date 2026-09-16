using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>rarity_weight</c> content type of spec 8.5, one rarity's weight against one tag. Child of
/// <c>rarity_rule</c>, type id <see cref="InstanceContentTypeIds.RarityWeightTypeId"/>.
/// <para>
/// <b>The WHOLE TYPE is <see cref="ContentVisibility.ServerOnly"/></b>, one of the three of spec 8.1 and
/// for the same reason as the other two: a weight tells a client the exact odds of every outcome, which is
/// farmable rather than displayable, and the client chunk builder omits whole FIELDS so a weight inside a
/// mixed-visibility row had no way out. A client downloads no rarity weight row at all, while everything
/// else about a rarity stays client visible.
/// </para>
/// <para>
/// <b>A weight is keyed by TAG, never by base id</b>, which is the same first-tag-wins rule
/// <c>mod_tier_weight</c> follows, over rarities rather than tiers. A base's weight for a rarity is the
/// weight of the FIRST tag in the base's authored tag list that has a row for that rarity, or zero when
/// none does. First rather than sum, because the base's tag order is authored information. This is the
/// draw the generator's rarity step makes.
/// </para>
/// <para>
/// There is no <c>sort</c>. The rows of this type have no order, so a child type carries none.
/// </para>
/// </summary>
public static class RarityWeightContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child of a type with few rows.</summary>
    public const int DefaultChunkSlots = 1024;

    /// <summary>This type's row cap, which the default covers for a row of three small fields.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. Odds are farmable rather than displayable.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.ServerOnly;

    /// <summary>The rarity this weight belongs to.</summary>
    public const string RarityRuleIdField = "rarity_rule_id";

    /// <summary>The tag the weight is keyed by.</summary>
    public const string TagIdField = "tag_id";

    /// <summary>The rarity's share of the weighted draw for a base carrying that tag.</summary>
    public const string WeightField = "weight";

    /// <summary>The ordered field list, spec 8.5's third table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            RarityRuleIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.RarityRuleTypeKey,
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
    /// The rarity weight row codec. It adds the one IN-ROW bound the generic walk cannot express, the same
    /// rarity id ceiling kind 130's byte imposes, checked on BOTH sides. A weight of zero is an ordinary row
    /// meaning the rarity cannot be drawn for that tag, so there is no floor on the weight itself.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the rarity weight schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                RarityRuleIdField when value.Number is < 0 or > RarityRuleContentType.MaxDefinitionId
                    => ReasonFieldMalformed,
                _ => null,
            };
    }
}
