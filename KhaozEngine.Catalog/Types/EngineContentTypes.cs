using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// The six engine content types of spec 3.1, their stable ids and keys, and the ONE helper that registers
/// them. Six ids out of the engine band's 255 are spent, which is deliberate headroom: an engine release
/// adding a type can never collide with a game's.
/// <para>
/// A caller registers through <see cref="Register"/> rather than by naming a band, because
/// <see cref="ContentRegistrationBand"/> says which ids the caller is entitled to and the engine's
/// entitlement is not a game's to claim.
/// </para>
/// </summary>
public static class EngineContentTypes
{
    /// <summary>The tag vocabulary of contracts 4.6, type id 1.</summary>
    public const ushort TagTypeId = 1;

    /// <summary>The tag type's stable key.</summary>
    public const string TagTypeKey = "tag";

    /// <summary>The item base, type id 2.</summary>
    public const ushort ItemTypeId = 2;

    /// <summary>The item type's stable key.</summary>
    public const string ItemTypeKey = "item";

    /// <summary>The stat definition of contracts 13.1, type id 3.</summary>
    public const ushort StatTypeId = 3;

    /// <summary>The stat type's stable key.</summary>
    public const string StatTypeKey = "stat";

    /// <summary>A named drop or reward table, type id 4.</summary>
    public const ushort LootTableTypeId = 4;

    /// <summary>The loot table type's stable key.</summary>
    public const string LootTableTypeKey = "loot_table";

    /// <summary>One weighted row of one loot table, type id 5.</summary>
    public const ushort LootEntryTypeId = 5;

    /// <summary>The loot entry type's stable key.</summary>
    public const string LootEntryTypeKey = "loot_entry";

    /// <summary>One socket an item base is authored with, type id 6.</summary>
    public const ushort BaseSocketTypeId = 6;

    /// <summary>The base socket type's stable key.</summary>
    public const string BaseSocketTypeKey = "base_socket";

    /// <summary>
    /// The type key <c>item.equip_profile</c> points at. The engine writes the key once, here, and a GAME
    /// registers a type under it in its own id range. With no type registered under the key the field must
    /// be 0 on every row, which is <c>KEC0007</c>'s refusal rather than this type's.
    /// </summary>
    public const string EquipProfileTypeKey = "equip_profile";

    /// <summary>
    /// The type key <c>base_socket.socket_type</c> points at, the second use of the same late binding.
    /// Scope B or a game registers a type under it, and a game that uses no sockets authors no
    /// <c>base_socket</c> rows at all.
    /// </summary>
    public const string SocketTypeTypeKey = "socket_type";

    /// <summary>
    /// Registers all six engine types, once, at process start and before any pack loads.
    /// </summary>
    /// <exception cref="ContentRegistrationException">
    /// The registry is frozen, or one of the six ids or keys is already taken, which a second call to this
    /// helper on the same registry is.
    /// </exception>
    public static void Register(ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        RegisterOne(
            registry,
            TagTypeId,
            TagTypeKey,
            TagContentType.CreateSchema(),
            ContentVisibility.Client,
            TagContentType.DefaultChunkSlots,
            ContentPackFormat.DefaultMaxRowBytes,
            static (type, schema) => new TagContentType.Codec(type, schema));

        RegisterOne(
            registry,
            ItemTypeId,
            ItemTypeKey,
            ItemContentType.CreateSchema(),
            ContentVisibility.Client,
            ItemContentType.DefaultChunkSlots,
            ContentPackFormat.DefaultMaxRowBytes,
            static (type, schema) => new ItemContentType.Codec(type, schema));

        RegisterOne(
            registry,
            StatTypeId,
            StatTypeKey,
            StatContentType.CreateSchema(),
            ContentVisibility.Client,
            StatContentType.DefaultChunkSlots,
            ContentPackFormat.DefaultMaxRowBytes,
            static (type, schema) => new StatContentType.Codec(type, schema));

        RegisterOne(
            registry,
            LootTableTypeId,
            LootTableTypeKey,
            LootTableContentType.CreateSchema(),
            ContentVisibility.ServerOnly,
            LootTableContentType.DefaultChunkSlots,
            ContentPackFormat.DefaultMaxRowBytes,
            static (type, schema) => new LootTableContentType.Codec(type, schema));

        RegisterOne(
            registry,
            LootEntryTypeId,
            LootEntryTypeKey,
            LootEntryContentType.CreateSchema(),
            ContentVisibility.ServerOnly,
            LootEntryContentType.DefaultChunkSlots,
            LootEntryContentType.MaxRowBytes,
            static (type, schema) => new LootEntryContentType.Codec(type, schema));

        RegisterOne(
            registry,
            BaseSocketTypeId,
            BaseSocketTypeKey,
            BaseSocketContentType.CreateSchema(),
            ContentVisibility.Client,
            BaseSocketContentType.DefaultChunkSlots,
            BaseSocketContentType.MaxRowBytes,
            static (type, schema) => new BaseSocketContentType.Codec(type, schema));
    }

    static void RegisterOne(
        ContentTypeRegistry registry,
        ushort typeId,
        string typeKey,
        ContentFieldSchema schema,
        ContentVisibility visibility,
        int chunkSlots,
        int maxRowBytes,
        Func<ContentTypeId, ContentFieldSchema, IContentRowCodec> codec)
        => registry.RegisterContentType(
            ContentRegistrationBand.Engine,
            typeId,
            typeKey,
            codec(new ContentTypeId(typeId), schema),
            validator: null,
            schema,
            visibility,
            chunkSlots,
            maxRowBytes);
}
