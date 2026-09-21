using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// The <c>item</c> content type of spec 3.3, the item base. Every field replaces a real consumer field the
/// three surveys found, and nothing here is speculative.
/// <para>
/// It takes 1,024 id slots where every other engine type takes more, which is a download decision rather
/// than a storage one: a chunk is the unit of re-download, and the type that is edited one row at a time
/// wants the smallest chunk that still amortizes a manifest entry.
/// </para>
/// <para>
/// <c>icon</c>, <c>mesh</c> and <c>held_mesh</c> name a FILE rather than a row, so they cannot be content
/// keys, which carry no dot and no slash. On the contracts as written they are opaque bytes with a declared
/// shape, and this type's codec is where that shape is enforced: at most 128 bytes, character set
/// <c>a-z0-9_./-</c>. CCR-1 asks the contracts for an asset-reference kind of its own, which would move the
/// check into the generic walk and change nothing else.
/// </para>
/// </summary>
public static class ItemContentType
{
    /// <summary>Id slots per chunk, the smallest of the six, for the re-download reason above.</summary>
    public const int DefaultChunkSlots = 1024;

    /// <summary>The most bytes an asset reference may carry, contracts 4.7.</summary>
    public const int MaxAssetReferenceBytes = 128;

    /// <summary>The derived display name.</summary>
    public const string NameField = "name";

    /// <summary>The derived examine text.</summary>
    public const string ExamineField = "examine";

    /// <summary>The item's tags, in authored order, which replace a predicate in code.</summary>
    public const string TagsField = "tags";

    /// <summary>Whether several of the item occupy one slot.</summary>
    public const string StackableField = "stackable";

    /// <summary>The largest stack one slot holds.</summary>
    public const string MaxStackField = "max_stack";

    /// <summary>Whether the item may change hands.</summary>
    public const string TradableField = "tradable";

    /// <summary>The economy value.</summary>
    public const string ValueField = "value";

    /// <summary>The inventory icon, an asset reference.</summary>
    public const string IconField = "icon";

    /// <summary>The ground mesh, an asset reference.</summary>
    public const string MeshField = "mesh";

    /// <summary>The held mesh, an asset reference.</summary>
    public const string HeldMeshField = "held_mesh";

    /// <summary>How the item sits on the ground. 0 upright, 1 lie flat.</summary>
    public const string GroundPoseField = "ground_pose";

    /// <summary>The icon shot's tilt, in thousandths.</summary>
    public const string IconTiltField = "icon_tilt";

    /// <summary>The icon shot's spin, in thousandths.</summary>
    public const string IconSpinField = "icon_spin";

    /// <summary>The durability a fresh instance starts at. 0 means the base has no durability.</summary>
    public const string DurabilityMaxField = "durability_max";

    /// <summary>The socket CAP, not the authored set. 0 means the base takes no sockets.</summary>
    public const string SocketMaxField = "socket_max";

    /// <summary>The game-owned equip profile, late bound at registry freeze.</summary>
    public const string EquipProfileField = "equip_profile";

    /// <summary>
    /// The one <c>item_category</c> row this base belongs to, or absent when it belongs to none. It is the
    /// LAST field of the schema and it is optional, which is what lets a catalog published before the field
    /// existed keep decoding: a body that ends where the old schema ended reads it absent.
    /// </summary>
    public const string CategoryField = "category";

    /// <summary>The scale <c>icon_tilt</c> and <c>icon_spin</c> store their thousandths under.</summary>
    const int IconAngleScale = 1000;

    /// <summary>
    /// The ordered field list, spec 3.3's table plus <c>category</c> appended after it.
    /// <para>
    /// <b>A field is only ever APPENDED here, and only ever as an optional one.</b> A row body is a
    /// positional walk, so inserting anywhere else moves every field after it and repoints every published
    /// row. Appending an optional field leaves every existing index where it was and costs one zero byte on a
    /// row that does not carry it.
    /// </para>
    /// </summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ExamineField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false),
        new ContentFieldEntry(
            TagsField,
            ContentFieldKind.TagList,
            ContentFieldEntry.TagReferenceTarget,
            ContentVisibility.Client,
            false),
        new ContentFieldEntry(StackableField, ContentFieldKind.Bool, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MaxStackField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(TradableField, ContentFieldKind.Bool, null, ContentVisibility.Client, true),
        new ContentFieldEntry(ValueField, ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true),
        new ContentFieldEntry(IconField, ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false),
        new ContentFieldEntry(MeshField, ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false),
        new ContentFieldEntry(HeldMeshField, ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false),
        new ContentFieldEntry(GroundPoseField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
        new ContentFieldEntry(
            IconTiltField,
            ContentFieldKind.ScaledInt,
            null,
            ContentVisibility.Client,
            false,
            IconAngleScale),
        new ContentFieldEntry(
            IconSpinField,
            ContentFieldKind.ScaledInt,
            null,
            ContentVisibility.Client,
            false,
            IconAngleScale),
        new ContentFieldEntry(DurabilityMaxField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
        new ContentFieldEntry(SocketMaxField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
        new ContentFieldEntry(
            EquipProfileField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.EquipProfileTypeKey,
            ContentVisibility.Client,
            false),
        new ContentFieldEntry(
            CategoryField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemCategoryTypeKey,
            ContentVisibility.Client,
            false),
    ]);

    /// <summary>
    /// True for an asset reference the engine will carry: at most <see cref="MaxAssetReferenceBytes"/>
    /// bytes, every one of them <c>a-z</c>, <c>0-9</c>, <c>_</c>, <c>.</c>, <c>/</c> or <c>-</c>.
    /// <para>
    /// The engine neither resolves nor loads the value. It is a name the game's own asset layer looks up,
    /// and the codec checks the character set and the length and nothing else.
    /// </para>
    /// </summary>
    public static bool IsAssetReference(ReadOnlySpan<byte> reference)
    {
        if (reference.Length > MaxAssetReferenceBytes)
        {
            return false;
        }

        foreach (byte c in reference)
        {
            bool allowed = c is >= (byte)'a' and <= (byte)'z'
                || c is >= (byte)'0' and <= (byte)'9'
                || c is (byte)'_' or (byte)'.' or (byte)'/' or (byte)'-';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The item row codec: the generic positional walk plus the one constraint the walk cannot express,
    /// the asset-reference cap and character set on the three file-naming fields.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the item schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
        {
            if (field.Name is not (IconField or MeshField or HeldMeshField))
            {
                return null;
            }

            return IsAssetReference(value.Bytes.Span) ? null : ReasonFieldMalformed;
        }
    }
}
