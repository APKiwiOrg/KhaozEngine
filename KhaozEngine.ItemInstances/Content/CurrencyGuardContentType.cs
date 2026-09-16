using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>currency_guard</c> content type of spec 10.4, one precondition on a currency's target or on one
/// of its steps. Child of <c>crafting_currency</c>, type id
/// <see cref="InstanceContentTypeIds.CurrencyGuardTypeId"/>.
/// <para>
/// <b>Target guards and step guards are ONE type told apart by one empty reference.</b> A guard with no
/// <see cref="CurrencyStepIdField"/> is evaluated once, before any step, against the target, and a failed
/// one refuses the whole craft and consumes nothing. A guard NAMING a step is evaluated before that step,
/// and a failed one SKIPS that step rather than refusing the craft. One type rather than two because the
/// guard schema is identical either way and a second type would duplicate every column of it. What stops a
/// guard naming a step of a different currency is the validator's <c>KEC0100</c> rather than the schema.
/// </para>
/// <para>
/// <see cref="GuardKindField"/> is a CLOSED vocabulary, spec 10.3's fifteen kinds, so the range is refused
/// on both sides here and the kinds themselves belong to the crafting framework that evaluates them. A
/// kind outside the range would be a guard the evaluator cannot read, and a guard silently treated as true
/// is the one failure mode a precondition may never have.
/// </para>
/// <para>
/// The two parameters are REQUIRED and carry 0 when unused, the same convention the step's four take, and
/// they are deliberately unbounded because a guard's pair is a rarity id on one kind and an inclusive range
/// on the next.
/// </para>
/// </summary>
public static class CurrencyGuardContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child that outnumbers its parent.</summary>
    public const int DefaultChunkSlots = 16384;

    /// <summary>
    /// This type's own row cap, which its slot count requires: 16,384 slots at the 1,024 byte default would
    /// build a chunk past the uncompressed ceiling no reader loads, which leaves 1,015 bytes per row. A
    /// guard row is a key, two references and three varints, so 512 is already generous.
    /// </summary>
    public const int MaxRowBytes = 512;

    /// <summary>The type level visibility, spec 8.1. A client explains a refused craft from these bytes.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The first guard kind of spec 10.3's table.</summary>
    public const int MinGuardKind = 1;

    /// <summary>The last guard kind of spec 10.3's table, which closes the vocabulary.</summary>
    public const int MaxGuardKind = 15;

    /// <summary>The currency this guard belongs to.</summary>
    public const string CraftingCurrencyIdField = "crafting_currency_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int CraftingCurrencyIdIndex = 0;

    /// <summary>The step this guard runs before, or absent for a guard on the target.</summary>
    public const string CurrencyStepIdField = "currency_step_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int CurrencyStepIdIndex = 1;

    /// <summary>The evaluation order within this guard's own set.</summary>
    public const string SortField = "sort";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int SortIndex = 2;

    /// <summary>One of spec 10.3's fifteen guard kinds.</summary>
    public const string GuardKindField = "guard_kind";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int GuardKindIndex = 3;

    /// <summary>The guard's first parameter, 0 when unused.</summary>
    public const string ParameterAField = "parameter_a";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int ParameterAIndex = 4;

    /// <summary>The guard's second parameter, 0 when unused.</summary>
    public const string ParameterBField = "parameter_b";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int ParameterBIndex = 5;

    /// <summary>The ordered field list, spec 10.4's third table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            CraftingCurrencyIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.CraftingCurrencyTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(
            CurrencyStepIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.CurrencyStepTypeKey,
            ContentVisibility.Client,
            false),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(GuardKindField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ParameterAField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ParameterBField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The currency guard row codec. It adds the two IN-ROW bounds the generic walk cannot express, checked
    /// on BOTH sides so an encoder cannot write a row its own decoder refuses.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the currency guard schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                SortField when value.Number < 0 => ReasonFieldMalformed,
                GuardKindField when value.Number is < MinGuardKind or > MaxGuardKind => ReasonFieldMalformed,
                _ => null,
            };
    }
}
