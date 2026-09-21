using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// Adopting <c>item.category</c> on a catalog that was PUBLISHED before the field existed, which is the case
/// every consumer of this engine is in: a database full of item rows, a pack root full of chunk bytes, and a
/// shipped client holding both.
/// <para>
/// The old schema here is the current one minus its last field, taken from the live declaration rather than
/// transcribed, so the fixture cannot drift into testing a layout the engine never shipped.
/// </para>
/// <para>
/// <b>Version 2 deliberately authors NOTHING of type item.</b> That is what makes the item chunk unaffected,
/// so the publisher carries its version 1 bytes forward under the SAME hash, and the boot that follows is a
/// current build reading bytes an older build wrote. A test that edited an item would have re-encoded the
/// chunk and proved nothing.
/// </para>
/// </summary>
public sealed class ItemCategoryAdoptionTests
{
    static ContentTypeId ItemType => new(EngineContentTypes.ItemTypeId);

    static ContentTypeId CategoryType => new(EngineContentTypes.ItemCategoryTypeId);

    /// <summary>The item schema as it stood before <c>category</c> was appended: the current list minus one.</summary>
    static ContentFieldSchema OldItemSchema()
    {
        IReadOnlyList<ContentFieldEntry> current = ItemContentType.CreateSchema().Fields;
        Assert.Equal(ItemContentType.CategoryField, current[^1].Name);

        var older = new List<ContentFieldEntry>(current.Count - 1);
        for (int i = 0; i < current.Count - 1; i++)
        {
            older.Add(current[i]);
        }

        return new ContentFieldSchema(older);
    }

    /// <summary>A registry carrying <c>tag</c> and the OLD <c>item</c>, which is what the first publish ran on.</summary>
    static ContentTypeRegistry OldRegistry()
    {
        var registry = new ContentTypeRegistry();
        ContentFieldSchema tag = TagContentType.CreateSchema();
        registry.RegisterContentType(
            ContentRegistrationBand.Engine,
            EngineContentTypes.TagTypeId,
            EngineContentTypes.TagTypeKey,
            new TagContentType.Codec(new ContentTypeId(EngineContentTypes.TagTypeId), tag),
            validator: null,
            tag,
            ContentVisibility.Client,
            TagContentType.DefaultChunkSlots);

        ContentFieldSchema item = OldItemSchema();
        registry.RegisterContentType(
            ContentRegistrationBand.Engine,
            EngineContentTypes.ItemTypeId,
            EngineContentTypes.ItemTypeKey,
            new ItemContentType.Codec(ItemType, item),
            validator: null,
            item,
            ContentVisibility.Client,
            ItemContentType.DefaultChunkSlots);

        return registry;
    }

