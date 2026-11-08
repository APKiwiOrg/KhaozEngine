using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The bundle of spec 10.9: the whole catalog as one document, which is the SEEDING format and the LOSSLESS
/// EXPORT format and there is only one of them.
/// <para>
/// Two rules carry the section. Export at version N then import into an empty store reproduces the same
/// rows, the same keys and the SAME IDS, because the export carries them and the import keeps them. And an
/// import works into an EMPTY store only, which is the answer to a whole class of seeding defect where a
/// seed that runs repeatedly against live data reverts an operator's value on the next deploy.
/// </para>
/// <para>
/// <b>A lossless export is not a backup, and the difference is the version LINE.</b> An import republishes at
/// version 1, so the history starts there and every rule's introduced-in is the new line's.
/// </para>
/// </summary>
public class BundleTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    /// <summary>A store, its registry and its pack root, which every bundle test needs three of.</summary>
    sealed class Catalog : IDisposable
    {
        public Catalog()
        {
            Root = new TemporaryRoot();
            Registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
            Pack = new FileSystemPackStore(Root.Path);
            Store = PublishFixtures.Store(Registry, Pack);
        }

        public TemporaryRoot Root { get; }

        public ContentTypeRegistry Registry { get; }

        public FileSystemPackStore Pack { get; }

        public InMemoryContentAuthoringStore Store { get; }

        public void Dispose() => Root.Dispose();
    }

    static async Task<ContentBundle> SeedAsync(Catalog catalog)
    {
        // A family, so the export carries blocks, plus an ordinary row, a retired row and a fork, so it
        // carries both rule kinds phase 1 can produce.
        ContentFamily family = await catalog.Store.CreateFamilyAsync(
            Thing, "swords", 16, PublishFixtures.Actor, "oid:tests");

        await PublishFixtures.ApplyAsync(
            catalog.Store,
            ContentEdit.Add(Thing, new ContentKey("plain"), PublishFixtures.Fields(1)),
            ContentEdit.Add(Thing, new ContentKey("sword"), PublishFixtures.Fields(2), family.FamilyId),
            ContentEdit.Add(Thing, new ContentKey("doomed"), PublishFixtures.Fields(3)),
            ContentEdit.Add(
                new ContentTypeId(PublishFixtures.OtherTypeId),
                new ContentKey("other"),
                PublishFixtures.Fields(4)));
        await catalog.Store.PublishAsync(PublishFixtures.Request(0));

        // The ids came from the allocator, some from the family's block, so they are read back rather than
        // assumed: what the bundle has to reproduce is whatever they turned out to be.
        int doomed = await IdOfAsync(catalog, "doomed");
        int plain = await IdOfAsync(catalog, "plain");

        await PublishFixtures.ApplyAsync(
            catalog.Store,
            ContentEdit.Retire(Thing, doomed, new ContentKey("doomed"), ContentRetirePolicy.Placeholder, 0));
        await catalog.Store.PublishAsync(PublishFixtures.Request(1));

        await PublishFixtures.ApplyAsync(
            catalog.Store,
            ContentEdit.Fork(
                Thing,
                plain,
                new ContentKey("plain"),
                new ContentKey("plain_legacy"),
                PublishFixtures.LegacyField,
                [new ContentFieldEdit(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 11))]));
        await catalog.Store.PublishAsync(PublishFixtures.Request(2));

        return await catalog.Store.ExportBundleAsync(3);
    }

    static async Task<int> IdOfAsync(Catalog catalog, string key)
    {
        ContentRowPage page = await catalog.Store.ListRowsAsync(Thing, 0, null, true, 0, 100);
        return page.Rows.Single(row => row.Key.ToString() == key).Id;
    }

    [Fact]
    public async Task AnExportCarriesTheFivePartsAndNamesEveryRowsId()
    {
        using var catalog = new Catalog();
        ContentBundle bundle = await SeedAsync(catalog);

        Assert.Equal(ContentBundle.CurrentFormatVersion, bundle.FormatVersion);
        Assert.Equal(3, bundle.SourceVersion);
        Assert.NotEmpty(bundle.StoreEpoch);
        Assert.Equal(2, bundle.Types.Count);
        Assert.All(bundle.Rows, row => Assert.NotNull(row.Id));
        Assert.Contains(bundle.Rows, row => row.IsRetired);
        Assert.Contains(bundle.Rows, row => row.FamilyKey == "swords");
        Assert.Equal(2, bundle.Rules.Count);
        Assert.Single(bundle.Families);
        Assert.NotEmpty(bundle.Families[0].Blocks);
    }

    [Fact]
    public async Task ExportThenImportIntoAnEmptyStoreReproducesTheSameRowsKeysAndIds()
    {
        using var source = new Catalog();
        ContentBundle bundle = await SeedAsync(source);

        using var destination = new Catalog();
        ContentPublishResult imported = await destination.Store.ImportBundleAsync(
            bundle, PublishFixtures.Actor, "oid:tests", "seed");

        // The import republishes at version 1, so the version LINE restarts. That is the difference between
        // a lossless export and a backup.
        Assert.Equal(1, imported.VersionNumber);

        ContentBundle again = await destination.Store.ExportBundleAsync(1);

        Assert.Equal(bundle.Rows.Count, again.Rows.Count);
        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            ContentBundleRow was = bundle.Rows[i];
            ContentBundleRow now = again.Rows[i];
            Assert.Equal(was.Type, now.Type);
            Assert.Equal(was.Id, now.Id);
            Assert.Equal(was.Key.ToString(), now.Key.ToString());
            Assert.Equal(was.IsRetired, now.IsRetired);
            Assert.Equal(was.FamilyKey, now.FamilyKey);
            Assert.Equal(was.Fields.Count, now.Fields.Count);
            for (int f = 0; f < was.Fields.Count; f++)
            {
                Assert.Equal(was.Fields[f].Name, now.Fields[f].Name);
                Assert.Equal(was.Fields[f].Value, now.Fields[f].Value);
            }
        }

        Assert.Equal(bundle.Families.Count, again.Families.Count);
        Assert.Equal(bundle.Families[0].FamilyId, again.Families[0].FamilyId);
        Assert.Equal(bundle.Families[0].FamilyKey, again.Families[0].FamilyKey);
        Assert.Equal(bundle.Families[0].BlockSize, again.Families[0].BlockSize);
        Assert.Equal(bundle.Families[0].Blocks[0].BaseId, again.Families[0].Blocks[0].BaseId);

        // The rules are the NEW line's: contiguous from 1, introduced in version 1, in the same order.
        Assert.Equal(bundle.Rules.Count, again.Rules.Count);
        for (int i = 0; i < again.Rules.Count; i++)
        {
            Assert.Equal(i + 1, again.Rules[i].Sequence);
            Assert.Equal(1, again.Rules[i].IntroducedIn);
            Assert.Equal(bundle.Rules[i].Kind, again.Rules[i].Kind);
            Assert.Equal(bundle.Rules[i].FromId, again.Rules[i].FromId);
            Assert.Equal(bundle.Rules[i].ToId, again.Rules[i].ToId);
        }

        Assert.NotEqual(bundle.StoreEpoch, again.StoreEpoch);
    }

    [Fact]
    public async Task AnIdFreeBundleIsAllocatedInItsOwnRowOrder()
    {
        using var first = new Catalog();
        using var second = new Catalog();
        ContentBundle bundle = IdFree();

        await first.Store.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed");
        await second.Store.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed");

        // Two publishes of one id-free bundle into two empty stores produce the SAME ids, which is what
        // makes an export and a re-import reproducible.
        Assert.Equal(1, await IdOfAsync(first, "one"));
        Assert.Equal(2, await IdOfAsync(first, "two"));
        Assert.Equal(3, await IdOfAsync(first, "three"));
        Assert.Equal(1, await IdOfAsync(second, "one"));
        Assert.Equal(2, await IdOfAsync(second, "two"));
        Assert.Equal(3, await IdOfAsync(second, "three"));
    }

    [Fact]
    public async Task ABundleMixesNamedAndUnnamedIdsAndNoUnnamedRowLandsOnANamedOne()
    {
        using var catalog = new Catalog();
        var bundle = new ContentBundle(
            ContentBundle.CurrentFormatVersion,
            "seed",
            0,
            [],
            [
                new ContentBundleRow(Thing, null, new ContentKey("first"), false, null, PublishFixtures.Fields(1)),
                new ContentBundleRow(Thing, 40, new ContentKey("named"), false, null, PublishFixtures.Fields(2)),
                new ContentBundleRow(Thing, null, new ContentKey("second"), false, null, PublishFixtures.Fields(3)),
            ],
            [],
            []);

        await catalog.Store.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed");

        // A bundle may MIX the two, because the id is per row. The named id is kept exactly, and the unnamed
        // ones come in the bundle's own order from ABOVE every id the bundle already named, so no allocation
        // can land on one.
        Assert.Equal(40, await IdOfAsync(catalog, "named"));
        int unnamed = await IdOfAsync(catalog, "first");
        Assert.True(unnamed > 40, FormattableString.Invariant($"Expected an id above 40 and got {unnamed}."));
        Assert.Equal(unnamed + 1, await IdOfAsync(catalog, "second"));

        // The marks are above the carried id too, so the first ordinary add after an import does not land on
        // an imported row.
        await PublishFixtures.ApplyAsync(
            catalog.Store,
            ContentEdit.Add(Thing, new ContentKey("after"), PublishFixtures.Fields(4)));
        await catalog.Store.PublishAsync(PublishFixtures.Request(1));

        Assert.Equal(unnamed + 2, await IdOfAsync(catalog, "after"));
    }

    static ContentBundle IdFree()
        => new(
            ContentBundle.CurrentFormatVersion,
            "seed",
            0,
            [],
            [
                new ContentBundleRow(Thing, null, new ContentKey("one"), false, null, PublishFixtures.Fields(1)),
                new ContentBundleRow(Thing, null, new ContentKey("two"), false, null, PublishFixtures.Fields(2)),
                new ContentBundleRow(Thing, null, new ContentKey("three"), false, null, PublishFixtures.Fields(3)),
            ],
            [],
            []);

    [Fact]
    public async Task AnImportIntoAStoreThatAlreadyPublishedIsRefusedWithNothingWritten()
    {
        using var source = new Catalog();
        ContentBundle bundle = await SeedAsync(source);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => source.Store.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed again"));

        Assert.Equal(ContentAuthoringException.CatalogNotEmptyReason, refused.Reason);
        Assert.Equal(ContentAuthoringRefusal.CatalogNotEmpty, refused.Refusal);
        Assert.Equal(3, refused.ActiveVersion);
        Assert.Equal(3, await source.Store.GetActiveVersionAsync());
        Assert.Null(await source.Store.GetOpenDraftAsync());
        using var empty = new Catalog();
        Assert.Equal(1, (await empty.Store.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed")).VersionNumber);
    }

    [Fact]
    public async Task AnImportOfATypeThisProcessDoesNotRegisterIsRefusedWhole()
    {
        using var catalog = new Catalog();
        var schema = new ContentFieldSchema([
            new ContentFieldEntry("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
        ]);
        var bundle = new ContentBundle(
            ContentBundle.CurrentFormatVersion,
            "seed",
            0,
            [new ContentBundleType(new ContentTypeId(2000), "unknown", ContentVisibility.Client, 256, null, schema)],
            [],
            [],
            []);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => catalog.Store.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed"));

        Assert.Equal(ContentAuthoringException.UnknownTypeReason, refused.Reason);
        Assert.Equal(0, await catalog.Store.GetActiveVersionAsync());
    }

    [Fact]
    public async Task ARefusedImportLeavesTheStoreEmptyRatherThanHalfSeeded()
    {
        using var catalog = new Catalog();

        // Two rows under one key, which KEC0002 refuses at validation, after the families and the rules are
        // already in. The store has to end up where it started.
        var bundle = new ContentBundle(
            ContentBundle.CurrentFormatVersion,
            "seed",
            0,
            [],
            [
                new ContentBundleRow(Thing, 1, new ContentKey("same"), false, null, PublishFixtures.Fields(1)),
                new ContentBundleRow(Thing, 2, new ContentKey("same"), false, null, PublishFixtures.Fields(2)),
            ],
            [],
            []);

        await Assert.ThrowsAsync<ContentAuthoringException>(
            () => catalog.Store.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed"));

        Assert.Equal(0, await catalog.Store.GetActiveVersionAsync());
        Assert.Empty(await catalog.Store.ListVersionsAsync());
        Assert.Empty(await catalog.Store.ListFamiliesAsync(default));
        Assert.Empty(catalog.Store.Rules);
        Assert.Null(await catalog.Store.GetOpenDraftAsync());
    }

    [Fact]
    public async Task AnExportOfAVersionTheStoreDoesNotHoldIsRefused()
    {
        using var catalog = new Catalog();

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => catalog.Store.ExportBundleAsync(9));

        Assert.Equal(ContentAuthoringException.UnknownVersionReason, refused.Reason);
    }

    [Fact]
    public async Task TheJsonDocumentIsByteIdenticalAcrossTwoExportsOfOneVersion()
    {
        using var catalog = new Catalog();
        ContentBundle bundle = await SeedAsync(catalog);

        string first = ContentBundleJson.Write(bundle);
        string second = ContentBundleJson.Write(await catalog.Store.ExportBundleAsync(3));

        // A bundle lands in a repository beside the code it seeds, so two exports of one version have to be
        // the same bytes. Every property is written explicitly, in a fixed order.
        Assert.Equal(first, second);
        Assert.Contains('\n', first);
        Assert.DoesNotContain('\r', first);
        Assert.Contains("\"formatVersion\": 1", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJsonRoundTripReproducesTheBundleAndImportsToTheSameIds()
    {
        using var source = new Catalog();
        ContentBundle bundle = await SeedAsync(source);

        ContentBundle read = ContentBundleJson.Read(ContentBundleJson.Write(bundle));

        Assert.Equal(ContentBundleJson.Write(bundle), ContentBundleJson.Write(read));

        using var destination = new Catalog();
        await destination.Store.ImportBundleAsync(read, PublishFixtures.Actor, "oid:tests", "seed");

        ContentBundle again = await destination.Store.ExportBundleAsync(1);
        Assert.Equal(
            bundle.Rows.Select(row => (row.Id, row.Key.ToString(), row.IsRetired)),
            again.Rows.Select(row => (row.Id, row.Key.ToString(), row.IsRetired)));
    }

    [Theory]
    [InlineData("[]", ContentAuthoringRefusal.BundleRootObjectRequired, "$")]
    [InlineData("{ \"formatVersion\": 2, \"storeEpoch\": \"x\", \"sourceVersion\": 0 }", ContentAuthoringRefusal.BundleMemberRequired, "$.types")]
    [InlineData("{ \"formatVersion\": 1, \"storeEpoch\": \"x\", \"sourceVersion\": 0 }", ContentAuthoringRefusal.BundleMemberRequired, "$.types")]
    public void ABundleDocumentThisBuildCannotReadIsRefusedRatherThanPartlyBuilt(
        string json, ContentAuthoringRefusal refusal, string path)
    {
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Equal(refusal, refused.Refusal);
        Assert.Equal(path, refused.JsonPath);
    }

    [Fact]
    public void ANegativeSourceVersionKeepsItsArgumentContract()
    {
        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContentBundleJson.Read(ValidDocument().Replace("\"sourceVersion\": 0", "\"sourceVersion\": -1", StringComparison.Ordinal)));
        Assert.Equal("sourceVersion", refused.ParamName);
        Assert.Equal(-1, refused.ActualValue);
        Assert.Equal(0, ContentBundleJson.Read(ValidDocument()).SourceVersion);
    }

    /// <summary>
    /// One defect per row against <see cref="ValidDocument"/>, then the same member repaired. A range row's
    /// repair sits at the inclusive limit, and each path is rooted at <c>$</c> with zero-based indices.
    /// </summary>
    [Theory]
    [InlineData("\"storeEpoch\": \"test\"", "\"epoch\": \"test\"", ContentAuthoringRefusal.BundleMemberRequired, "$.storeEpoch", null, null, "\"storeEpoch\": \"test\"")]
    [InlineData("\"storeEpoch\": \"test\"", "\"storeEpoch\": 5", ContentAuthoringRefusal.BundleMemberKindInvalid, "$.storeEpoch", JsonValueKind.Number, null, "\"storeEpoch\": \"test\"")]
    [InlineData("\"sourceVersion\": 0", "\"sourceVersion\": \"0\"", ContentAuthoringRefusal.BundleMemberKindInvalid, "$.sourceVersion", JsonValueKind.String, null, "\"sourceVersion\": 0")]
    [InlineData("\"sourceVersion\": 0", "\"sourceVersion\": 2147483648", ContentAuthoringRefusal.BundleInt32Unrepresentable, "$.sourceVersion", null, null, "\"sourceVersion\": 2147483647")]
    [InlineData("\"rules\": [", "\"spare\": [", ContentAuthoringRefusal.BundleMemberRequired, "$.rules", null, null, "\"rules\": [")]
    [InlineData("\"rows\": [", "\"rows\": null, \"spare\": [", ContentAuthoringRefusal.BundleMemberKindInvalid, "$.rows", JsonValueKind.Null, null, "\"rows\": [")]
    [InlineData("\"typeId\": 1024", "\"typeId\": 70000", ContentAuthoringRefusal.BundleTypeIdOutsideRange, "$.types[0].typeId", null, 70000, "\"typeId\": 65535")]
    [InlineData("\"typeId\": 1027", "\"typeId\": 0", ContentAuthoringRefusal.BundleTypeIdOutsideRange, "$.rules[0].typeId", null, 0, "\"typeId\": 1")]
    [InlineData("\"defaultVisibility\": 0", "\"defaultVisibility\": 2", ContentAuthoringRefusal.BundleVisibilityUnknown, "$.types[0].defaultVisibility", null, 2, "\"defaultVisibility\": 1")]
    [InlineData("\"kind\": 6", "\"kind\": 7", ContentAuthoringRefusal.BundleFieldKindUnknown, "$.types[0].fields[1].kind", null, 7, "\"kind\": 6")]
    [InlineData("\"maxDefinitionId\": null", "\"maxDefinitionId\": 2147483648", ContentAuthoringRefusal.BundleInt32Unrepresentable, "$.types[0].maxDefinitionId", null, null, "\"maxDefinitionId\": 2147483647")]
    [InlineData("\"required\": false", "\"needed\": false", ContentAuthoringRefusal.BundleMemberRequired, "$.types[0].fields[0].required", null, null, "\"required\": false")]
    [InlineData("\"required\": true", "\"required\": 1", ContentAuthoringRefusal.BundleMemberKindInvalid, "$.types[0].fields[1].required", JsonValueKind.Number, null, "\"required\": true")]
    [InlineData("\"familyId\": 7", "\"familyId\": \"7\"", ContentAuthoringRefusal.BundleMemberKindInvalid, "$.families[0].familyId", JsonValueKind.String, null, "\"familyId\": 7")]
    [InlineData("\"familyId\": 7", "\"familyId\": 9223372036854775808", ContentAuthoringRefusal.BundleInt64Unrepresentable, "$.families[0].familyId", null, null, "\"familyId\": 9223372036854775807")]
    [InlineData("\"baseId\": 16", "\"baseId\": 16.5", ContentAuthoringRefusal.BundleInt32Unrepresentable, "$.families[0].blocks[0].baseId", null, null, "\"baseId\": 16")]
    [InlineData("\"number\": 5", "\"number\": 1.5", ContentAuthoringRefusal.BundleInt64Unrepresentable, "$.rows[0].fields[0].number", null, null, "\"number\": 5")]
    [InlineData("\"bytes\": \"0a\"", "\"bytes\": \"0g\"", ContentAuthoringRefusal.BundleHexMalformed, "$.rows[0].fields[1].bytes", null, null, "\"bytes\": \"0A\"")]
    [InlineData("\"payload\": \"\"", "\"payload\": \"abc\"", ContentAuthoringRefusal.BundleHexMalformed, "$.rules[0].payload", null, null, "\"payload\": \"abcd\"")]
    [InlineData("\"kind\": 3", "\"kind\": 0", ContentAuthoringRefusal.BundleRemapKindUnknown, "$.rules[0].kind", null, 0, "\"kind\": 1")]
    [InlineData("\"kind\": 3", "\"kind\": 4294967297", ContentAuthoringRefusal.BundleInt32Unrepresentable, "$.rules[0].kind", null, null, "\"kind\": 1")]
    public void ABundleIntegerOutsideItsTargetDomainIsRefused(
        string original,
        string replacement,
        ContentAuthoringRefusal refusal,
        string path,
        JsonValueKind? actualKind,
        int? actualInteger,
        string control)
    {
        string document = ValidDocument();
        Assert.Equal(1, Occurrences(document, original));

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(document.Replace(original, replacement, StringComparison.Ordinal)));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Equal(refusal, refused.Refusal);
        Assert.Equal(path, refused.JsonPath);
        Assert.Equal(actualKind, refused.ActualJsonKind);
        Assert.Equal(actualInteger, refused.ActualInteger);
        bool typeIdRange = refusal == ContentAuthoringRefusal.BundleTypeIdOutsideRange;
        Assert.Equal(typeIdRange ? 1 : null, refused.MinimumInteger);
        Assert.Equal(typeIdRange ? 65535 : null, refused.MaximumInteger);

        ContentBundle repaired = ContentBundleJson.Read(document.Replace(original, control, StringComparison.Ordinal));
        Assert.Equal(1, Assert.Single(repaired.Rules).Sequence);
    }

    [Theory]
    [InlineData("$.types[0]")]
    [InlineData("$.types[0].fields[0]")]
    [InlineData("$.families[0]")]
    [InlineData("$.families[0].blocks[0]")]
    [InlineData("$.rows[0]")]
    [InlineData("$.rows[0].fields[0]")]
    [InlineData("$.rules[0]")]
    public void Every_bundle_array_refuses_a_nonobject_element_at_its_own_path(string path)
    {
        JsonNode document = JsonNode.Parse(ValidDocument())!;
        JsonArray array = path switch
        {
            "$.types[0]" => document["types"]!.AsArray(),
            "$.types[0].fields[0]" => document["types"]![0]!["fields"]!.AsArray(),
            "$.families[0]" => document["families"]!.AsArray(),
            "$.families[0].blocks[0]" => document["families"]![0]!["blocks"]!.AsArray(),
            "$.rows[0]" => document["rows"]!.AsArray(),
            "$.rows[0].fields[0]" => document["rows"]![0]!["fields"]!.AsArray(),
            "$.rules[0]" => document["rules"]!.AsArray(),
            _ => throw new ArgumentException("The theory names an array.", nameof(path)),
        };
        JsonNode control = array[0]!.DeepClone();
        array[0] = 5;

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() => ContentBundleJson.Read(document.ToJsonString()));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Equal(ContentAuthoringRefusal.BundleMemberKindInvalid, refused.Refusal);
        Assert.Equal(path, refused.JsonPath);
        Assert.Equal(JsonValueKind.Number, refused.ActualJsonKind);
        array[0] = control;
        Assert.Single(ContentBundleJson.Read(document.ToJsonString()).Types);
    }

    /// <summary>
    /// One schema defect per row against <see cref="ValidDocument"/>, mapped from the schema's own finding, then the
    /// same field repaired. A scale row refuses at the limit and repairs at it.
    /// </summary>
    [Theory]
    [InlineData("\"name\": \"number\", \"kind\": 0", "\"name\": \" \", \"kind\": 0", "\"name\": \"n\", \"kind\": 0", ContentAuthoringRefusal.BundleFieldNameBlank, "$.types[0].fields[0].name", null, null, null, null, null)]
    [InlineData("\"name\": \"blob\", \"kind\": 6", "\"name\": \"number\", \"kind\": 6", "\"name\": \"numbers\", \"kind\": 6", ContentAuthoringRefusal.BundleFieldNameDuplicate, "$.types[0].fields[1].name", "number", null, null, null, null)]
    [InlineData("\"kind\": 0, \"referenceTarget\": null", "\"kind\": 3, \"referenceTarget\": \" \"", "\"kind\": 3, \"referenceTarget\": \"a\"", ContentAuthoringRefusal.BundleReferenceTargetMissing, "$.types[0].fields[0].referenceTarget", "number", null, null, null, null)]
    [InlineData("\"kind\": 0, \"referenceTarget\": null", "\"kind\": 4, \"referenceTarget\": \"Tag\"", "\"kind\": 4, \"referenceTarget\": \"tag\"", ContentAuthoringRefusal.BundleTagReferenceTargetInvalid, "$.types[0].fields[0].referenceTarget", "number", null, null, null, null)]
    [InlineData("\"kind\": 0, \"referenceTarget\": null", "\"kind\": 0, \"referenceTarget\": \"\"", "\"kind\": 0, \"referenceTarget\": null", ContentAuthoringRefusal.BundleUnexpectedReferenceTarget, "$.types[0].fields[0].referenceTarget", "number", ContentFieldKind.Int, null, null, null)]
    [InlineData("\"kind\": 0, \"referenceTarget\": null, \"visibility\": 0, \"required\": false, \"scale\": 1", "\"kind\": 1, \"referenceTarget\": null, \"visibility\": 0, \"required\": false, \"scale\": 0", "\"kind\": 1, \"referenceTarget\": null, \"visibility\": 0, \"required\": false, \"scale\": 1", ContentAuthoringRefusal.BundleScaledIntegerScaleInvalid, "$.types[0].fields[0].scale", "number", null, 0, 1, null)]
    [InlineData("\"required\": true, \"scale\": 1", "\"required\": true, \"scale\": 2", "\"required\": true, \"scale\": 1", ContentAuthoringRefusal.BundleUnscaledKindScaleInvalid, "$.types[0].fields[1].scale", "blob", ContentFieldKind.OpaqueBytes, 2, 1, 1)]
    [InlineData("\"required\": true, \"scale\": 1", "\"required\": true, \"scale\": 0", "\"required\": true, \"scale\": 1", ContentAuthoringRefusal.BundleUnscaledKindScaleInvalid, "$.types[0].fields[1].scale", "blob", ContentFieldKind.OpaqueBytes, 0, 1, 1)]
    public void ASchemaFieldTheSchemaRefusesNamesItsRuleAndField(
        string original,
        string replacement,
        string control,
        ContentAuthoringRefusal refusal,
        string path,
        string? fieldName,
        ContentFieldKind? actualKind,
        int? actualInteger,
        int? minimumInteger,
        int? maximumInteger)
    {
        string document = ValidDocument();
        Assert.Equal(1, Occurrences(document, original));

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(document.Replace(original, replacement, StringComparison.Ordinal)));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Equal(refusal, refused.Refusal);
        Assert.Equal(path, refused.JsonPath);
        Assert.Equal(fieldName, refused.FieldName);
        Assert.Equal(actualKind, refused.ActualKind);
        Assert.Equal(actualInteger, refused.ActualInteger);
        Assert.Equal(minimumInteger, refused.MinimumInteger);
        Assert.Equal(maximumInteger, refused.MaximumInteger);
        Assert.IsAssignableFrom<ArgumentException>(refused.InnerException);

        ContentBundle repaired = ContentBundleJson.Read(document.Replace(original, control, StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(repaired.Types).Schema.Fields.Count);
    }

    /// <summary>
    /// One family, rule or row value defect per row against <see cref="ValidDocument"/>, mapped from the lower
    /// constructor's own rule, then the same member repaired. A range row refuses past the inclusive limit and
    /// repairs at it.
    /// </summary>
    [Theory]
    [InlineData("\"familyKey\": \"swords\", \"blockSize\": 16", "\"familyKey\": \" \", \"blockSize\": 16", "\"familyKey\": \"s\", \"blockSize\": 16", ContentAuthoringRefusal.BundleFamilyKeyBlank, "$.families[0].familyKey", null, null, null, null, null, null, null, null)]
    [InlineData("\"blockSize\": 16", "\"blockSize\": 8", "\"blockSize\": 16", ContentAuthoringRefusal.FamilyBlockSizeOutsideRange, "$.families[0].blockSize", "swords", 8, 16, 65536, null, null, null, null)]
    [InlineData("\"blockSize\": 16", "\"blockSize\": 24", "\"blockSize\": 16", ContentAuthoringRefusal.FamilyBlockSizeNotPowerOfTwo, "$.families[0].blockSize", "swords", 24, null, null, null, null, null, null)]
    [InlineData("\"baseId\": 16", "\"baseId\": 17", "\"baseId\": 32", ContentAuthoringRefusal.BundleFamilyBlockAlignmentInvalid, "$.families[0].blocks[0].baseId", null, null, null, null, 17, null, null, null)]
    [InlineData("\"sequence\": 1", "\"sequence\": -1", "\"sequence\": 0", ContentAuthoringRefusal.BundleRuleIntegerNegative, "$.rules[0].sequence", null, null, null, null, -1, 0, null, null)]
    [InlineData("\"introducedIn\": 1", "\"introducedIn\": -1", "\"introducedIn\": 0", ContentAuthoringRefusal.BundleRuleIntegerNegative, "$.rules[0].introducedIn", null, null, null, null, -1, 0, null, null)]
    [InlineData("\"fromId\": 1", "\"fromId\": -1", "\"fromId\": 0", ContentAuthoringRefusal.BundleRuleIntegerNegative, "$.rules[0].fromId", null, null, null, null, -1, 0, null, null)]
    [InlineData("\"toId\": 2", "\"toId\": -1", "\"toId\": 0", ContentAuthoringRefusal.BundleRuleIntegerNegative, "$.rules[0].toId", null, null, null, null, -1, 0, null, null)]
    [InlineData("\"kind\": 3, \"fromId\": 1, \"toId\": 2, \"payload\": \"\"", "\"kind\": 4, \"fromId\": 1, \"toId\": 2, \"payload\": \"000000\"", "\"kind\": 4, \"fromId\": 1, \"toId\": 2, \"payload\": \"00000000\"", ContentAuthoringRefusal.BundleRulePayloadMalformed, "$.rules[0].payload", null, null, null, null, null, null, RemapRuleKind.StackCapLowered, 3L)]
    [InlineData("\"kind\": 4, \"bytes\": \"0a\"", "\"kind\": 5, \"bytes\": \"0a\"", "\"kind\": 5, \"absent\": true", ContentAuthoringRefusal.BundleMarkerValueAuthored, "$.rows[0].fields[1].bytes", null, null, null, null, null, null, null, null)]
    public void AFamilyRuleOrRowValueItsConstructorRefusesNamesItsRule(
        string original,
        string replacement,
        string control,
        ContentAuthoringRefusal refusal,
        string path,
        string? familyKey,
        int? actualBlockSize,
        int? minimumBlockSize,
        int? maximumBlockSize,
        int? actualInteger,
        int? minimumInteger,
        RemapRuleKind? ruleKind,
        long? actualBytes)
    {
        string document = ValidDocument();
        if (refusal == ContentAuthoringRefusal.FamilyBlockSizeNotPowerOfTwo)
        {
            const string block = "\"baseId\": 16, \"nextFreeId\": 17";
            Assert.Equal(1, Occurrences(document, block));
            document = document.Replace(block, "\"baseId\": 48, \"nextFreeId\": 49", StringComparison.Ordinal);
        }
        Assert.Equal(1, Occurrences(document, original));

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(document.Replace(original, replacement, StringComparison.Ordinal)));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Equal(refusal, refused.Refusal);
        Assert.Equal(path, refused.JsonPath);
        Assert.Equal(familyKey, refused.FamilyKey);
        Assert.Equal(actualBlockSize, refused.ActualBlockSize);
        Assert.Equal(minimumBlockSize, refused.MinimumBlockSize);
        Assert.Equal(maximumBlockSize, refused.MaximumBlockSize);
        Assert.Equal(actualInteger, refused.ActualInteger);
        Assert.Equal(minimumInteger, refused.MinimumInteger);
        Assert.Null(refused.MaximumInteger);
        Assert.Equal(ruleKind, refused.RuleKind);
        Assert.Equal(actualBytes, refused.ActualBytes);
        Assert.IsAssignableFrom<ArgumentException>(refused.InnerException);

        ContentBundle repaired = ContentBundleJson.Read(document.Replace(original, control, StringComparison.Ordinal));
        Assert.Single(Assert.Single(repaired.Families).Blocks);
        Assert.Single(repaired.Rules);
        Assert.Equal(2, Assert.Single(repaired.Rows).Fields.Count);
    }

    [Fact]
    public void A_family_block_size_above_the_ceiling_is_refused_beside_the_ceiling_control()
    {
        JsonNode document = JsonNode.Parse(ValidDocument())!;
        JsonNode family = document["families"]![0]!;
        family["blockSize"] = 131072;
        family["blocks"]![0]!["baseId"] = 131072;
        family["blocks"]![0]!["nextFreeId"] = 131073;

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() => ContentBundleJson.Read(document.ToJsonString()));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Equal(ContentAuthoringRefusal.FamilyBlockSizeOutsideRange, refused.Refusal);
        Assert.Equal("$.families[0].blockSize", refused.JsonPath);
        Assert.Equal("swords", refused.FamilyKey);
        Assert.Equal(131072, refused.ActualBlockSize);
        Assert.Equal(16, refused.MinimumBlockSize);
        Assert.Equal(65536, refused.MaximumBlockSize);
        family["blockSize"] = 65536;
        Assert.Equal(65536, Assert.Single(ContentBundleJson.Read(document.ToJsonString()).Families).BlockSize);
    }

    static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// A format 1 document every reader helper visits once: a type with a number and a bytes field, a family
    /// with one block, a row with a number and a bytes value, and a rule. Each type id and kind literal is
    /// distinct, so a row can break exactly one member.
    /// </summary>
    static string ValidDocument() => """
        {
          "formatVersion": 1,
          "storeEpoch": "test",
          "sourceVersion": 0,
          "types": [
            {
              "typeId": 1024,
              "typeKey": "thing",
              "defaultVisibility": 0,
              "chunkSlots": 1,
              "maxDefinitionId": null,
              "fields": [
                { "name": "number", "kind": 0, "referenceTarget": null, "visibility": 0, "required": false, "scale": 1 },
                { "name": "blob", "kind": 6, "referenceTarget": null, "visibility": 1, "required": true, "scale": 1 }
              ]
            }
          ],
          "families": [
            {
              "familyId": 7,
              "typeId": 1025,
              "familyKey": "swords", "blockSize": 16,
              "retired": false,
              "createdInVersion": 1,
              "blocks": [ { "ordinal": 0, "baseId": 16, "nextFreeId": 17, "reservedInVersion": 1 } ]
            }
          ],
          "rows": [
            {
              "typeId": 1026,
              "id": 16,
              "key": "sword",
              "retired": false,
              "familyKey": "swords",
              "fields": [
                { "name": "number", "kind": 1, "number": 5 },
                { "name": "blob", "kind": 4, "bytes": "0a" }
              ]
            }
          ],
          "rules": [
            { "sequence": 1, "introducedIn": 1, "typeId": 1027, "kind": 3, "fromId": 1, "toId": 2, "payload": "" }
          ]
        }
        """;

    [Fact]
    public async Task AnImportWritesOneBulkImportAuditRowCarryingTheRowCount()
    {
        using var source = new Catalog();
        ContentBundle bundle = await SeedAsync(source);

        using var destination = new Catalog();
        await destination.Store.ImportBundleAsync(bundle, PublishFixtures.Actor, "oid:tests", "seed");

        ContentAuditEntry entry = (await destination.Store.ListAuditAsync(default, 0, 0, 200))
            .First(one => string.Equals(one.Action, ContentAuditActions.BulkImport, StringComparison.Ordinal));

        Assert.Equal(bundle.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.AfterValue);
        Assert.Equal(1, entry.VersionNumber);
    }
}
