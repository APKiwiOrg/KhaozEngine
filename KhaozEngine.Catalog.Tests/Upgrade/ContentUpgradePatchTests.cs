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
/// An upgrade that patches TWO fields of one row, end to end on both providers.
/// <para>
/// One row is one edit in the draft, whatever a planner staged, because a draft holds one pending intent
/// per row. Two edits of one target would have the second replace the first at the store, publish only the
/// last field, and leave a draft that no later run can ever prove is its own.
/// </para>
/// </summary>
public sealed class ContentUpgradePatchTests
{
    /// <summary>The patching definition's stable id.</summary>
    const string PatchId = "patch-both-fields";

    /// <summary>The row the upgrade patches.</summary>
    const string RowKey = "old_row";

    /// <summary>The int field's old shipped default.</summary>
    const int OldValue = 11;

    /// <summary>The int field's new value.</summary>
    const int NewValue = 12;

    /// <summary>Both fields land on the in-memory store, under one published version.</summary>
    [Fact]
    public async Task TwoPatchesOfOneRowBothPublishOnTheInMemoryStore()
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = Registry();
        var store = new InMemoryContentAuthoringStore(registry, files.Pack());
        await SeedAsync(store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, registry, Set(registry), UpgradeFixtures.Apply());

        await AssertBothFieldsPublishedAsync(store, report);
    }

    /// <summary>The same upgrade on a real SQLite file, which rebuilds its draft from a stored edit table.</summary>
    [Fact]
    public async Task TwoPatchesOfOneRowBothPublishOnSqlite()
    {
        using var database = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = Registry();
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, registry, database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await SeedAsync(store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, registry, Set(registry), UpgradeFixtures.Apply());

        await AssertBothFieldsPublishedAsync(store, report);
    }

    /// <summary>
    /// A run killed between its edits and its publish leaves a draft holding the one edit its plan really
    /// produced, so the next run proves it and publishes it. A plan that had staged two edits of one row
    /// could never match the one the store kept, and every later boot would report an operator draft nobody
    /// opened.
    /// </summary>
    [Fact]
    public async Task AnInterruptedRunOfATwoPatchPlanRecoversToExactlyOnePublishedVersion()
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = Registry();
        var store = new InMemoryContentAuthoringStore(registry, files.Pack());
        await SeedAsync(store);
        ContentUpgradeSet set = Set(registry);
        ContentBundle baseline = await store.ExportBundleAsync(1);
        ContentUpgradePlan plan = set.Definitions[0].Plan(new ContentUpgradeContext(1, baseline, registry));
        await store.ApplyEditsAsync(
            plan.Edits,
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            ContentUpgradeRunner.NoteFor(PatchId));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            store, registry, set, UpgradeFixtures.Apply());

        Assert.Contains(
            report.Diagnostics,
            diagnostic => string.Equals(
                diagnostic.Code, ContentUpgradeCodes.DraftRecovered, StringComparison.Ordinal));
        await AssertBothFieldsPublishedAsync(store, report);
    }

    /// <summary>The current build's registry, carrying both fixture types.</summary>
    static ContentTypeRegistry Registry()
        => PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);

    /// <summary>The one patching definition this build ships, over its committed target bundle.</summary>
    static ContentUpgradeSet Set(ContentTypeRegistry registry)
        => new(UpgradeFixtures.PatchesBothFields(
            PatchId,
            1,
            UpgradeFixtures.Target(
                registry, UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, RowKey, NewValue)),
            UpgradeFixtures.Thing,
            RowKey,
            OldValue,
            NewValue));

    /// <summary>The older catalog: version 1 carrying the row at its old shipped default.</summary>
    static async Task SeedAsync(IContentAuthoringStore store)
    {
        await store.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey(RowKey), PublishFixtures.Fields(OldValue))],
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            "seed the older catalog");
        await store.PublishAsync(PublishFixtures.Request(0));
    }

    /// <summary>
    /// One published version, one ledger row, no draft left standing, and BOTH patched fields holding their
    /// new values on the row the upgrade named.
    /// </summary>
    static async Task AssertBothFieldsPublishedAsync(IContentAuthoringStore store, ContentUpgradeReport report)
    {
        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(2, report.ActiveVersionAfter);
        Assert.Equal(2, (await store.ListVersionsAsync()).Count);
        Assert.Null(await store.GetOpenDraftAsync());
        ContentUpgradeRecord record = Assert.Single(await ((IContentUpgradeLedger)store).ListUpgradesAsync());
        Assert.Equal(PatchId, record.Id);

        ContentBundle published = await store.ExportBundleAsync(2);
        ContentBundleRow row = Assert.IsType<ContentBundleRow>(
            ContentUpgradeChecks.FindByKey(published, UpgradeFixtures.Thing, new ContentKey(RowKey)));
        Assert.Equal(
            ContentFieldValue.OfNumber(ContentFieldKind.Int, NewValue),
            ValueOf(row, PublishFixtures.ValueField));
        Assert.Equal(
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1),
            ValueOf(row, PublishFixtures.LegacyField));
    }

    /// <summary>One field's value on an exported row, failing loudly when the row carries none.</summary>
    static ContentFieldValue ValueOf(ContentBundleRow row, string field)
    {
        IReadOnlyList<ContentFieldEdit> fields = row.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            if (string.Equals(fields[i].Name, field, StringComparison.Ordinal))
            {
                return fields[i].Value;
            }
        }

        Assert.Fail(FormattableString.Invariant($"The published row carries no field '{field}'."));
        return default;
    }
}
