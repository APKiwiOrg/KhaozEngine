using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>socket_type</c> content type of spec 8.7, what a socket accepts. Type id
/// <see cref="InstanceContentTypeIds.SocketTypeTypeId"/>.
/// <para>
/// <b>Registering this type is what closes the engine's second late binding.</b>
/// <c>base_socket.socket_type</c> is a key reference at
/// <see cref="EngineContentTypes.SocketTypeTypeKey"/> and its doc comment says "Scope B or a game
/// registers a type under it". Until something does, every <c>base_socket</c> row naming a socket type
/// draws <c>KEC0007</c> and the field has to be 0 on every row. Id 260 under that key is what finally
/// makes the shipped engine type's reference target resolve.
/// </para>
/// <para>
/// A socket type DOES restrict, and the restriction is CONTENT rather than code: the accept and reject
/// tags live in <see cref="SocketTagRuleContentType"/> rows, evaluated by the socket craft primitive and
/// by nothing else.
/// </para>
/// <para>
/// <b><see cref="MaxNestedBytesField"/> of 0 means the whole payload budget</b>, which is
/// <see cref="ItemInstancePayload.MaxInstancePayloadBytes"/> and never a second copy of that number. It
/// exists because a six socket item has about 58 bytes per nested payload before the cap binds, so a
/// socket type that admits a deep item would make that ceiling a runtime surprise. Authoring the per
/// socket budget turns it into a publish-time fact and a refusal at the moment of socketing rather than at
/// the moment of encoding.
/// </para>
/// </summary>
public static class SocketTypeContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table. Socket types are few, so the chunk is the smallest legal one.</summary>
    public const int DefaultChunkSlots = 256;

    /// <summary>This type's row cap, which the default covers for a row of one small field.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client renders a socket from these bytes.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>
    /// The <see cref="MaxNestedBytesField"/> value meaning the whole payload budget rather than no nesting
    /// at all.
    /// </summary>
    public const int FullPayloadBudget = 0;

    /// <summary>
    /// The largest per socket nested budget an author may name, which is the payload cap itself. The number
    /// lives once, on the payload, and is referenced here.
    /// </summary>
    public const int MaxNestedBytesCeiling = ItemInstancePayload.MaxInstancePayloadBytes;

    /// <summary>
    /// The socket's display template, a marker whose derived key is
    /// <c>socket_type.&lt;content key&gt;.display_format</c>.
    /// </summary>
    public const string DisplayFormatField = "display_format";

    /// <summary>The per socket nested payload budget, or <see cref="FullPayloadBudget"/> for the cap.</summary>
    public const string MaxNestedBytesField = "max_nested_bytes";

    /// <summary>The ordered field list, spec 8.7's first table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            DisplayFormatField,
            ContentFieldKind.LocalizedTextKey,
            null,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(MaxNestedBytesField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The socket type row codec. It adds the one IN-ROW bound the generic walk cannot express, checked on
    /// BOTH sides: a budget past the payload cap is a promise the format cannot keep, and a negative one is
    /// not a budget at all.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the socket type schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                MaxNestedBytesField when value.Number is < FullPayloadBudget or > MaxNestedBytesCeiling
                    => ReasonFieldMalformed,
                _ => null,
            };
    }
}
