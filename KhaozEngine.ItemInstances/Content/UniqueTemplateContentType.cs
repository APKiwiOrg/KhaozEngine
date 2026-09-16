using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>unique_template</c> content type of spec 8.6, a fixed item built on a base. Type id
/// <see cref="InstanceContentTypeIds.UniqueTemplateTypeId"/>, and the id payload kind 129 stores.
/// <para>
/// <b>A unique is a template plus rolls, not a separate item kind.</b> The generated item carries kind 129
/// naming the template, kind 131 carrying the rolled positions for the template's lines, and kind 130
/// carrying the unique rarity rule. Nothing in the payload format is special, which is what lets a craft
/// reroll a unique's values without any primitive knowing what a unique is.
/// </para>
/// <para>
/// <see cref="BaseIdField"/> names Scope A's <c>item</c> type, which is the base. An earlier draft of the
/// spec wrote the target as <c>item_base</c>, a type key nothing registers, which would have fired
/// <c>KEC0007</c> at registry freeze and blocked every pack carrying a unique.
/// </para>
/// <para>
/// <b><see cref="WeightField"/> is a scalar <see cref="ContentVisibility.ServerOnly"/> FIELD on a
/// <see cref="ContentVisibility.Client"/> type</b>, which contracts 4.7 allows because visibility is per
/// field. It needs no child type of its own because it does not REPEAT, and that is exactly the difference
/// between it and the three whole <c>ServerOnly</c> weight types of spec 8.1: those repeat per tag, and a
/// repeating weight inside a row a client downloads had no way out.
/// </para>
/// <para>
/// <see cref="ItemLevelMinField"/> carries the same 1 to 65,535 item level domain kind 2 stores, held once
/// in <see cref="ModTierContentType"/> rather than copied here. Every CROSS-ROW rule belongs to the
/// validator: a base that resolves, and the unique's lines naming mods with no weight rows (spec 8.9
/// check 7).
/// </para>
/// </summary>
public static class UniqueTemplateContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>This type's row cap, which the default covers for a row of three small fields.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client renders a unique from these bytes.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The item base the unique is built on.</summary>
    public const string BaseIdField = "base_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int BaseIdIndex = 0;

    /// <summary>
    /// The name that REPLACES the base name, a marker whose derived key is
    /// <c>unique_template.&lt;content key&gt;.name</c>.
    /// </summary>
    public const string NameField = "name";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int NameIndex = 1;

    /// <summary>The inclusive item level floor the unique is reachable at.</summary>
    public const string ItemLevelMinField = "item_level_min";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int ItemLevelMinIndex = 2;

    /// <summary>The unique's share of the weighted draw, which a client never downloads.</summary>
    public const string WeightField = "weight";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int WeightIndex = 3;

    /// <summary>The ordered field list, spec 8.6's first table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            BaseIdField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ItemLevelMinField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(WeightField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
    ]);

    /// <summary>
    /// The unique template row codec. It adds the one IN-ROW bound the generic walk cannot express, checked
    /// on BOTH sides. A weight of zero is an ordinary row meaning the unique never drops, which is how one
    /// reachable only through crafting is authored, so there is no floor on the weight itself.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the unique template schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                ItemLevelMinField when value.Number is < ModTierContentType.MinItemLevel
                    or > ModTierContentType.MaxItemLevel => ReasonFieldMalformed,
                _ => null,
            };
    }
}
