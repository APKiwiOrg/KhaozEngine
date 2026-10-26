using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
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
        var packs = new ObservedFileSystemPacks(files.Pack());
        using ContentAuthoringStoreLease lease = await UpgradeLifetimeCatalog.CreateAsync(files, registry, sqlite, packs);
        IContentAuthoringStore inner = lease.Store;
        // A real orphan makes prune coverage observable, rather than merely counting a sweep callback.
        KeyValuePair<string, string>[] orphan = [new("test.orphan", "unused")];
        string orphanHash = ContentTextChunkCodec.Hash("en", orphan);
        await packs.PutAsync(orphanHash, ContentTextChunkCodec.Encode("en", orphan));
        var scenario = new UpgradePublishScenario(inner, registry, sweepFails);
        (ContentUpgradeReport olderReport, ContentUpgradeReport nextReport) = await scenario.RunAsync();
        UpgradePublishSequence sequence = scenario.Sequence;
        Assert.Equal(sweepFails, scenario.Older.SweepFailureInjected);
        Assert.Equal(4, sequence.Phase);
        Assert.Equal(4, scenario.Older.DraftReads);
        Assert.Equal(3, scenario.Next.DraftReads);
        Assert.False(await packs.ExistsAsync(orphanHash));
        Assert.True(packs.Deletions > 0);
        Assert.Equal(sweepFails ? 2 : 3, packs.Enumerations);
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
