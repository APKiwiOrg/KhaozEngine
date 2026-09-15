using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// A deterministic synthetic content set built from <c>--seed</c>. Rows are DERIVED from their id rather
/// than held, so the stress figure costs an id array per type instead of a million live objects, and the
/// same seed gives byte-identical packs on every machine.
/// <para>
/// Shape, from spec section 14.1: item rows with two localized marker fields costing zero row bytes, three
/// asset references, a tag list of four and the scalar fields of section 3.3; 200 tags; 300 stats; four
/// loot entries per ten items; base sockets on a third of the bases. Ids are dense from 1 with four family
/// blocks above them, so a few chunks are sparse the way a family block makes them.
/// </para>
/// </summary>
public sealed class SyntheticContentSet
{
    private static readonly string[] Words =
    [
        "sword", "axe", "bow", "staff", "helm", "plate", "boots", "ring",
        "gem", "ore", "log", "fish", "potion", "rune", "shield", "dagger",
    ];

    private static readonly string[] Prose =
    [
        "a well made", "a battered", "an ancient", "a gleaming", "a crude", "a masterwork",
        "piece of gear", "tool of trade", "trinket", "relic", "supply", "component",
        "favoured by miners", "carried by rangers", "sold in every port", "found in deep ruins",
    ];

    private readonly int _seed;

    public SyntheticContentSet(CatalogBenchmarkConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        _seed = config.Seed;
        Types = ContentTypes.Build(config);
        ItemIds = BuildItemIds(config);
        TagIds = Dense(config.TagCount);
        StatIds = Dense(config.StatCount);
        LootTableIds = Dense(config.LootTableCount);
        LootEntryIds = Dense(config.LootEntryCount);
        BaseSocketIds = Dense(config.BaseSocketCount);
        var gameIds = new List<int[]>(config.GameTypeCount);
        for (int i = 0; i < config.GameTypeCount; i++) gameIds.Add(Dense(config.GameTypeRowCount));
        GameTypeIds = gameIds;
        HasEquipProfileType = config.GameTypeCount >= 1;
        HasSocketTypeType = config.GameTypeCount >= 2;
    }

    public CatalogBenchmarkConfig Config { get; }

    public IReadOnlyList<ContentTypeDescriptor> Types { get; }

    public int[] ItemIds { get; }

    public int[] TagIds { get; }

    public int[] StatIds { get; }

    public int[] LootTableIds { get; }

    public int[] LootEntryIds { get; }

    public int[] BaseSocketIds { get; }

    public IReadOnlyList<int[]> GameTypeIds { get; }

    /// <summary>True when a type is registered under the key <c>equip_profile</c>, so the field may be non zero.</summary>
    public bool HasEquipProfileType { get; }

    public bool HasSocketTypeType { get; }

    public int[] IdsFor(ushort typeId) => typeId switch
    {
        ContentTypes.Tag => TagIds,
        ContentTypes.Item => ItemIds,
        ContentTypes.Stat => StatIds,
        ContentTypes.LootTable => LootTableIds,
        ContentTypes.LootEntry => LootEntryIds,
        ContentTypes.BaseSocket => BaseSocketIds,
        _ => GameTypeIds[typeId - ContentTypes.FirstGameTypeId],
    };

    public ContentTypeDescriptor TypeOf(ushort typeId)
    {
        foreach (ContentTypeDescriptor type in Types)
        {
            if (type.TypeId == typeId) return type;
        }
        throw new ArgumentOutOfRangeException(nameof(typeId), typeId, "No such registered type.");
    }

    /// <summary>The content key of a row, under contracts 5.3's character rules.</summary>
    public string KeyFor(ushort typeId, int id)
    {
        string number = id.ToString(CultureInfo.InvariantCulture).PadLeft(6, '0');
        return typeId switch
        {
            ContentTypes.Tag => "tag_" + Words[id % Words.Length] + "_" + number,
            ContentTypes.Item => Words[id % Words.Length] + "_" + number,
            ContentTypes.Stat => "stat_" + number,
            ContentTypes.LootTable => "loot_" + number,
            ContentTypes.LootEntry => "lootent_" + number,
            ContentTypes.BaseSocket => "sock_" + number,
            _ => ContentTypes.GameTypeKey(typeId - ContentTypes.FirstGameTypeId) + "_" + number,
        };
    }

