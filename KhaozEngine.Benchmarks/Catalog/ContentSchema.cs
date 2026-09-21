using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>Contracts 4.7's value kinds, plus the asset reference CCR-1 asks for.</summary>
public enum ContentValueKind
{
    Int,
    ScaledInt,
    Bool,
    KeyReference,
    TagList,
    LocalizedTextKey,
    AssetReference,
}

/// <summary>One entry of a type's field schema (contracts 4.7).</summary>
public sealed record ContentFieldSchema(
    string Name,
    ContentValueKind Kind,
    string? ReferenceTarget,
    byte Visibility,
    bool Required,
    int Scale = 1)
{
    /// <summary>
    /// A localized text key is a MARKER: it carries no value and the row carries no bytes for it
    /// (spec section 3.2, contracts CCR-3).
    /// </summary>
    public bool IsMarker => Kind == ContentValueKind.LocalizedTextKey;
}

/// <summary>A registered content type: its id, key, schema, visibility, chunk slots and row cap.</summary>
public sealed record ContentTypeDescriptor(
    ushort TypeId,
    string TypeKey,
    IReadOnlyList<ContentFieldSchema> Fields,
    byte DefaultVisibility,
    int ChunkSlots,
    int MaxRowBytes)
{
    /// <summary>The registration bound of section 7.1, checked before the registry freezes.</summary>
    public bool FitsChunkBound() =>
        (long)ChunkSlots * (MaxRowBytes + 8) + ContentPackFormat.ChunkHeaderBytes
            <= ContentPackFormat.MaxChunkUncompressedBytes;

    public int ChunkIndexOf(int definitionId) => definitionId / ChunkSlots;
}

/// <summary>
/// The six engine content types of spec section 3.1, plus the synthetic game types <c>--types</c> adds.
/// Type ids and keys are section 3.1's; the field schemas are sections 3.2 to 3.5.
/// </summary>
public static class ContentTypes
{
    public const ushort Tag = 1;
    public const ushort Item = 2;
    public const ushort Stat = 3;
    public const ushort LootTable = 4;
    public const ushort LootEntry = 5;
    public const ushort BaseSocket = 6;
    public const ushort FirstGameTypeId = 1024;

    private const byte Client = ContentPackFormat.VisibilityClient;
    private const byte ServerOnly = ContentPackFormat.VisibilityServerOnly;

    public static IReadOnlyList<ContentTypeDescriptor> Build(CatalogBenchmarkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var types = new List<ContentTypeDescriptor>(config.Types)
        {
            new(Tag, "tag", TagFields(), Client, 4096, ContentPackFormat.DefaultMaxRowBytes),
            new(Item, "item", ItemFields(), Client, config.ChunkSlots, ContentPackFormat.DefaultMaxRowBytes),
            new(Stat, "stat", StatFields(), Client, 4096, ContentPackFormat.DefaultMaxRowBytes),
            new(LootTable, "loot_table", LootTableFields(), ServerOnly, 4096, ContentPackFormat.DefaultMaxRowBytes),
            new(LootEntry, "loot_entry", LootEntryFields(), ServerOnly, 16384, 512),
            new(BaseSocket, "base_socket", BaseSocketFields(), Client, 16384, 512),
        };
        for (int i = 0; i < config.GameTypeCount; i++)
        {
            ushort id = (ushort)(FirstGameTypeId + i);
            types.Add(new ContentTypeDescriptor(id, GameTypeKey(i), GameFields(), Client, 4096,
                ContentPackFormat.DefaultMaxRowBytes));
        }
        foreach (ContentTypeDescriptor type in types)
        {
            if (!type.FitsChunkBound())
                throw new InvalidOperationException($"Type '{type.TypeKey}' fails the chunk bound of section 7.1.");
        }
        return types;
    }

