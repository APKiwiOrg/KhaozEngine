using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>currency_step</c> content type of spec 10.4, one step of one currency: a primitive or a game
/// operation with its parameters. Child of <c>crafting_currency</c>, type id
/// <see cref="InstanceContentTypeIds.CurrencyStepTypeId"/>.
/// <para>
/// <b><see cref="OperationField"/> is ONE number covering two vocabularies</b>, split by
/// <see cref="FirstGameOperation"/>. <see cref="MinPrimitiveOperation"/> to
/// <see cref="MaxPrimitiveOperation"/> is one of the fourteen primitives spec 10.2 closes, and anything at
/// or above 1,024 is a game operation resolved through the registry. The gap between them is refused on
/// both sides, because a step naming 20 is an author reaching for a primitive that does not exist and a
/// step the evaluator silently skipped would be a craft that quietly did less than its row says.
/// </para>
/// <para>
/// The two vocabularies share the step list ON PURPOSE, so one currency can mix a primitive with a game
/// operation, which is the whole reason the operation registry exists.
/// </para>
/// <para>
/// <see cref="SortField"/> is the step's position and it carries no meaning beyond order. It is REQUIRED
/// and unique within its currency, which is the validator's cross-row rule rather than this codec's.
/// </para>
/// <para>
/// The four parameters are REQUIRED and carry 0 when unused, so a parameter an author zeroed and a
/// parameter nobody wrote are the same authored row rather than two states the evaluator has to tell
/// apart. They are deliberately UNBOUNDED: a parameter is a delta on one primitive and an absolute on the
/// next, so a negative value is ordinary and the primitive that reads the pair is what knows its domain.
/// A selector occupies two of them, its kind then its parameter.
/// </para>
/// </summary>
public static class CurrencyStepContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child that outnumbers its parent.</summary>
    public const int DefaultChunkSlots = 16384;

    /// <summary>
    /// This type's own row cap, which its slot count requires: 16,384 slots at the 1,024 byte default would
    /// build a chunk past the uncompressed ceiling no reader loads, which leaves 1,015 bytes per row. A step
    /// row is a key, a reference and six varints, so 512 is already generous.
    /// </summary>
    public const int MaxRowBytes = 512;

    /// <summary>The type level visibility, spec 8.1. A client lists a currency's steps.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The first of the fourteen primitives of spec 10.2.</summary>
    public const int MinPrimitiveOperation = 1;

    /// <summary>The last of them.</summary>
    public const int MaxPrimitiveOperation = 14;

    /// <summary>The first operation number a GAME may register, spec 10.5.</summary>
    public const int FirstGameOperation = 1024;

    /// <summary>The currency this step belongs to.</summary>
    public const string CraftingCurrencyIdField = "crafting_currency_id";

    /// <summary>The step's position, unique within the currency.</summary>
    public const string SortField = "sort";

    /// <summary>A primitive of spec 10.2, or a game operation of spec 10.5.</summary>
    public const string OperationField = "operation";

    /// <summary>The operation's first parameter, 0 when unused.</summary>
    public const string ParameterAField = "parameter_a";

    /// <summary>The operation's second parameter, 0 when unused.</summary>
    public const string ParameterBField = "parameter_b";

    /// <summary>The operation's third parameter, 0 when unused.</summary>
    public const string ParameterCField = "parameter_c";

    /// <summary>The operation's fourth parameter, 0 when unused.</summary>
    public const string ParameterDField = "parameter_d";

    /// <summary>True for an operation number naming one of the fourteen primitives.</summary>
    public static bool IsPrimitive(long operation)
        => operation is >= MinPrimitiveOperation and <= MaxPrimitiveOperation;

    /// <summary>True for an operation number a game registers rather than the engine.</summary>
    public static bool IsGameOperation(long operation) => operation >= FirstGameOperation;

    /// <summary>The ordered field list, spec 10.4's second table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            CraftingCurrencyIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.CraftingCurrencyTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(OperationField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ParameterAField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ParameterBField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ParameterCField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ParameterDField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The currency step row codec. It adds the two IN-ROW domains the generic walk cannot express, checked
    /// on BOTH sides so an encoder cannot write a row its own decoder refuses.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the currency step schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                SortField when value.Number < 0 => ReasonFieldMalformed,
                OperationField when !IsPrimitive(value.Number) && !IsGameOperation(value.Number)
                    => ReasonFieldMalformed,
                _ => null,
            };
    }
}
