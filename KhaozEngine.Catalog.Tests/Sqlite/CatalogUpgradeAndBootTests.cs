using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Upgrade;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The regression this whole lifecycle exists for: an OLDER POPULATED catalog, published under a registry
/// that did not know the new type, upgraded in place and then booted STRICTLY through <see cref="ContentBoot"/>
/// by the new build.
/// <para>
/// <b>Nothing short of a boot proves it.</b> A fresh install seeded from the current bundle passes every
/// other suite there is, and the failure that shipped was a strict load refusing an older catalog at step 6
/// with the host exiting before it opened a socket. The pack the upgrade generated for the NEW type is what
/// the boot reads, so the chunk's existence is asserted as well as its content.
/// </para>
/// </summary>
public sealed class CatalogUpgradeAndBootTests
{
    static ContentTypeId Thing => UpgradeFixtures.Thing;

    static ContentTypeId Other => UpgradeFixtures.Other;

    /// <summary>
    /// The whole journey: publish under the old registry, reopen under the superset, apply, then boot the
    /// published version and read the new type's row out of the pack.
    /// </summary>
    [Fact]
    public async Task AnOlderPopulatedCatalogIsUpgradedAndThenBootsStrictly()
    {
        using var database = new TemporaryCatalogDatabase();
        await PublishUnderTheOldRegistryAsync(database);

        ContentTypeRegistry superset = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        var packs = database.Pack();
        ContentUpgradeReport report;
        using (var store = new SqliteContentAuthoringStore(database.ConnectionString, superset, packs))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
            report = await ContentUpgradeRunner.RunAsync(
                store, superset, SetFor(superset), UpgradeFixtures.Apply(expectedVersion: 1));

            Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
            Assert.Equal(2, report.ActiveVersionAfter);
            Assert.Single(await store.ListUpgradesAsync());
            Assert.Null(await store.GetOpenDraftAsync());
        }

        // The pack the upgrade generated carries a chunk for the NEW type, and the store really holds it.
        PackVersionPointer pointer = Assert.IsType<PackVersionPointer>(await packs.GetVersionPointerAsync(2));
        ContentManifestRead read = await ContentPackReader.ReadManifestAsync(
            packs, pointer.ServerManifestHash, ContentManifestSide.Server, superset);
        Assert.True(read.Success, read.Reason ?? "no reason");
        ManifestTypeEntry added = Single(read.Manifest!.Types, Other.Value);
        ManifestChunkEntry chunk = Assert.Single(added.Chunks);
        Assert.True(await packs.ExistsAsync(chunk.Hash));

        // The strict boot the old catalog used to refuse.
        superset.Freeze();
        var holder = new ContentRuntimeHolder();
        ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
        {
            Registry = superset,
            Store = packs,
            Pointers = packs,
            Holder = holder,
            ConfiguredVersion = 2,
            ServerBuild = int.MaxValue,
            StoreName = "the upgrade and boot test store",
        });

        Assert.True(boot.Success, string.Join(" | ", boot.StandardError));
        Assert.Equal(2, boot.Runtime!.VersionNumber);
        Assert.True(boot.Runtime.TryGetRow(Thing, 1, out ContentRow? carried));
        Assert.Equal(11, carried.Fields[0].Number);
        Assert.True(boot.Runtime.TryGetRow(Other, 1, out ContentRow? added1));
        Assert.Equal("new_row", added1.Key.ToString());
        Assert.Equal(22, added1.Fields[0].Number);
    }

    /// <summary>
    /// A field an OPERATOR changed before the upgrade keeps its value after it, and the rows the upgrade did
    /// not name are not touched at all: their history shows no new revision and no replacement.
    /// </summary>
    [Fact]
    public async Task OperatorTunedValuesSurviveTheUpgradeAndTheirRowsAreUntouched()
    {
        using var database = new TemporaryCatalogDatabase();
        await PublishUnderTheOldRegistryAsync(database);

        ContentTypeRegistry superset = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, superset, database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        // The operator's own pass, published as version 2 before the upgrade ever runs.
        await store.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("old_row"), PublishFixtures.Fields(99))],
            "a-human-operator",
            "oid:human",
            "autumn price pass");
        await store.PublishAsync(PublishFixtures.Request(1));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, superset, SetFor(superset), UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(3, report.ActiveVersionAfter);

        ContentRowPage tuned = await store.ListRowsAsync(Thing, 0, null, false, 0, 10);
        Assert.Equal(99, Assert.Single(tuned.Rows).Fields[0].Number);

        // Two revisions and no more: the one the seed published and the one the operator published. The
        // upgrade's own version appears nowhere in this row's history, which is the whole claim.
        IReadOnlyList<ContentRowRevision> history = await store.GetRowHistoryAsync(Thing, 1);
        Assert.Equal(2, history.Count);
        Assert.Equal(1, history[0].ValidFromVersion);
        Assert.Equal(2, history[0].ReplacedInVersion);
        Assert.Equal(11, history[0].Row.Fields[0].Number);
        Assert.Equal(2, history[1].ValidFromVersion);
        Assert.Null(history[1].ReplacedInVersion);
        Assert.Equal(99, history[1].Row.Fields[0].Number);
    }

    /// <summary>
    /// Version 1 published by a build whose registry knew ONE type, which is what an older deployed catalog
    /// really is. The store is disposed, so the next open is a genuine reopen.
    /// </summary>
    static async Task PublishUnderTheOldRegistryAsync(TemporaryCatalogDatabase database)
    {
        ContentTypeRegistry older = PublishFixtures.Registry(PublishFixtures.Thing);
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, older, database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ApplyEditsAsync(
            [ContentEdit.Add(Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            "publish under the old registry");
        ContentPublishResult published = await store.PublishAsync(PublishFixtures.Request(0));
        Assert.Equal(1, published.VersionNumber);
    }

    /// <summary>The one definition this build ships, which adds the new type's first row.</summary>
    static ContentUpgradeSet SetFor(ContentTypeRegistry registry)
    {
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(Other, 1, "new_row", 22));
        return new ContentUpgradeSet(UpgradeFixtures.Adds(
            "add-the-new-type", 1, target, UpgradeFixtures.Identity(Other, "new_row")));
    }

    static ManifestTypeEntry Single(IReadOnlyList<ManifestTypeEntry> types, ushort typeId)
    {
        for (int i = 0; i < types.Count; i++)
        {
            if (types[i].TypeId == typeId)
            {
                return types[i];
            }
        }

        throw new Xunit.Sdk.XunitException(FormattableString.Invariant(
            $"The manifest names no type {typeId}. It names {types.Count} type(s)."));
    }
}
