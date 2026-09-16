using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// A version carrying loot on top of <see cref="CatalogRuntimeFixtures"/>'s tags and items: two rollable
/// tables, one whose three entries all compete in the weighted draw and one whose only entry is guaranteed,
/// entries covering all three ways spec 3.5 lets an entry name its draw, and the four shapes the index has to
/// drop rather than fail the load on: an orphan entry naming a table the version does not carry, a RETIRED
/// table, a RETIRED entry, and a second entry row under an id another row already took.
/// </summary>
internal static class CatalogLootFixtures
{
    /// <summary>The weighted table, whose three entries are an item, a nested table and a tag filter.</summary>
    public const int GoblinTable = 101;

    /// <summary>The table the goblin table recurses into, whose one entry is guaranteed.</summary>
    public const int RareTable = 103;

    /// <summary>
    /// A table id no row carries, sitting BETWEEN the two real tables on purpose. An absent id ABOVE the
    /// highest real one is caught by any bound on the array length, so a fixture that put it there could not
    /// tell a presence test from an arithmetic one.
    /// </summary>
    public const int MissingTable = 102;

    /// <summary>
    /// A table whose row is RETIRED. Its entries are authored and live, so anything the index does for it is
    /// the retirement's doing rather than an empty table's.
    /// </summary>
    public const int RetiredTable = 104;

    /// <summary>The default weights of the goblin table's three entries, in sort order.</summary>
    public static IReadOnlyList<int> DefaultWeights => [10, 30, 60];

    /// <summary>The loaded runtime over the loot fixture.</summary>
    public static ContentRuntime Runtime(out ContentTypeRegistry registry)
        => RuntimeWithWeights(DefaultWeights, out registry);

    /// <summary>The same version with the goblin table's three weights replaced, in sort order.</summary>
    public static ContentRuntime RuntimeWithWeights(IReadOnlyList<int> weights, out ContentTypeRegistry registry)
    {
        registry = CatalogSnapshotFixtures.Registry();
        var builder = new ContentSnapshotBuilder(registry).WithIdentity(7, new string('f', 64));

        CatalogRuntimeFixtures.AddTag(builder, registry, CatalogRuntimeFixtures.SwordTag, "sword");
        CatalogRuntimeFixtures.AddTag(builder, registry, CatalogRuntimeFixtures.MetalTag, "metal");
        CatalogRuntimeFixtures.AddItem(builder, registry, 1, "item_1", [CatalogRuntimeFixtures.SwordTag]);
        CatalogRuntimeFixtures.AddItem(
            builder, registry, 2, "item_2", [CatalogRuntimeFixtures.SwordTag, CatalogRuntimeFixtures.MetalTag]);
        CatalogRuntimeFixtures.AddItem(builder, registry, 8, "item_8", [CatalogRuntimeFixtures.MetalTag]);
        CatalogRuntimeFixtures.AddItem(
            builder, registry, 35, "item_35", [CatalogRuntimeFixtures.SwordTag], isRetired: true);

        AddTable(builder, registry, GoblinTable, "goblin", rollCount: 2);
        AddTable(builder, registry, RareTable, "rare", rollCount: 1);
        AddTable(builder, registry, RetiredTable, "retired", rollCount: 2, isRetired: true);

        // Entry 201 sorts AFTER 202, so the index's order is the authored sort rather than the id order.
        AddEntry(builder, registry, 201, GoblinTable, nestedTable: RareTable, weight: weights[1], sort: 1);
        AddEntry(builder, registry, 202, GoblinTable, item: 2, weight: weights[0], sort: 0, chance: 5_000, min: 1, max: 3);
        AddEntry(
            builder,
            registry,
            203,
            GoblinTable,
            weight: weights[2],
            sort: 2,
            requiredTags: [CatalogRuntimeFixtures.SwordTag]);
        AddEntry(
            builder,
            registry,
            204,
            RareTable,
            weight: 1,
            sort: 0,
            guaranteed: true,
            requiredTags: [CatalogRuntimeFixtures.SwordTag, CatalogRuntimeFixtures.MetalTag]);

        // The orphan: an entry naming a table this version does not carry, which is a finding rather than a
        // load failure.
        AddEntry(builder, registry, 205, MissingTable, item: 2, weight: 5, sort: 0);

        // The retired table's own entries, both live and both heavy, so a table that indexed them at all
        // would be visible in TableCount, in its entry list and in a roll.
        AddEntry(builder, registry, 206, RetiredTable, item: 8, weight: 500, sort: 0);
        AddEntry(builder, registry, 207, RetiredTable, item: 2, weight: 1, sort: 1, guaranteed: true);

        // A RETIRED entry of a live table, naming the retired item 35: the normal shape of a retirement,
        // which every validator check skips for exactly that reason.
        AddEntry(builder, registry, 208, GoblinTable, item: 35, weight: 999, sort: 3, isRetired: true);

        // A second row under entry 202's id, which is KEC0036 on the publish side. Its weight is counted
        // once or the pool is wider than anything authored.
        AddEntry(builder, registry, 202, GoblinTable, item: 8, weight: 1_000, sort: 4);

        return ContentRuntime.FromSnapshot(builder.Build(), registry);
    }

    static void AddTable(
        ContentSnapshotBuilder builder,
        ContentTypeRegistry registry,
        int id,
        string key,
        int rollCount,
        bool isRetired = false)
    {
        var row = new ContentRow(
            new ContentTypeId(EngineContentTypes.LootTableTypeId),
            id,
            new ContentKey(key),
            0,
            isRetired,
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
        int weight,
        int sort,
        int item = 0,
        int nestedTable = 0,
        int chance = 10_000,
        bool guaranteed = false,
        int min = 1,
        int max = 1,
        bool isRetired = false,
        IReadOnlyList<int>? requiredTags = null)
    {
        var row = new ContentRow(
            new ContentTypeId(EngineContentTypes.LootEntryTypeId),
            id,
            new ContentKey("entry_" + id),
            0,
            isRetired,
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
