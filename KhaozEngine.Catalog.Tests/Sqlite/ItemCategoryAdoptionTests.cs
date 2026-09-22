using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
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
/// <b>The pack rebuild fact is the one that matters most.</b> A consumer rebuilds the pack of the version a
/// boot is about to LOAD when the pack root has lost it, so a refusal there is a refused boot. The canonical
/// short encode of <c>ContentRowTailRule</c> is what keeps it working: a pre-adoption item row sets no
/// category, so it re-encodes to the bytes the sixteen field release wrote and the rebuilt manifest digests to
/// the hash the version record already holds.
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
        Assert.Equal(ItemContentType.BaselineFieldCount, current.Count - 1);

        var older = new List<ContentFieldEntry>(current.Count - 1);
        for (int i = 0; i < current.Count - 1; i++)
        {
            older.Add(current[i]);
        }

        return new ContentFieldSchema(older);
    }

    /// <summary>
    /// The engine registry as the release BEFORE this one declared it: the same six types under the same ids,
    /// keys, slot counts, row caps and visibilities, with <c>item</c> carrying the sixteen field schema.
    /// <para>
    /// It is built the long way rather than by editing a copy of <see cref="EngineContentTypes.Register"/>,
    /// because the whole point of the fixture is to be a DIFFERENT declaration than the one this build ships.
    /// </para>
    /// </summary>
    static ContentTypeRegistry OldRegistry() => SixTypes(OldItemSchema());

    /// <summary>
    /// The same six types with the SEVENTEEN field item schema, which is the registry shape that isolates a
    /// row's bytes from everything else a manifest hash covers.
    /// </summary>
    static ContentTypeRegistry WiderItemRegistry() => SixTypes(ItemContentType.CreateSchema());

    static ContentTypeRegistry SixTypes(ContentFieldSchema item)
    {
        var registry = new ContentTypeRegistry();
        Add(registry, EngineContentTypes.TagTypeId, EngineContentTypes.TagTypeKey, TagContentType.CreateSchema(),
            ContentVisibility.Client, TagContentType.DefaultChunkSlots, ContentPackFormat.DefaultMaxRowBytes,
            static (type, schema) => new TagContentType.Codec(type, schema));
        Add(registry, EngineContentTypes.ItemTypeId, EngineContentTypes.ItemTypeKey, item,
            ContentVisibility.Client, ItemContentType.DefaultChunkSlots, ContentPackFormat.DefaultMaxRowBytes,
            static (type, schema) => new ItemContentType.Codec(type, schema));
        Add(registry, EngineContentTypes.StatTypeId, EngineContentTypes.StatTypeKey, StatContentType.CreateSchema(),
            ContentVisibility.Client, StatContentType.DefaultChunkSlots, ContentPackFormat.DefaultMaxRowBytes,
            static (type, schema) => new StatContentType.Codec(type, schema));
        Add(registry, EngineContentTypes.LootTableTypeId, EngineContentTypes.LootTableTypeKey,
            LootTableContentType.CreateSchema(), ContentVisibility.ServerOnly,
            LootTableContentType.DefaultChunkSlots, ContentPackFormat.DefaultMaxRowBytes,
            static (type, schema) => new LootTableContentType.Codec(type, schema));
        Add(registry, EngineContentTypes.LootEntryTypeId, EngineContentTypes.LootEntryTypeKey,
            LootEntryContentType.CreateSchema(), ContentVisibility.ServerOnly,
            LootEntryContentType.DefaultChunkSlots, LootEntryContentType.MaxRowBytes,
            static (type, schema) => new LootEntryContentType.Codec(type, schema));
        Add(registry, EngineContentTypes.BaseSocketTypeId, EngineContentTypes.BaseSocketTypeKey,
            BaseSocketContentType.CreateSchema(), ContentVisibility.Client,
            BaseSocketContentType.DefaultChunkSlots, BaseSocketContentType.MaxRowBytes,
            static (type, schema) => new BaseSocketContentType.Codec(type, schema));
        return registry;
    }

    static void Add(
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

    /// <summary>
    /// A version published under the SIXTEEN field item schema rebuilds under the seventeen field one,
    /// reproducing both recorded manifest digests and therefore every chunk hash inside them.
    /// <para>
    /// This is the regression guard for a row's BYTES. <c>ContentPackRebuild</c> verifies before it writes, by
    /// digesting the manifests it rebuilt and comparing them against the ones the version record holds, so it
    /// is exactly the operation a schema change that moved a row's bytes would break. It passes only because
    /// the encoder omits trailing appended fields no row sets: with a long encode it refuses with
    /// <c>server-manifest-mismatch</c>.
    /// </para>
    /// <para>
    /// The registry carries the same TYPE SET the publish ran on, deliberately, which is what makes this a
    /// fact about the rows. A manifest hash also covers the registration set, so a registry that gained a type
    /// moves it whatever the rows do, and the fact below is where that is pinned instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AVersionPublishedBeforeTheFieldRebuildsUnderTheWiderItemSchema()
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

        ContentTypeRegistry wider = WiderItemRegistry();
        using var adopted = new SqliteContentAuthoringStore(database.ConnectionString, wider, database.Pack());
        await adopted.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        using var recovered = new TemporaryCatalogDatabase();
        var target = new FileSystemPackStore(recovered.PackRoot);

        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(adopted, wider, 1, target);

        Assert.True(rebuilt.Rebuilt, rebuilt.RefusalDetail ?? rebuilt.RefusalReason ?? "no reason");
        Assert.Null(rebuilt.RefusalReason);
        Assert.Equal(1, rebuilt.VersionNumber);
        Assert.True(rebuilt.ObjectsWritten > 0);

        // The rebuilt root carries the SAME addresses the original publish wrote, which is the digest
        // comparison inside the rebuild restated as the thing a boot against a recovered root needs.
        ContentVersionRecord record = await adopted.GetVersionAsync(1)
            ?? throw new InvalidOperationException("the store holds no version 1");
        Assert.True(await target.ExistsAsync(record.ServerManifestHash));
        Assert.True(await target.ExistsAsync(record.ClientManifestHash));

        wider.Freeze();
        var holder = new ContentRuntimeHolder();
        ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
        {
            Registry = wider,
            Store = target,
            Pointers = target,
            Holder = holder,
            ConfiguredVersion = 1,
            ServerBuild = int.MaxValue,
            StoreName = "the recovered pack root",
        });

        Assert.True(boot.Success, string.Join(" | ", boot.StandardError));
        Assert.True(boot.Runtime!.TryGetRow(ItemType, 1, out ContentRow? bronze));
        Assert.True(bronze.Fields[^1].IsAbsent);
    }

    /// <summary>
    /// The OTHER half of adopting a release that adds a TYPE, which no row rule can reach and which a consumer
    /// has to plan around. A manifest hash covers the REGISTRATION SET, not just the chunks
    /// (<c>ContentManifestBuilder</c>), and a boot refuses a version whose manifest does not name every
    /// registered type (<c>ContentBoot</c> step 6). So a version published before <c>item_category</c> existed
    /// is refused by a build that registers it, at the boot and at the rebuild both, FOR THE TYPE LIST and not
    /// for its rows.
    /// <para>
    /// It is pinned here because it is the fact that decides what a consumer does on adoption: republish once
    /// under the new registry before the new build serves. The fact above proves the rows themselves are
    /// unchanged, so that republish authors nothing and every item chunk carries forward at its old hash.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AVersionPublishedBeforeTheNewTypeIsRefusedForItsTypeListRatherThanItsRows()
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

        using var recovered = new TemporaryCatalogDatabase();
        ContentPackRebuildResult rebuilt = await ContentPackRebuild.RunAsync(
            adopted, current, 1, new FileSystemPackStore(recovered.PackRoot));

        Assert.False(rebuilt.Rebuilt);
        Assert.Equal(ContentPackRebuild.RefusedServerManifest, rebuilt.RefusalReason);

        current.Freeze();
        ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
        {
            Registry = current,
            Store = packs,
            Pointers = packs,
            Holder = new ContentRuntimeHolder(),
            ConfiguredVersion = 1,
            ServerBuild = int.MaxValue,
            StoreName = "the pre-adoption version",
        });

        Assert.False(boot.Success);
        Assert.Equal(ContentBootRefusal.TypeAbsentFromVersion, boot.Refusal);
        Assert.Contains(
            boot.StandardError,
            line => line.Contains(EngineContentTypes.ItemCategoryTypeKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// The encoder is the decoder's mirror: an item with no category goes out as the SIXTEEN field bytes the
    /// old registry wrote, and one with a category is that plus its varint.
    /// </summary>
    [Fact]
    public void AnItemWithNoCategoryEncodesToTheBytesTheOldSchemaWrote()
    {
        ContentTypeRegistration older = Lookup(OldRegistry(), EngineContentTypes.ItemTypeKey);
        ContentTypeRegistration newer = Lookup(NewRegistry(), EngineContentTypes.ItemTypeKey);

        ContentFieldValue[] baseline =
        [
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            ContentRowCodecBase.TagListValue([4, 9]),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 0),
            ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1),
            ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 250),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.Int),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            ContentFieldValue.OfNumber(ContentFieldKind.Int, 900),
            ContentFieldValue.OfNumber(ContentFieldKind.Int, 2),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference),
        ];

        Assert.Equal(ItemContentType.BaselineFieldCount, baseline.Length);

        byte[] under16 = Encode(older, Row(older, baseline));
        byte[] under17 = Encode(
            newer, Row(newer, [.. baseline, ContentFieldValue.Absent(ContentFieldKind.KeyReference)]));
        Assert.Equal(under16, under17);

        byte[] withCategory = Encode(
            newer, Row(newer, [.. baseline, ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, 3)]));
        Assert.Equal(under16.Length + 1, withCategory.Length);
        Assert.Equal(under16, withCategory[..^1]);
        Assert.Equal(3, withCategory[^1]);

        // And it round trips: the long form comes back with the category set, the short one with it absent.
        Assert.True(newer.Codec.TryDecode(withCategory, out ContentRow? loaded, out string? reason), reason);
        Assert.Equal(3L, loaded.Fields[^1].Number);
        Assert.True(newer.Codec.TryDecode(under16, out ContentRow? shorter, out reason), reason);
        Assert.True(shorter.Fields[^1].IsAbsent);
    }

    /// <summary>
    /// A body that ends INSIDE the baseline fields is refused with the same token the sixteen field build gave
    /// it, which is the half of the rule that keeps a corrupt row from reading as an older one.
    /// </summary>
    [Fact]
    public void ABodyEndingInsideTheBaselineIsRefusedExactlyAsTheOlderBuildRefusedIt()
    {
        ContentTypeRegistration older = Lookup(OldRegistry(), EngineContentTypes.ItemTypeKey);
        ContentTypeRegistration newer = Lookup(NewRegistry(), EngineContentTypes.ItemTypeKey);

        var fields = new List<ContentFieldValue>();
        foreach (ContentFieldEntry entry in older.Schema.Fields)
        {
            fields.Add(entry.Required && !entry.IsDerivedMarker
                ? ContentFieldValue.OfNumber(entry.Kind, 1)
                : ContentFieldValue.Absent(entry.Kind));
        }

        byte[] whole = Encode(older, Row(older, fields));

        for (int length = 0; length < whole.Length; length++)
        {
            bool before = older.Codec.TryDecode(whole.AsSpan(0, length), out _, out string? oldReason);
            bool after = newer.Codec.TryDecode(whole.AsSpan(0, length), out _, out string? newReason);

            Assert.False(before, length.ToString(CultureInfo.InvariantCulture));
            Assert.False(after, length.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(oldReason, newReason);
        }

        // The whole body is the baseline boundary itself, which is the one length that decodes.
        Assert.True(newer.Codec.TryDecode(whole, out ContentRow? row, out string? reason), reason);
        Assert.True(row.Fields[^1].IsAbsent);
    }

    static ContentTypeRegistration Lookup(ContentTypeRegistry registry, string typeKey)
    {
        Assert.True(registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        return registration;
    }

    static ContentRow Row(ContentTypeRegistration registration, IReadOnlyList<ContentFieldValue> fields)
        => new(registration.Type, 1, new ContentKey("bronze_sword"), 0, false, fields);

    static byte[] Encode(ContentTypeRegistration registration, ContentRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        registration.Codec.Encode(row, buffer);
        return buffer.WrittenSpan.ToArray();
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

    /// <summary>
    /// The OTHER direction, and the fact that decides what the format generation is actually worth: a build
    /// that does NOT register <c>item_category</c> cannot read a version published by a registry that does,
    /// because the manifest names a type it has no codec for (<c>ContentBoot</c> step 6,
    /// <c>TypeUnregistered</c>).
    /// <para>
    /// That refusal is independent of <c>ContentPackFormat.Generation</c>. This test can only vary the
    /// REGISTRY, since the generation constant is this build's, but the constant is checked at step 4 and this
    /// at step 6, so the generation bump only decides WHICH of the two refusals an older reader hits first. It
    /// is refused either way, and for every version published under the new registry rather than only those
    /// that carry a category.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AVersionPublishedWithTheNewTypeIsRefusedByABuildThatDoesNotRegisterIt()
    {
        using var database = new TemporaryCatalogDatabase();

        ContentTypeRegistry current = NewRegistry();
        var packs = database.Pack();
        using (var publisher = new SqliteContentAuthoringStore(database.ConnectionString, current, packs))
        {
            await publisher.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await publisher.ApplyEditsAsync(
                [ContentEdit.Add(ItemType, new ContentKey("bronze_sword"), ItemFields(250))],
                "item-category-tests",
                "oid:tests",
                "publish under the new registry, authoring no category at all");
            Assert.Equal(1, await PublishAsync(publisher, 0));
        }

        // Not one category row exists and not one item sets the field, so the CHUNKS are byte identical to
        // what the older registry would have written. The manifest is not, and the manifest is what refuses.
        ContentTypeRegistry old = OldRegistry();
        old.Freeze();
        ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
        {
            Registry = old,
            Store = packs,
            Pointers = packs,
            Holder = new ContentRuntimeHolder(),
            ConfiguredVersion = 1,
            ServerBuild = int.MaxValue,
            StoreName = "a build from before the new type",
        });

        Assert.False(boot.Success);
        Assert.Equal(ContentBootRefusal.TypeUnregistered, boot.Refusal);
        Assert.Contains(
            boot.StandardError,
            line => line.Contains(
                EngineContentTypes.ItemCategoryTypeId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal));
    }
}
