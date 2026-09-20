using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// Two runners against ONE catalog at the same time, which is the deploy that ran twice and the two replicas
/// that booted together.
/// <para>
/// <b>The guarantee is one published version per definition, whoever wins.</b> The ledger's primary key is
/// what enforces it: the loser's commit is refused whole, it reads the ledger rather than assuming, and it
/// adopts the winner's result. A failure path that left a draft behind would wedge the catalog for the next
/// run, so that is asserted too.
/// </para>
/// <para>
/// <b>It loops</b>, because a race that only shows one time in ten is the one that reaches production. One
/// pass proves nothing about interleaving.
/// </para>
/// <para>
/// Both runners share ONE store instance, which is the shape a host really has: a process opens its catalog
/// once. The publishes still interleave, because a publish writes its whole pack outside any connection
/// lease and the runners' plans, draft writes and commits are separate calls.
/// </para>
/// </summary>
public sealed class ContentUpgradeConcurrencyTests
{
    /// <summary>How many times the race is run. Twenty is enough for an interleaving to show at least once.</summary>
    const int Iterations = 20;

    /// <summary>The in-memory reference store, which is the fastest interleaving available.</summary>
    [Fact]
    public async Task TwoRunnersOnOneInMemoryCatalogPublishEachUpgradeExactlyOnce()
    {
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using var harness = new UpgradeHarness();
            await harness.SeedOlderCatalogAsync();

            await RaceAsync(harness.Store, harness.Registry, SetFor(harness.Registry), iteration);
        }
    }

    /// <summary>The same race on a real SQLite FILE, where a publish also writes a pack to disk.</summary>
    [Fact]
    public async Task TwoRunnersOnOneSqliteCatalogPublishEachUpgradeExactlyOnce()
    {
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using var database = new TemporaryCatalogDatabase();
            ContentTypeRegistry registry = PublishFixtures.Registry(
                PublishFixtures.Thing, PublishFixtures.Other);
            using var store = new SqliteContentAuthoringStore(
                database.ConnectionString, registry, database.Pack());
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await store.ApplyEditsAsync(
                [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
                UpgradeFixtures.Actor,
                UpgradeFixtures.Operator,
                "seed the older catalog");
            await store.PublishAsync(PublishFixtures.Request(0));

            await RaceAsync(store, registry, SetFor(registry), iteration);
        }
    }

    /// <summary>The two definitions this build ships, over a registry, with their committed target bundle.</summary>
    static ContentUpgradeSet SetFor(ContentTypeRegistry registry)
    {
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));
        return new ContentUpgradeSet(
            UpgradeFixtures.Adds(
                UpgradeHarness.FirstId, 1, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "new_row")),
            UpgradeFixtures.Adds(
                UpgradeHarness.SecondId,
                2,
                target,
                UpgradeFixtures.Identity(UpgradeFixtures.Other, "second_new_row")));
    }

    /// <summary>
    /// Both runners at once, then the four things that have to hold however they interleaved. They go
    /// through <c>Task.Run</c> because the in-memory store answers synchronously, so a bare call would run
    /// the whole first report before the second one started and the test would prove nothing.
    /// </summary>
    static async Task RaceAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        ContentUpgradeSet set,
        int iteration)
    {
        ContentUpgradeReport[] reports = await Task.WhenAll(
            Task.Run(() => ContentUpgradeRunner.RunAsync(store, registry, set, UpgradeFixtures.Apply())),
            Task.Run(() => ContentUpgradeRunner.RunAsync(store, registry, set, UpgradeFixtures.Apply())));

        string context = Describe(iteration, reports);
        Assert.True(reports[0].Success, context);
        Assert.True(reports[1].Success, context);
        Assert.Equal(3, (await store.ListVersionsAsync()).Count);

        IReadOnlyList<ContentUpgradeRecord> ledger = await ((IContentUpgradeLedger)store).ListUpgradesAsync();
        Assert.Equal(2, ledger.Count);
        Assert.Equal([UpgradeHarness.FirstId, UpgradeHarness.SecondId], Ids(ledger));

        // A failure path that left a draft behind would refuse every later edit and wedge the catalog.
        Assert.Null(await store.GetOpenDraftAsync());

        // EXACTLY the committed ids, under contention. A losing runner's prepare still advanced the id mark
        // durably before it was refused, so ids that came from the allocator could not hold this. They come
        // from the committed bundle instead, so they do.
        ContentRowPage rows = await store.ListRowsAsync(UpgradeFixtures.Other, 0, null, false, 0, 50);
        Assert.Equal(2, rows.Total);
        Assert.Equal(1, rows.Rows[0].Id);
        Assert.Equal("new_row", rows.Rows[0].Key.ToString());
        Assert.Equal(2, rows.Rows[1].Id);
        Assert.Equal("second_new_row", rows.Rows[1].Key.ToString());
    }

    static string Describe(int iteration, IReadOnlyList<ContentUpgradeReport> reports)
    {
        var lines = new List<string> { "iteration " + iteration.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        for (int i = 0; i < reports.Count; i++)
        {
            lines.AddRange(reports[i].Lines);
        }

        return string.Join(" | ", lines);
    }

    static string[] Ids(IReadOnlyList<ContentUpgradeRecord> records)
    {
        var ids = new string[records.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = records[i].Id;
        }

        return ids;
    }
}
