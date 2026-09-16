using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>socket_tag_rule</c> content type of spec 8.7, one accept or reject tag of a socket type. Child
/// of <c>socket_type</c>, type id <see cref="InstanceContentTypeIds.SocketTagRuleTypeId"/>.
/// <para>
/// <b><see cref="RuleField"/> is <see cref="RuleAccept"/> for accept and <see cref="RuleReject"/> for
/// reject, and REJECT is checked FIRST and WINS.</b> A contained item must carry at least one accept tag
/// and none of the reject tags, so a type that accepts <c>gem</c> and rejects <c>corrupted</c> is one
/// parent row and two child rows. The evaluation itself belongs to the socket craft primitive and to
/// nothing else, and is not this type's code.
/// </para>
/// <para>
/// <b>A socket type with NO accept row accepts NOTHING</b>, which is a legal state rather than an error and
/// is how a decorative socket is authored. Nothing anywhere reads an empty accept set as "accepts
/// everything".
/// </para>
/// <para>
/// Accept versus reject is a property of the PAIR, which is why this is a child type rather than two tag
/// list fields on the parent. Two fields would put the distinction in a field NAME, where a generic editor
/// and a publish diff cannot see it as one decision.
/// </para>
/// <para>
/// <see cref="SortField"/> is evaluation order WITHIN one rule kind. Every CROSS-ROW rule belongs to the
/// validator, including spec 8.9 check 8: a socket type's accept and reject tag sets are disjoint.
/// </para>
/// </summary>
public static class SocketTagRuleContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child of a type with few rows.</summary>
    public const int DefaultChunkSlots = 1024;

    /// <summary>This type's row cap, which the default covers for a row of four small fields.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client explains a refused socket from these bytes.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The rule kind that ADMITS a tag. A socket type with none of these accepts nothing.</summary>
    public const int RuleAccept = 1;

    /// <summary>The rule kind that REFUSES a tag, checked before every accept row and winning over them.</summary>
    public const int RuleReject = 2;

    /// <summary>The socket type this rule belongs to.</summary>
    public const string SocketTypeIdField = "socket_type_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int SocketTypeIdIndex = 0;

    /// <summary>The evaluation order within this row's own rule kind.</summary>
    public const string SortField = "sort";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int SortIndex = 1;

    /// <summary>The tag the rule is about.</summary>
    public const string TagIdField = "tag_id";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int TagIdIndex = 2;

    /// <summary>Whether the rule accepts or rejects, one of the two rule kinds.</summary>
    public const string RuleField = "rule";

    /// <summary>Its position in <see cref="CreateSchema"/>, which is its index in every row.</summary>
    public const int RuleIndex = 3;

    /// <summary>The ordered field list, spec 8.7's second table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            SocketTypeIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.SocketTypeTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            TagIdField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.TagTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(RuleField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The socket tag rule row codec. It adds the one IN-ROW domain the generic walk cannot express,
    /// checked on BOTH sides. A third rule kind is not a value an author can write, because the primitive
    /// that reads it knows exactly two and a third would be silently ignored.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the socket tag rule schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                RuleField when value.Number is not (RuleAccept or RuleReject) => ReasonFieldMalformed,
                _ => null,
            };
    }
}