    /// <summary>A row is RETIRED on a fixed fraction, so the retired bit of section 7.3 is exercised.</summary>
    public bool IsRetired(ushort typeId, int id) => typeId == ContentTypes.Item && id % 97 == 0;

    public void Item(int id, ref ItemRowData row)
    {
        var rng = new DeterministicRng(Mix(ContentTypes.Item, id));
        string key = KeyFor(ContentTypes.Item, id);
        row.Id = id;
        row.Key = key;
        row.Retired = IsRetired(ContentTypes.Item, id);
        row.TagCount = 4;
        row.Tag0 = 1 + rng.Next(Config.TagCount);
        row.Tag1 = 1 + rng.Next(Config.TagCount);
        row.Tag2 = 1 + rng.Next(Config.TagCount);
        row.Tag3 = 1 + rng.Next(Config.TagCount);
        row.Stackable = rng.Next(100) < 45;
        row.MaxStack = row.Stackable ? 2 + rng.Next(65_534) : 1;
        row.Tradable = rng.Next(100) < 80;
        row.Value = rng.Next(1, 250_000);
        row.Icon = "ui/icon/" + key + ".png";
        row.Mesh = "kit/ground/" + key + ".glb";
        row.HeldMesh = "kit/held/" + key + ".glb";
        row.GroundPose = rng.Next(2);
        row.IconTilt = rng.Next(-45_000, 45_000);
        row.IconSpin = rng.Next(-180_000, 180_000);
        row.DurabilityMax = row.Stackable ? 0 : rng.Next(0, 4_000);
        row.SocketMax = row.Stackable ? 0 : rng.Next(0, 4);
        row.EquipProfile = HasEquipProfileType && !row.Stackable ? 1 + rng.Next(Config.GameTypeRowCount) : 0;
    }

    public void Tag(int id, ref TagRowData row)
    {
        var rng = new DeterministicRng(Mix(ContentTypes.Tag, id));
        row.Id = id;
        row.Key = KeyFor(ContentTypes.Tag, id);
        row.Retired = false;
        row.Sort = rng.Next(1000);
    }

    public void Stat(int id, ref StatRowData row)
    {
        var rng = new DeterministicRng(Mix(ContentTypes.Stat, id));
        row.Id = id;
        row.Key = KeyFor(ContentTypes.Stat, id);
        row.Retired = false;
        row.Scale = Pow10(rng.Next(4));
        row.Min = -rng.Next(1, 10_000);
        row.Max = rng.Next(1, 1_000_000);
        row.TagCount = 2;
        row.Tag0 = 1 + rng.Next(Config.TagCount);
        row.Tag1 = 1 + rng.Next(Config.TagCount);
    }

    public void LootTable(int id, ref LootTableRowData row)
    {
        var rng = new DeterministicRng(Mix(ContentTypes.LootTable, id));
        row.Id = id;
        row.Key = KeyFor(ContentTypes.LootTable, id);
        row.Retired = false;
        row.RollCount = 1 + rng.Next(3);
        row.TagCount = 1;
        row.Tag0 = 1 + rng.Next(Config.TagCount);
        row.Guaranteed = rng.Next(100) < 20;
    }

