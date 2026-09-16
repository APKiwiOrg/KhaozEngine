using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>crafting_currency</c> content type of spec 10.4, a named sequence of steps with guards. Type id
/// <see cref="InstanceContentTypeIds.CraftingCurrencyTypeId"/>.
/// <para>
/// <b>A currency is THREE content types rather than one row with two lists in it.</b> The currency, its
/// ordered <c>currency_step</c> children and its <c>currency_guard</c> children, because any repeating
/// child structure is its own content type with a key reference to its parent. All three register in this
/// band so the eighteen type ids sit in one table, and their SEMANTICS are the crafting framework's.
/// </para>
/// <para>
/// <see cref="ConsumesDefinitionIdField"/> is OPTIONAL, and an empty one is a FREE operation rather than an
/// authoring mistake: a bench that costs nothing is an ordinary currency row. <see cref="ConsumesCountField"/>
/// is required, so a count of 0 beside a named item is an authored value the validator can see rather than
/// an unset field it cannot tell from one.
/// </para>
/// <para>
/// <see cref="MaxStepsField"/> is a ceiling the AUTHOR declares and the publish checks the step rows
/// against, capped at <see cref="MaxSteps"/> in v1. The in-row half of that, a declared ceiling past 16, is
/// the codec's. Whether the currency actually carries more step rows than it declares is a cross-row rule
/// and belongs to the validator.
/// </para>
/// <para>
/// <see cref="NameField"/> and <see cref="DescriptionField"/> store NOTHING. Both are markers whose derived
/// keys are <c>crafting_currency.&lt;content key&gt;.name</c> and
/// <c>crafting_currency.&lt;content key&gt;.description</c>, so a currency's displayed text is looked up
/// rather than stored and a translator adding a language adds a text chunk rather than a content edit.
/// </para>
/// </summary>
public static class CraftingCurrencyContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>
    /// This type's row cap. At 4,096 slots the chunk ceiling leaves 4,087 bytes per row, so the 1,024 byte
    /// default fits and the type declares no cap of its own.
    /// </summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client lists a currency and its text.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The most steps a currency may declare in v1, spec 10.4.</summary>
    public const int MaxSteps = 16;

    /// <summary>The currency's displayed name, derived as <c>crafting_currency.&lt;key&gt;.name</c>.</summary>
    public const string NameField = "name";

    /// <summary>The currency's displayed description, derived the same way.</summary>
    public const string DescriptionField = "description";

    /// <summary>The item base spent to run the currency, or absent for a free operation.</summary>
    public const string ConsumesDefinitionIdField = "consumes_definition_id";

    /// <summary>How many of that item one run spends.</summary>
    public const string ConsumesCountField = "consumes_count";

    /// <summary>The declared ceiling on this currency's step count, at most <see cref="MaxSteps"/>.</summary>
    public const string MaxStepsField = "max_steps";

    /// <summary>The ordered field list, spec 10.4's first table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            DescriptionField,
            ContentFieldKind.LocalizedTextKey,
            null,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(
            ConsumesDefinitionIdField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            false),
        new ContentFieldEntry(ConsumesCountField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MaxStepsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The crafting currency row codec. It adds the two IN-ROW bounds the generic walk cannot express,
    /// checked on BOTH sides so an encoder cannot write a row its own decoder refuses.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the crafting currency schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                ConsumesCountField when value.Number < 0 => ReasonFieldMalformed,
                MaxStepsField when value.Number is < 0 or > MaxSteps => ReasonFieldMalformed,
                _ => null,
            };
    }
}
