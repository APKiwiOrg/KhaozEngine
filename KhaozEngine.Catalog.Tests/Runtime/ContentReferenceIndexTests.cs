using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The reverse-reference index over every <see cref="ContentFieldKind.KeyReference"/> field. It is built
/// once with the runtime, returns stable sorted source ids and retains only immutable arrays for later reads.
/// </summary>
[Collection("AllocSensitive")]
public sealed class ContentReferenceIndexTests
{
    static readonly ContentTypeId Item = new(EngineContentTypes.ItemTypeId);
    static readonly ContentTypeId Food = new(1024);
    static readonly ContentTypeId Tool = new(1025);

    [Fact]
    public void Multiple_reference_fields_and_types_produce_distinct_sorted_source_ids()
    {
        ContentRuntime runtime = Runtime(
            1,
            FoodRow(30, false, 5, 5),
            FoodRow(10, false, 5, 9),
            FoodRow(20, true, 5, null),
            FoodRow(50, false, 99, null),
            ToolRow(40, 5));

        ContentReferenceIndex references = runtime.Indexes.References;

        Assert.Equal([10, 20, 30], references.Ids(Item, 5, Food).ToArray());
        Assert.Equal([40], references.Ids(Item, 5, Tool).ToArray());

        // Both sides keep retirement as the READER's filter. Item 9 is retired and food 20 is retired, but
        // their relationships remain answerable for stored data and admin reads.
        Assert.Equal([10], references.Ids(Item, 9, Food).ToArray());
        Assert.True(runtime.IsRetired(Item, 9));
        Assert.True(runtime.IsRetired(Food, 20));

        // Row 50 points at no target row. The validator reports that defect, while the derived lookup has no
        // phantom target bucket for it.
        Assert.Empty(references.Ids(Item, 99, Food).ToArray());
        Assert.Empty(references.Ids(Item, 5, new ContentTypeId(2048)).ToArray());
    }

    [Fact]
    public void A_new_content_version_rebuilds_the_index_and_the_old_runtime_stays_frozen()
    {
        ContentRuntime first = Runtime(1, FoodRow(7, false, 5, null));
        ContentRuntime second = Runtime(
            2,
            FoodRow(7, false, 9, null),
            FoodRow(3, false, 5, null));

        Assert.NotSame(first.Indexes.References, second.Indexes.References);
        Assert.Equal([7], first.Indexes.References.Ids(Item, 5, Food).ToArray());
        Assert.Empty(first.Indexes.References.Ids(Item, 9, Food).ToArray());

        Assert.Equal([3], second.Indexes.References.Ids(Item, 5, Food).ToArray());
        Assert.Equal([7], second.Indexes.References.Ids(Item, 9, Food).ToArray());

        // Building version 2 changed no array reachable from version 1.
        Assert.Equal([7], first.Indexes.References.Ids(Item, 5, Food).ToArray());
    }

    [Fact]
    public void Repeated_lookup_reads_only_the_built_arrays_and_allocates_nothing()
    {
        ContentRuntime runtime = Runtime(
            1,
            FoodRow(30, false, 5, 5),
            FoodRow(10, false, 5, null),
            FoodRow(20, false, 5, null));
        ContentReferenceIndex references = runtime.Indexes.References;

        Assert.Equal([10, 20, 30], references.Ids(Item, 5, Food).ToArray());

        FieldInfo[] fields = typeof(ContentReferenceIndex).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotEmpty(fields);
        Assert.All(fields, field =>
        {
            Assert.True(field.IsInitOnly, $"{field.Name} is writable after construction");
            Assert.True(field.FieldType.IsArray, $"{field.Name} retains {field.FieldType} instead of a built array");
        });

        long before = GC.GetAllocatedBytesForCurrentThread();
        int checksum = 0;
        for (int i = 0; i < 1_000; i++)
        {
            ReadOnlySpan<int> ids = references.Ids(Item, 5, Food);
            checksum += ids[0] + ids[1] + ids[2];
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(60_000, checksum);
        Assert.Equal(0, allocated);
    }

    static ContentRuntime Runtime(int version, params ContentRow[] sourceRows)
    {
        ContentTypeRegistry registry = Registry();
        var builder = new ContentSnapshotBuilder(registry)
            .WithIdentity(version, new string((char)('a' + version), 64));

        CatalogRuntimeFixtures.AddItem(builder, registry, 5, "live_item", []);
        CatalogRuntimeFixtures.AddItem(builder, registry, 9, "retired_item", [], isRetired: true);

        for (int i = 0; i < sourceRows.Length; i++)
        {
            ContentRow row = sourceRows[i];
            string typeKey = row.Type == Food ? "food" : "tool_tier";
            builder.AddRow(row, Body(registry, typeKey, row));
        }

        return ContentRuntime.FromSnapshot(builder.Build(), registry);
    }

    static ContentTypeRegistry Registry()
    {
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        Register(registry, Food, "food", FoodSchema());
        Register(registry, Tool, "tool_tier", ToolSchema());
        return registry;
    }

    static void Register(
        ContentTypeRegistry registry,
        ContentTypeId type,
        string key,
        ContentFieldSchema schema)
        => registry.RegisterContentType(
            ContentRegistrationBand.Game,
            type.Value,
            key,
            new TestCodec(type, schema),
            null,
            schema,
            ContentVisibility.Client,
            ContentTypeRegistry.MinChunkSlots);

    static ContentFieldSchema FoodSchema()
        => new(
        [
            new ContentFieldEntry(
                "item", ContentFieldKind.KeyReference, EngineContentTypes.ItemTypeKey,
                ContentVisibility.Client, Required: true),
            new ContentFieldEntry(
                "replacement", ContentFieldKind.KeyReference, EngineContentTypes.ItemTypeKey,
                ContentVisibility.Client, Required: false),
            new ContentFieldEntry(
                "heals", ContentFieldKind.Int, null, ContentVisibility.Client, Required: true),
        ]);

    static ContentFieldSchema ToolSchema()
        => new(
        [
            new ContentFieldEntry(
                "item", ContentFieldKind.KeyReference, EngineContentTypes.ItemTypeKey,
                ContentVisibility.Client, Required: true),
        ]);

    static ContentRow FoodRow(int id, bool retired, int item, int? replacement)
        => new(
            Food,
            id,
            new ContentKey("food_" + id),
            0,
            retired,
            [
                Reference(item),
                replacement is int other
                    ? Reference(other)
                    : ContentFieldValue.Absent(ContentFieldKind.KeyReference),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 4),
            ]);

    static ContentRow ToolRow(int id, int item)
        => new(
            Tool,
            id,
            new ContentKey("tool_" + id),
            0,
            false,
            [Reference(item)]);

    static ContentFieldValue Reference(int id)
        => ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, id);

    static byte[] Body(ContentTypeRegistry registry, string typeKey, ContentRow row)
    {
        var writer = new ArrayBufferWriter<byte>();
        CatalogSnapshotFixtures.Registration(registry, typeKey).Codec.Encode(row, writer);
        return writer.WrittenSpan.ToArray();
    }

    sealed class TestCodec(ContentTypeId type, ContentFieldSchema schema) : ContentRowCodecBase(type, schema);
}
