namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>base_socket</c> content type of spec 3.3, one socket an item base is authored WITH, in authored
/// order.
/// <para>
/// <c>item.socket_max</c> is the CAP and these rows are the DECLARATION, and one int could not be both: a
/// count says how many sockets a base may end up with and cannot say which socket TYPES those sockets are
/// nor in what order. A base authored with one socket and a cap of three is an ordinary row, because
/// crafting adds sockets beyond the authored set up to the cap.
/// </para>
/// <para>
/// <c>socket_type</c> is a key reference resolved at registry freeze, the same late binding as
/// <c>item.equip_profile</c>. A game that does not use instances leaves the type unregistered and authors
/// no rows here at all.
/// </para>
/// </summary>
public static class BaseSocketContentType
{
    /// <summary>Id slots per chunk, sized for a child that outnumbers its parent.</summary>
    public const int DefaultChunkSlots = 16384;

    /// <summary>This type's own row cap, which its slot count requires.</summary>
    public const int MaxRowBytes = 512;

    /// <summary>The item base this socket belongs to.</summary>
    public const string ItemField = "item";

    /// <summary>The authored order, which is what makes "the second socket" a stable phrase.</summary>
    public const string SortField = "sort";

    /// <summary>The socket type, restricting what the socket accepts. Late bound at registry freeze.</summary>
    public const string SocketTypeField = "socket_type";

    /// <summary>The ordered field list, spec 3.3's base socket table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            ItemField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            SocketTypeField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.SocketTypeTypeKey,
            ContentVisibility.Client,
            true),
    ]);

    /// <summary>The base socket row codec, which is the generic positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the base socket schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
