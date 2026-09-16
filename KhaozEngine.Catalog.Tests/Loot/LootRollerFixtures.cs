using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Catalog.Runtime;

namespace KhaozEngine.Tests.Catalog.Loot;

/// <summary>
/// The tables the roller is pinned against, small enough that every draw of a seeded roll can be worked out
/// by hand from the source's own sequence.
/// <para>
/// The chest table composes all four shapes spec 3.5 lets a table carry: a guaranteed entry rolling its own
/// chance, a guaranteed entry recursing into another table, a weighted item entry and a weighted tag filter.
/// Its items come from <see cref="CatalogRuntimeFixtures"/>, so the sword tag resolves to items 1 and 2 with
/// the retired 35 excluded.
/// </para>
/// </summary>
internal static class LootRollerFixtures
{
    /// <summary>The table every exact-drop test rolls, whose four entries cover all four shapes.</summary>
    public const int ChestTable = 301;

    /// <summary>The table the chest's second entry recurses into.</summary>
    public const int RareTable = 302;

    /// <summary>A table whose guaranteed entry has a weight and never competes for it.</summary>
    public const int PoolTable = 303;

    /// <summary>The first half of the cycle a hostile pack could carry, which KEC0024 refuses at publish.</summary>
    public const int CycleTable = 311;

    /// <summary>The second half of that cycle, which names the first back.</summary>
    public const int CycleTableBack = 312;

    /// <summary>The loaded runtime the roller reads.</summary>
    public static ContentRuntime Runtime()
    {
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        var builder = new ContentSnapshotBuilder(registry).WithIdentity(7, new string('a', 64));

        CatalogRuntimeFixtures.AddTag(builder, registry, CatalogRuntimeFixtures.SwordTag, "sword");
        CatalogRuntimeFixtures.AddTag(builder, registry, CatalogRuntimeFixtures.MetalTag, "metal");
        CatalogRuntimeFixtures.AddItem(builder, registry, 1, "item_1", [CatalogRuntimeFixtures.SwordTag]);
        CatalogRuntimeFixtures.AddItem(
            builder, registry, 2, "item_2", [CatalogRuntimeFixtures.SwordTag, CatalogRuntimeFixtures.MetalTag]);
        CatalogRuntimeFixtures.AddItem(builder, registry, 8, "item_8", [CatalogRuntimeFixtures.MetalTag]);
        CatalogRuntimeFixtures.AddItem(
            builder, registry, 35, "item_35", [CatalogRuntimeFixtures.SwordTag], isRetired: true);

        AddTable(builder, registry, ChestTable, "chest", rollCount: 2);
        AddTable(builder, registry, RareTable, "rare", rollCount: 1);
        AddTable(builder, registry, PoolTable, "pool", rollCount: 3);
        AddTable(builder, registry, CycleTable, "cycle", rollCount: 0);
        AddTable(builder, registry, CycleTableBack, "cycle_back", rollCount: 0);

        // The chest, in sort order: a guaranteed item on a half chance, a guaranteed recursion that is
        // certain, then the two entries the weighted picks choose between.
        AddEntry(builder, registry, 401, ChestTable, sort: 0, guaranteed: true, item: 2, chance: 5_000, min: 1, max: 3);
        AddEntry(builder, registry, 402, ChestTable, sort: 1, guaranteed: true, nestedTable: RareTable);
        AddEntry(builder, registry, 403, ChestTable, sort: 2, weight: 30, item: 1, min: 2, max: 2);
        AddEntry(
            builder,
            registry,
            404,
            ChestTable,
            sort: 3,
            weight: 70,
            requiredTags: [CatalogRuntimeFixtures.SwordTag]);

        AddEntry(builder, registry, 405, RareTable, sort: 0, weight: 5, item: 8);

        // The pool: a guaranteed entry carrying a weight it can never win with, and one weighted entry.
        AddEntry(builder, registry, 406, PoolTable, sort: 0, guaranteed: true, weight: 1_000, item: 2, chance: 0);
        AddEntry(builder, registry, 407, PoolTable, sort: 1, weight: 1, item: 8);

        // The cycle: each table drops one certain item and then recurses into the other, forever.
        AddEntry(builder, registry, 408, CycleTable, sort: 0, guaranteed: true, item: 1);
        AddEntry(builder, registry, 409, CycleTable, sort: 1, guaranteed: true, nestedTable: CycleTableBack);
        AddEntry(builder, registry, 410, CycleTableBack, sort: 0, guaranteed: true, item: 8);
        AddEntry(builder, registry, 411, CycleTableBack, sort: 1, guaranteed: true, nestedTable: CycleTable);

        return ContentRuntime.FromSnapshot(builder.Build(), registry);
    }

    static void AddTable(
        ContentSnapshotBuilder builder,
        ContentTypeRegistry registry,
        int id,
        string key,
        int rollCount)
    {
        var row = new ContentRow(
            new ContentTypeId(EngineContentTypes.LootTableTypeId),
            id,
            new ContentKey(key),
            0,
            false,
            [
                ContentFieldValue.OfNumber(ContentFieldKind.Int, rollCount),
                ContentFieldValue.Absent(ContentFieldKind.TagList),
            ]);

        builder.AddRow(row, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.LootTableTypeKey, row));
    }

    static void AddEntry(
        ContentSnapshotBuilder builder,
        ContentTypeRegistry registry,
        int id,
        int table,
        int sort,
        int weight = 0,
        int item = 0,
        int nestedTable = 0,
        int chance = 10_000,
        bool guaranteed = false,
        int min = 1,
        int max = 1,
        IReadOnlyList<int>? requiredTags = null)
    {
        var row = new ContentRow(
            new ContentTypeId(EngineContentTypes.LootEntryTypeId),
            id,
            new ContentKey("entry_" + id),
            0,
            false,
            [
                ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, table),
                item == 0
                    ? ContentFieldValue.Absent(ContentFieldKind.KeyReference)
                    : ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, item),
                nestedTable == 0
                    ? ContentFieldValue.Absent(ContentFieldKind.KeyReference)
                    : ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, nestedTable),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, weight),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, chance),
                ContentFieldValue.OfNumber(ContentFieldKind.Bool, guaranteed ? 1 : 0),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, min),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, max),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, sort),
                ContentRowCodecBase.TagListValue(requiredTags ?? []),
            ]);

        builder.AddRow(row, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.LootEntryTypeKey, row));
    }
}
