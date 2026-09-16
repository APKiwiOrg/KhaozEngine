using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>rare_name_word</c> content type of spec 8.8, one word in a rare-name position. Type id
/// <see cref="InstanceContentTypeIds.RareNameWordTypeId"/>, and the id payload kind 134 stores.
/// <para>
/// <b><see cref="TextField"/> is the word itself and it stores NOTHING.</b> It is a marker whose derived
/// key is <c>rare_name_word.&lt;content key&gt;.text</c>, which is the name contracts 12.3 illustrates, so
/// a translator adding a language adds a text chunk rather than a content edit.
/// </para>
/// <para>
/// <b>Kind 134 stores the word IDS in POSITION ORDER</b>, so the name is reproducible from the payload
/// with no re-roll, which is contracts 14.3's record-the-outcome rule applied to a name. A rare name is
/// <c>rarity_rule.name_word_positions</c> words drawn one per position, each from the words whose
/// <see cref="PositionField"/> matches and whose weight against the base's tags is above zero.
/// </para>
/// <para>
/// <b>The COMPOSED name is localization's problem and the template lives on the rarity rule.</b> Kind 134
/// stores ids, <see cref="RarityRuleContentType.DisplayFormatField"/> holds the template, and the display
/// layer composes them. A language whose adjective follows its noun composes the same ids differently,
/// which is exactly why the words are ids rather than a string.
/// </para>
/// <para>
/// <see cref="PositionField"/> is <see cref="MinPosition"/> to <see cref="MaxPosition"/>: which slot in
/// the name the word may fill. The ceiling is kind 134's <c>WordCount</c> byte, and 0 is no slot at all.
/// The expensive cross-product rule, that every position of every rarity has a word with a non-zero weight
/// for every reachable base tag, is spec 8.9 check 9 and runs once per publish in the validator.
/// </para>
/// </summary>
public static class RareNameWordContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>This type's row cap, which the default covers for a row of one small field.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client composes the name from these bytes.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The first name slot a word may fill.</summary>
    public const int MinPosition = 1;

    /// <summary>The last name slot a word may fill, which is the width of kind 134's word count byte.</summary>
    public const int MaxPosition = 255;

    /// <summary>The word itself, derived as <c>rare_name_word.&lt;content key&gt;.text</c>, carrying no bytes.</summary>
    public const string TextField = "text";

    /// <summary>Which slot in the name the word may fill.</summary>
    public const string PositionField = "position";

    /// <summary>The ordered field list, spec 8.8's first table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(TextField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
        new ContentFieldEntry(PositionField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The rare name word row codec. It adds the one IN-ROW bound the generic walk cannot express, checked
    /// on BOTH sides so an encoder cannot write a row its own decoder refuses.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the rare name word schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                PositionField when value.Number is < MinPosition or > MaxPosition => ReasonFieldMalformed,
                _ => null,
            };
    }
}
