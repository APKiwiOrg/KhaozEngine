using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// A version carrying loot on top of <see cref="CatalogRuntimeFixtures"/>'s tags and items: two tables, one
/// weighted and one guaranteed, entries covering all three ways spec 3.5 lets an entry name its draw, and one
/// orphan entry naming a table the version does not carry.
/// </summary>
internal static class CatalogLootFixtures
{
    /// <summary>The weighted table, whose three entries are an item, a nested table and a tag filter.</summary>
    public const int GoblinTable = 101;

    /// <summary>The guaranteed table the goblin table recurses into.</summary>
    public const int RareTable = 102;

    /// <summary>A table id no row carries, which the orphan entry names.</summary>
    public const int MissingTable = 103;

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

        AddTable(builder, registry, GoblinTable, "goblin", rollCount: 2, guaranteed: false);
        AddTable(builder, registry, RareTable, "rare", rollCount: 1, guaranteed: true);

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
            requiredTags: [CatalogRuntimeFixtures.SwordTag, CatalogRuntimeFixtures.MetalTag]);

        // The orphan: an entry naming a table this version does not carry, which is a finding rather than a
        // load failure.
        AddEntry(builder, registry, 205, MissingTable, item: 2, weight: 5, sort: 0);

        return ContentRuntime.FromSnapshot(builder.Build(), registry);
    }

    static void AddTable(
        ContentSnapshotBuilder builder,
        ContentTypeRegistry registry,
        int id,
        string key,
        int rollCount,
        bool guaranteed)
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
                ContentFieldValue.OfNumber(ContentFieldKind.Bool, guaranteed ? 1 : 0),
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
                ContentFieldValue.OfNumber(ContentFieldKind.Int, min),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, max),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, sort),
                ContentRowCodecBase.TagListValue(requiredTags ?? []),
            ]);

        builder.AddRow(row, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.LootEntryTypeKey, row));
    }
}