    static ContentTypeRegistry NewRegistry()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        return registry;
    }

    /// <summary>An item row's authored fields, every required one set, through the by-name edit path.</summary>
    static ContentFieldEdit[] ItemFields(int value) =>
    [
        new(ItemContentType.StackableField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 0)),
        new(ItemContentType.MaxStackField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 1)),
        new(ItemContentType.TradableField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1)),
        new(ItemContentType.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, value)),
        new(ItemContentType.DurabilityMaxField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 900)),
    ];

    static ContentFieldEdit[] CategoryFields(int sort) =>
    [
        new(ItemCategoryContentType.SortField, ContentFieldValue.OfNumber(ContentFieldKind.Int, sort)),
    ];

    static ContentPublishRequest Request(int baseVersion)
        => new("item-category-tests", "oid:tests", "adopt the category field", baseVersion, null, null);

    /// <summary>A publish whose refusal names every finding, because "3 finding(s)" is not a failure message.</summary>
    static async Task<int> PublishAsync(SqliteContentAuthoringStore store, int baseVersion)
    {
        try
        {
            return (await store.PublishAsync(Request(baseVersion))).VersionNumber;
        }
        catch (ContentAuthoringException refused)
        {
            var lines = new List<string>();
            foreach (ContentFinding finding in refused.Findings)
            {
                lines.Add(finding.Code + " type " + finding.Type.Value + " id " + finding.Id + ": " + finding.Message);
            }

            Assert.Fail(refused.Message + " | " + string.Join(" | ", lines));
            return 0;
        }
    }

    [Fact]
    public async Task AnItemCatalogPublishedBeforeTheFieldBootsUnderTheNewSchemaWithCategoryAbsent()
    {
        using var database = new TemporaryCatalogDatabase();

        ContentTypeRegistry old = OldRegistry();
        using (var original = new SqliteContentAuthoringStore(database.ConnectionString, old, database.Pack()))
        {
            await original.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await original.ApplyEditsAsync(
                [
                    ContentEdit.Add(ItemType, new ContentKey("bronze_sword"), ItemFields(250)),
                    ContentEdit.Add(ItemType, new ContentKey("iron_sword"), ItemFields(500)),
                ],
                "item-category-tests",
                "oid:tests",
                "publish under the old item schema");
            Assert.Equal(1, await PublishAsync(original, 0));
        }

        ContentTypeRegistry current = NewRegistry();
        var packs = database.Pack();
        using var adopted = new SqliteContentAuthoringStore(database.ConnectionString, current, packs);

        // ValidateOnly: the new item_category type arrives with no schema migration at all, because the
        // catalog tables hold a type row and a field row per NAME and never a per-type column list.
        await adopted.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        await adopted.ApplyEditsAsync(
            [ContentEdit.Add(CategoryType, new ContentKey("tool"), CategoryFields(10))],
            "item-category-tests",
            "oid:tests",
            "publish the first category row");
        Assert.Equal(2, await PublishAsync(adopted, 1));

        Assert.Equal(
            await ItemChunkHashAsync(adopted, packs, 1),
            await ItemChunkHashAsync(adopted, packs, 2));

        current.Freeze();
        var holder = new ContentRuntimeHolder();
        ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
        {
            Registry = current,
            Store = packs,
            Pointers = packs,
            Holder = holder,
            ConfiguredVersion = 2,
            ServerBuild = int.MaxValue,
            StoreName = "the item category adoption test store",
        });

        Assert.True(boot.Success, string.Join(" | ", boot.StandardError));
        Assert.True(boot.Runtime!.TryGetRow(ItemType, 1, out ContentRow? bronze));
        Assert.Equal("bronze_sword", bronze.Key.ToString());
        Assert.Equal(ItemContentType.CreateSchema().Fields.Count, bronze.Fields.Count);
        Assert.True(bronze.Fields[^1].IsAbsent);
        Assert.Equal(250L, bronze.Fields[6].Number);

        // The typed hot-field view reads the same short bodies, which is the path a server takes per merge
        // test and per price read rather than the generic row walk above.
        Assert.True(boot.Runtime.TryGetItem(2, out ItemRow iron));
        Assert.Equal("iron_sword", iron.Key.ToString());
        Assert.Equal(500, iron.Value);
        Assert.Equal(1, iron.MaxStack);
        Assert.Equal(900, iron.DurabilityMax);

        Assert.True(boot.Runtime.TryGetRow(CategoryType, 1, out ContentRow? category));
        Assert.Equal("tool", category.Key.ToString());
        Assert.Equal(10L, category.Fields[1].Number);
    }

    /// <summary>
    /// An item authored AFTER the adoption carries the category, and the two generations of bytes sit in one
    /// catalog without either disturbing the other.
    /// </summary>
    [Fact]
    public async Task AnItemAuthoredAfterTheAdoptionCarriesItsCategoryBesideTheOlderRows()
    {
        using var database = new TemporaryCatalogDatabase();

        ContentTypeRegistry old = OldRegistry();
        using (var original = new SqliteContentAuthoringStore(database.ConnectionString, old, database.Pack()))
        {
            await original.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await original.ApplyEditsAsync(
                [ContentEdit.Add(ItemType, new ContentKey("bronze_sword"), ItemFields(250))],
                "item-category-tests",
                "oid:tests",
                "publish under the old item schema");
            Assert.Equal(1, await PublishAsync(original, 0));
        }

        ContentTypeRegistry current = NewRegistry();
        var packs = database.Pack();
        using var adopted = new SqliteContentAuthoringStore(database.ConnectionString, current, packs);
        await adopted.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        await adopted.ApplyEditsAsync(
            [ContentEdit.Add(CategoryType, new ContentKey("tool"), CategoryFields(10))],
            "item-category-tests",
            "oid:tests",
            "publish the first category row");
        Assert.Equal(2, await PublishAsync(adopted, 1));

        var withCategory = new List<ContentFieldEdit>(ItemFields(75))
        {
            new(ItemContentType.CategoryField, ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, 1)),
        };
        await adopted.ApplyEditsAsync(
            [ContentEdit.Add(ItemType, new ContentKey("pickaxe"), withCategory)],
            "item-category-tests",
            "oid:tests",
            "author an item in a category");
        Assert.Equal(3, await PublishAsync(adopted, 2));

        current.Freeze();
        var holder = new ContentRuntimeHolder();
        ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
        {
            Registry = current,
            Store = packs,
            Pointers = packs,
            Holder = holder,
            ConfiguredVersion = 3,
            ServerBuild = int.MaxValue,
            StoreName = "the item category adoption test store",
        });

        Assert.True(boot.Success, string.Join(" | ", boot.StandardError));
        Assert.True(boot.Runtime!.TryGetRow(ItemType, 1, out ContentRow? bronze));
        Assert.True(bronze.Fields[^1].IsAbsent);
        Assert.True(boot.Runtime.TryGetRow(ItemType, 2, out ContentRow? pickaxe));
        Assert.Equal("pickaxe", pickaxe.Key.ToString());
        Assert.Equal(1L, pickaxe.Fields[^1].Number);
    }

    /// <summary>The client manifest's hash for item chunk 0 at one version, which is what a carry forward keeps.</summary>
    static async Task<string> ItemChunkHashAsync(
        SqliteContentAuthoringStore store,
        IPackStore packs,
        int versionNumber)
    {
        ContentVersionRecord record = await store.GetVersionAsync(versionNumber)
            ?? throw new InvalidOperationException("the store holds no version " + versionNumber);
        ReadOnlyMemory<byte>? file = await packs.GetAsync(record.ClientManifestHash);
        Assert.True(file.HasValue);
        Assert.True(
            ContentManifestCodec.TryDecode(
                file.Value.Span, ContentManifestSide.Client, out ContentManifest? manifest, out string? reason),
            reason);

        foreach (ManifestTypeEntry type in manifest.Types)
        {
            if (type.TypeId == EngineContentTypes.ItemTypeId)
            {
                return Assert.Single(type.Chunks).Hash;
            }
        }

        Assert.Fail("the manifest names no item chunk");
        return string.Empty;
    }
}
