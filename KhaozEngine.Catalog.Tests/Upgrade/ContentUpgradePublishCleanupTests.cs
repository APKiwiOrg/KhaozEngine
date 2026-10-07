using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>A completed publish cannot release the next upgrade's frozen draft while it finishes sweeping.</summary>
public sealed class ContentUpgradePublishCleanupTests
{
    /// <summary>The second runner freezes its plan after the first commit and before the first cleanup.</summary>
    /// <param name="sqlite">Whether to exercise SQLite rather than the reference store.</param>
    /// <param name="sweepFails">Whether the first publish reports a failure after its commit.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AnOlderPublishLeavesTheNextRunnersFrozenDraftOwnedByItsPublish(bool sqlite, bool sweepFails)
    {
        using var files = new TemporaryCatalogDatabase();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        IContentAuthoringStore inner = sqlite
            ? new SqliteContentAuthoringStore(files.ConnectionString, registry, files.Pack())
            : new InMemoryContentAuthoringStore(registry, files.Pack());
        using var lease = new ContentAuthoringStoreLease(inner);
        await inner.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await inner.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
            UpgradeFixtures.Actor, UpgradeFixtures.Operator, "seed the older catalog");
        await inner.PublishAsync(PublishFixtures.Request(0));

        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));
        var set = new ContentUpgradeSet(
            UpgradeFixtures.Adds(UpgradeHarness.FirstId, 1, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "new_row")),
            UpgradeFixtures.Adds(UpgradeHarness.SecondId, 2, target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "second_new_row")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sequence = new UpgradePublishSequence();
        var older = new InterleavedUpgradePublishStore(inner, registry, sequence, older: true, sweepFails);
        var next = new InterleavedUpgradePublishStore(inner, registry, sequence, older: false, sweepFails: false);

        Task<ContentUpgradeReport> olderRun = ContentUpgradeRunner.RunAsync(
            older, registry, set, UpgradeFixtures.Apply(), deadline.Token);
        await sequence.FirstCommitted.Task.WaitAsync(deadline.Token);
        ContentUpgradeReport nextReport;
        try
        {
            nextReport = await ContentUpgradeRunner.RunAsync(next, registry, set, UpgradeFixtures.Apply(), deadline.Token);
        }
        finally
        {
            sequence.SecondFinished.TrySetResult();
        }

        ContentUpgradeReport olderReport = await olderRun;
        Assert.True(nextReport.Success, string.Join(" | ", nextReport.Lines));
        Assert.True(olderReport.Success, string.Join(" | ", olderReport.Lines));
        Assert.NotNull(sequence.DraftAfterOlderPublish);
        Assert.True(sequence.DraftAfterOlderPublish.IsFrozen, "cleanup of version 2 released version 3's draft");
        Assert.Equal(2, sequence.DraftAfterOlderPublish.BaseVersion);
        Assert.Equal(3, (await inner.ListVersionsAsync()).Count);
        IReadOnlyList<ContentUpgradeRecord> records = await ((IContentUpgradeLedger)inner).ListUpgradesAsync();
        Assert.Collection(records,
            first => { Assert.Equal(UpgradeHarness.FirstId, first.Id); Assert.Equal(2, first.VersionNumber); },
            second => { Assert.Equal(UpgradeHarness.SecondId, second.Id); Assert.Equal(3, second.VersionNumber); });
        Assert.Null(await inner.GetOpenDraftAsync());
        ContentRowPage rows = await inner.ListRowsAsync(UpgradeFixtures.Other, 0, null, false, 0, 50);
        Assert.Equal(2, rows.Total);
        Assert.Collection(rows.Rows,
            first => { Assert.Equal(1, first.Id); Assert.Equal("new_row", first.Key.ToString()); },
            second => { Assert.Equal(2, second.Id); Assert.Equal("second_new_row", second.Key.ToString()); });
    }
}
