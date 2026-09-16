using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>unique_socket</c> content type of spec 8.6, one socket a unique template forces, in authored
/// order. Child of <c>unique_template</c>, type id
/// <see cref="InstanceContentTypeIds.UniqueSocketTypeId"/>.
/// <para>
/// <b>These rows ARE the socket order.</b> An earlier draft of the spec carried <c>(socket type id, count)</c>
/// pairs, which could not say what order the sockets sat in, and kind 132 is AUTHORED order rather than a
/// sorted list. One row per socket with a <see cref="SortField"/> is both smaller and the only shape that
/// can seed the field.
/// </para>
/// <para>
/// <b><see cref="SortField"/> IS the socket's index in kind 132</b>, not merely an ordering key, which is
/// why the codec refuses a negative one: an index into a payload field is never below zero. It has no
/// CEILING, because kind 132's <c>Count</c> is a varint rather than a byte (spec 3.3), and narrowing it
/// here would reintroduce the width divergence that note exists to prevent.
/// </para>
/// <para>
/// <see cref="SocketTypeIdField"/> of 0 means NO RESTRICTION, which is the socket entry's own convention
/// (spec 3.5), so the field is optional and its zero form reads back absent.
/// </para>
/// </summary>
public static class UniqueSocketContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>This type's row cap, which the default covers for a row of three small fields.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client renders a unique's sockets from these bytes.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The smallest legal socket index, which is the first slot of kind 132.</summary>
    public const int MinSocketIndex = 0;

    /// <summary>The unique template this socket belongs to.</summary>
    public const string UniqueTemplateIdField = "unique_template_id";

    /// <summary>The socket's index in kind 132, authored order.</summary>
    public const string SortField = "sort";

    /// <summary>The socket type restricting what the socket accepts, or absent for no restriction.</summary>
    public const string SocketTypeIdField = "socket_type_id";

    /// <summary>The ordered field list, spec 8.6's third table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            UniqueTemplateIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.UniqueTemplateTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            SocketTypeIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.SocketTypeTypeKey,
            ContentVisibility.Client,
            false),
    ]);

    /// <summary>
    /// The unique socket row codec. It adds the one IN-ROW bound the generic walk cannot express, checked on
    /// BOTH sides. Whether two sockets of one template share an index is a cross-row rule and is the
    /// validator's <c>KEC0115</c>.
    /// <para>
    /// <b>CONTIGUITY is not checked and is not a rule spec 8.6 states.</b> The table there says the sort is
    /// the socket's index in kind 132 in authored order and says nothing about gaps, and nothing seeds kind
    /// 132 from these rows yet, so a gap has no reader to confuse.
    /// Deciding it is
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/966">966</see>.
    /// </para>
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the unique socket schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                SortField when value.Number < MinSocketIndex => ReasonFieldMalformed,
                _ => null,
            };
    }
}
