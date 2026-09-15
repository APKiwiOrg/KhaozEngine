using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The loaded version the runtime and derived-index tests read: a handful of tags, a sparse item type whose
/// highest id is well under its chunk slot count, a retired row, and one remap rule, all with their encoded
/// bodies so the tables index real bytes rather than rows alone.
/// </summary>
internal static class CatalogRuntimeFixtures
{
    /// <summary>The highest item id the fixture carries, which is what sizes the item table.</summary>
    public const int HighestItemId = 35;

    /// <summary>How many item rows the fixture carries.</summary>
    public const int ItemCount = 5;

    /// <summary>The tag ids the fixture's items carry, which are the tag rows it also writes.</summary>
    public const int SwordTag = 7;

    /// <summary>The second tag, carried by a subset of the items.</summary>
    public const int MetalTag = 9;

    /// <summary>The version's identity, so a test can assert what the runtime carries.</summary>
    public static ContentVersionIdentity Identity => new(7, new string('f', 64));

    /// <summary>The snapshot the runtime is built from, and the registry it loaded against.</summary>
    public static ContentSnapshot Snapshot(out ContentTypeRegistry registry)
    {
        registry = CatalogSnapshotFixtures.Registry();
        var builder = new ContentSnapshotBuilder(registry)
            .WithIdentity(Identity.Number, Identity.ManifestHash)
            .WithRules(
            [
                new RemapRule(1, 7, CatalogSnapshotFixtures.ItemType, RemapRuleKind.ReplacedBy, 35, 2, default),
            ]);

        AddTag(builder, registry, SwordTag, "sword");
        AddTag(builder, registry, MetalTag, "metal");

        // Ids 1, 2, 8, 20 and 35, so nothing about the table can be derived from a dense id run.
        AddItem(builder, registry, 1, "item_1", [SwordTag]);
        AddItem(builder, registry, 2, "item_2", [SwordTag, MetalTag]);
        AddItem(builder, registry, 8, "item_8", [MetalTag]);
        AddItem(builder, registry, 20, "item_20", []);
        AddItem(builder, registry, HighestItemId, "item_35", [SwordTag], isRetired: true);

        return builder.Build();
    }

    /// <summary>The loaded runtime over <see cref="Snapshot"/>.</summary>
    public static ContentRuntime Runtime(out ContentTypeRegistry registry)
    {
        ContentSnapshot snapshot = Snapshot(out registry);
        return ContentRuntime.FromSnapshot(snapshot, registry);
    }

    /// <summary>An item row with its encoded body, which is what a chunk carries.</summary>
    public static void AddItem(
        ContentSnapshotBuilder builder,
        ContentTypeRegistry registry,
        int id,
        string key,
        IReadOnlyList<int> tags,
        bool isRetired = false)
    {
        ContentRow row = ItemRowWithTags(id, key, tags, isRetired);
        builder.AddRow(row, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, row));
    }

    /// <summary>A tag row with its encoded body.</summary>
    public static void AddTag(ContentSnapshotBuilder builder, ContentTypeRegistry registry, int id, string key)
    {
        ContentRow row = CatalogSnapshotFixtures.TagRow(id, key);
        builder.AddRow(row, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.TagTypeKey, row));
    }

    /// <summary>
    /// The shared item row with its tag list replaced, because the tag index is derived from that field and
    /// a fixture where every row carries the same two tags could not tell a sorted list from a shuffled one.
    /// </summary>
    public static ContentRow ItemRowWithTags(int id, string key, IReadOnlyList<int> tags, bool isRetired = false)
    {
        ContentRow row = CatalogSnapshotFixtures.ItemRow(id, key, true, 64, 900, 3, isRetired);
        var fields = new List<ContentFieldValue>(row.Fields);
        fields[2] = ContentRowCodecBase.TagListValue(tags);
        return new ContentRow(row.Type, row.Id, row.Key, row.ParentId, row.IsRetired, fields);
    }
}
