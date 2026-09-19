using System;
using System.Linq;
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
        Assert.Contains("3", refused.Message, StringComparison.Ordinal);
        Assert.Equal(3, await source.Store.GetActiveVersionAsync());
        Assert.Null(await source.Store.GetOpenDraftAsync());
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
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{ \"formatVersion\": 2, \"storeEpoch\": \"x\", \"sourceVersion\": 0 }")]
    [InlineData("{ \"formatVersion\": 1, \"storeEpoch\": \"x\", \"sourceVersion\": 0 }")]
    public void ABundleDocumentThisBuildCannotReadIsRefusedRatherThanPartlyBuilt(string json)
    {
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
    }

    [Theory]
    [InlineData("sourceVersion", "0", "2147483648")]
    [InlineData("typeId", "1024", "70000")]
    [InlineData("defaultVisibility", "0", "2")]
    [InlineData("kind", "0", "7")]
    public void ABundleIntegerOutsideItsTargetDomainIsRefused(
        string member,
        string original,
        string replacement)
    {
        string json = ValidTypeDocument().Replace(
            FormattableString.Invariant($"\"{member}\": {original}"),
            FormattableString.Invariant($"\"{member}\": {replacement}"),
            StringComparison.Ordinal);

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Contains(member, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABundleRemapKindOutsideTheIntegerDomainIsRefused()
    {
        string json = """
            {
              "formatVersion": 1,
              "storeEpoch": "test",
              "sourceVersion": 0,
              "types": [],
              "families": [],
              "rows": [],
              "rules": [
                {
                  "sequence": 1,
                  "introducedIn": 1,
                  "typeId": 1,
                  "kind": 4294967297,
                  "fromId": 1,
                  "toId": 2,
                  "payload": ""
                }
              ]
            }
            """;

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentBundleJson.Read(json));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Contains("kind", refused.Message, StringComparison.Ordinal);
    }

    static string ValidTypeDocument() => """
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
                {
                  "name": "number",
                  "kind": 0,
                  "referenceTarget": null,
                  "visibility": 0,
                  "required": false,
                  "scale": 1
                }
              ]
            }
          ],
          "families": [],
          "rows": [],
          "rules": []
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