    /// <summary>
    /// Four entries per table. An entry names its draw exactly one of three ways, which is what
    /// <c>KEC0023</c> refuses every other count of: an item, a nested table, or a tag filter.
    /// </summary>
    public void LootEntry(int id, ref LootEntryRowData row)
    {
        var rng = new DeterministicRng(Mix(ContentTypes.LootEntry, id));
        row.Id = id;
        row.Key = KeyFor(ContentTypes.LootEntry, id);
        row.Retired = false;
        row.Table = 1 + ((id - 1) / 4) % Config.LootTableCount;
        row.Item = 0;
        row.NestedTable = 0;
        row.RequiredTag0 = 0;
        row.RequiredTagCount = 0;
        int shape = rng.Next(100);
        if (shape < 80)
        {
            row.Item = 1 + rng.Next(Math.Max(1, Config.DenseDefinitions));
        }
        else if (shape < 90 && row.Table < Config.LootTableCount)
        {
            // Nested tables only ever point FORWARD, so the graph is acyclic by construction (KEC0024).
            row.NestedTable = row.Table + 1;
        }
        else
        {
            row.RequiredTagCount = 1;
            row.RequiredTag0 = 1 + rng.Next(Config.TagCount);
        }
        row.Weight = 1 + rng.Next(1000);
        row.ChanceBp = 1 + rng.Next(10_000);
        row.MinCount = 1 + rng.Next(4);
        row.MaxCount = row.MinCount + rng.Next(8);
        row.Sort = (id - 1) % 4;
    }

    public void BaseSocket(int id, ref BaseSocketRowData row)
    {
        var rng = new DeterministicRng(Mix(ContentTypes.BaseSocket, id));
        row.Id = id;
        row.Key = KeyFor(ContentTypes.BaseSocket, id);
        row.Retired = false;
        row.Item = Math.Min(Math.Max(1, Config.DenseDefinitions), ((id - 1) * 3) + 1);
        row.Sort = 0;
        row.SocketType = HasSocketTypeType ? 1 + rng.Next(Config.GameTypeRowCount) : 0;
    }

    public void GameRow(ushort typeId, int id, ref GameRowData row)
    {
        var rng = new DeterministicRng(Mix(typeId, id));
        row.Id = id;
        row.Key = KeyFor(typeId, id);
        row.Retired = false;
        row.Slot = rng.Next(16);
        row.WeaponArchetype = rng.Next(8);
    }

    /// <summary>A localized value, 60 bytes, derived from its key so it never has to be held.</summary>
    public static string TextValue(string derivedKey)
    {
        var rng = new DeterministicRng(StableHash(derivedKey));
        Span<char> buffer = stackalloc char[60];
        int written = 0;
        while (written < 60)
        {
            string word = Prose[rng.Next(Prose.Length)];
            for (int i = 0; i < word.Length && written < 60; i++) buffer[written++] = word[i];
            if (written < 60) buffer[written++] = ' ';
        }
        return new string(buffer);
    }

    public ulong Mix(ushort typeId, int id) =>
        StableHash(((ulong)_seed << 32) ^ ((ulong)typeId << 40) ^ (uint)id);

    private static ulong StableHash(ulong value)
    {
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCDUL;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53UL;
        return value ^ (value >> 33);
    }

    private static ulong StableHash(string value)
    {
        ulong hash = 1469598103934665603UL;
        foreach (char c in value)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static int Pow10(int exponent)
    {
        int value = 1;
        for (int i = 0; i < exponent; i++) value *= 10;
        return value;
    }

    private static int[] Dense(int count)
    {
        int[] ids = new int[count];
        for (int i = 0; i < count; i++) ids[i] = i + 1;
        return ids;
    }

    private static int[] BuildItemIds(CatalogBenchmarkConfig config)
    {
        int dense = config.DenseDefinitions;
        int[] ids = new int[config.Definitions];
        for (int i = 0; i < dense; i++) ids[i] = i + 1;
        int block = config.FamilyBlockSize;
        long firstBase = ((dense + block) / block) * (long)block;
        int cursor = dense;
        for (int f = 0; f < config.FamilyCount; f++)
        {
            long familyBase = firstBase + ((long)f * block);
            for (int m = 0; m < config.FamilyBlockMembers; m++) ids[cursor++] = (int)(familyBase + m);
        }
        return ids;
    }
}