    /// <summary>
    /// The first two game types take the keys the engine's own schema reaches for late (section 3.3):
    /// <c>equip_profile</c> on <c>item</c> and <c>socket_type</c> on <c>base_socket</c>. Registering them
    /// is what turns those two fields from a forced zero into a checked key reference.
    /// </summary>
    public static string GameTypeKey(int ordinal) => ordinal switch
    {
        0 => "equip_profile",
        1 => "socket_type",
        _ => "game_type_" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static ContentFieldSchema[] TagFields() =>
    [
        new("name", ContentValueKind.LocalizedTextKey, null, Client, true),
        new("sort", ContentValueKind.Int, null, Client, false),
    ];

    private static ContentFieldSchema[] ItemFields() =>
    [
        new("name", ContentValueKind.LocalizedTextKey, null, Client, true),
        new("examine", ContentValueKind.LocalizedTextKey, null, Client, false),
        new("tags", ContentValueKind.TagList, "tag", Client, false),
        new("stackable", ContentValueKind.Bool, null, Client, true),
        new("max_stack", ContentValueKind.Int, null, Client, true),
        new("tradable", ContentValueKind.Bool, null, Client, true),
        new("value", ContentValueKind.ScaledInt, null, Client, true),
        new("icon", ContentValueKind.AssetReference, null, Client, false),
        new("mesh", ContentValueKind.AssetReference, null, Client, false),
        new("held_mesh", ContentValueKind.AssetReference, null, Client, false),
        new("ground_pose", ContentValueKind.Int, null, Client, false),
        new("icon_tilt", ContentValueKind.ScaledInt, null, Client, false, 1000),
        new("icon_spin", ContentValueKind.ScaledInt, null, Client, false, 1000),
        new("durability_max", ContentValueKind.Int, null, Client, false),
        new("socket_max", ContentValueKind.Int, null, Client, false),
        new("equip_profile", ContentValueKind.KeyReference, "equip_profile", Client, false),
        new("category", ContentValueKind.KeyReference, "item_category", Client, false),
    ];

    private static ContentFieldSchema[] StatFields() =>
    [
        new("name", ContentValueKind.LocalizedTextKey, null, Client, true),
        new("scale", ContentValueKind.Int, null, Client, true),
        new("min", ContentValueKind.Int, null, Client, true),
        new("max", ContentValueKind.Int, null, Client, true),
        new("tags", ContentValueKind.TagList, "tag", Client, false),
        new("display_format", ContentValueKind.LocalizedTextKey, null, Client, true),
    ];

    private static ContentFieldSchema[] LootTableFields() =>
    [
        new("roll_count", ContentValueKind.Int, null, ServerOnly, true),
        new("tags", ContentValueKind.TagList, "tag", ServerOnly, false),
        new("guaranteed", ContentValueKind.Bool, null, ServerOnly, true),
    ];

    private static ContentFieldSchema[] LootEntryFields() =>
    [
        new("table", ContentValueKind.KeyReference, "loot_table", ServerOnly, true),
        new("item", ContentValueKind.KeyReference, "item", ServerOnly, false),
        new("nested_table", ContentValueKind.KeyReference, "loot_table", ServerOnly, false),
        new("weight", ContentValueKind.Int, null, ServerOnly, true),
        new("chance_bp", ContentValueKind.Int, null, ServerOnly, true),
        new("min_count", ContentValueKind.Int, null, ServerOnly, true),
        new("max_count", ContentValueKind.Int, null, ServerOnly, true),
        new("sort", ContentValueKind.Int, null, ServerOnly, true),
        new("required_tags", ContentValueKind.TagList, "tag", ServerOnly, false),
    ];

    private static ContentFieldSchema[] BaseSocketFields() =>
    [
        new("item", ContentValueKind.KeyReference, "item", Client, true),
        new("sort", ContentValueKind.Int, null, Client, true),
        new("socket_type", ContentValueKind.KeyReference, "socket_type", Client, true),
    ];

    private static ContentFieldSchema[] GameFields() =>
    [
        new("name", ContentValueKind.LocalizedTextKey, null, Client, true),
        new("slot", ContentValueKind.Int, null, Client, true),
        new("weapon_archetype", ContentValueKind.Int, null, Client, false),
    ];
}
