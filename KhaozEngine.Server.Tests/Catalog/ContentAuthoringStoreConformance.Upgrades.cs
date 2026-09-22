using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The content upgrade LEDGER half of the suite: the seam schema version 2 adds, proved the same way on the
/// in-memory reference, SQLite and SQL Server.
/// <para>
/// <b>The fact that matters most is the atomic one.</b> An applied row is written inside the publish commit,
/// so a duplicate upgrade id has to take the whole publish down with it: the active version, the version
/// list, the rows and the draft all stand exactly where they were. A store that wrote the version and then
/// refused the ledger row would tell the next run that the upgrade never happened, which is the
/// defaults-reapplied failure the ledger exists to prevent.
/// </para>
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>A store with nothing recorded holds an EMPTY ledger, which is what a fresh install reads.</summary>
    [Fact]
    public virtual async Task ANewStoreHoldsAnEmptyUpgradeLedger()
    {
        IContentAuthoringStore store = await OpenAsync();

        Assert.Empty(await Ledger(store).ListUpgradesAsync());
    }

    /// <summary>
    /// A publish carrying a stamp writes EXACTLY ONE applied row, and the row names the version the publish
    /// assigned. The version number is the whole point: it is how an operator reads which upgrade produced
    /// which version without replaying the audit.
    /// </summary>
    [Fact]
    public virtual async Task AStampedPublishWritesOneAppliedRowCarryingThePublishedVersion()
    {
        IContentAuthoringStore store = await OpenAsync();
        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        ContentPublishResult published = await store.PublishAsync(Stamped(0, "harvest-profiles", 4));

        ContentUpgradeRecord record = Assert.Single(await Ledger(store).ListUpgradesAsync());
        Assert.Equal("harvest-profiles", record.Id);
        Assert.Equal(4, record.Order);
        Assert.Equal(ContentUpgradeDisposition.Applied, record.Disposition);
        Assert.Equal(published.VersionNumber, record.VersionNumber);
        Assert.Equal(CatalogFixtures.Actor, record.Actor);
        Assert.Equal(CatalogFixtures.Operator, record.Operator);

        // One audit row too, and one only. A ledger row with no audit entry against it is the same defect as
        // a content edit with none.
        ContentAuditEntry audit = Assert.Single(await UpgradeAuditAsync(store));
        Assert.Equal(ContentUpgradeDispositions.Applied, audit.FieldName);
        Assert.Equal("harvest-profiles", audit.AfterValue);
        Assert.Equal(published.VersionNumber, audit.VersionNumber);
    }

    /// <summary>
    /// An ordinary publish writes NO ledger row, so a catalog nothing has upgraded reads as one nothing has
    /// upgraded rather than as one that has quietly adopted every version it ever published.
    /// </summary>
    [Fact]
    public virtual async Task AnUnstampedPublishWritesNoLedgerRow()
    {
        IContentAuthoringStore store = await OpenAsync();

        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        Assert.Empty(await Ledger(store).ListUpgradesAsync());
        Assert.Empty(await UpgradeAuditAsync(store));
    }

    /// <summary>
    /// A second publish of ONE upgrade id is refused, and the refusal takes the whole commit with it. This is
    /// the concurrency guarantee: two runners applying one upgrade are one published version and one refusal,
    /// never two versions of the same edits.
    /// </summary>
    [Fact]
    public virtual async Task ASecondPublishOfOneUpgradeIdIsRefusedAndChangesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await store.PublishAsync(Stamped(0, "harvest-profiles", 4));

        await ApplyAsync(store, ContentEdit.Add(Thing, new ContentKey("two"), CatalogFixtures.Fields(22)));
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(Stamped(1, "harvest-profiles", 4)));

        Assert.Equal(ContentAuthoringException.UpgradeAlreadyRecordedReason, refused.Reason);
        Assert.Contains("harvest-profiles", refused.Message, StringComparison.Ordinal);

        // Nothing moved: the pointer, the version list, the ledger and the rows are where the first publish
        // left them, and the second publish's draft is still open for its caller to deal with.
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Single(await store.ListVersionsAsync());
        Assert.Single(await Ledger(store).ListUpgradesAsync());
        Assert.Equal(1, (await RowsAsync(store)).Total);
        Assert.Equal(1, (await DraftAsync(store)).EditCount);
    }

    /// <summary>
    /// The two dispositions a runner records rather than publishes, each carrying the ACTIVE version: adopted
    /// for content the planner found already present, and baseline for a catalog seeded from a bundle that
    /// carried it.
    /// </summary>
    [Fact]
    public virtual async Task AnAdoptedAndABaselineUpgradeAreRecordedAgainstTheActiveVersion()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        IContentUpgradeLedger ledger = Ledger(store);

        await ledger.RecordUpgradeAsync(
            new ContentUpgradeStamp("already-there", 1),
            ContentUpgradeDisposition.Adopted,
            CatalogFixtures.Actor,
            CatalogFixtures.Operator);
        await ledger.RecordUpgradeAsync(
            new ContentUpgradeStamp("shipped-in-the-bundle", 2),
            ContentUpgradeDisposition.Baseline,
            CatalogFixtures.Actor,
            CatalogFixtures.Operator);

        IReadOnlyList<ContentUpgradeRecord> records = await ledger.ListUpgradesAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(ContentUpgradeDisposition.Adopted, records[0].Disposition);
        Assert.Equal(ContentUpgradeDisposition.Baseline, records[1].Disposition);
        Assert.Equal(1, records[0].VersionNumber);
        Assert.Equal(1, records[1].VersionNumber);
        Assert.Equal(2, (await UpgradeAuditAsync(store)).Count);
    }

    /// <summary>
    /// Recording an id the ledger already holds is a NO-OP, so a crash between a seed and its baseline record
    /// is resolved by running the record again rather than by an operator editing a table.
    /// </summary>
    [Fact]
    public virtual async Task RecordingAnUpgradeTheLedgerAlreadyHoldsIsANoOp()
    {
        IContentAuthoringStore store = await OpenAsync();
        IContentUpgradeLedger ledger = Ledger(store);
        var stamp = new ContentUpgradeStamp("shipped-in-the-bundle", 1);
        await ledger.RecordUpgradeAsync(
            stamp, ContentUpgradeDisposition.Baseline, CatalogFixtures.Actor, CatalogFixtures.Operator);

        await ledger.RecordUpgradeAsync(
            stamp, ContentUpgradeDisposition.Adopted, CatalogFixtures.Actor, CatalogFixtures.Operator);

        // The first row stands, disposition and all: the second call recorded nothing rather than overwriting
        // how the catalog really came to hold the content.
        ContentUpgradeRecord record = Assert.Single(await ledger.ListUpgradesAsync());
        Assert.Equal(ContentUpgradeDisposition.Baseline, record.Disposition);
        Assert.Single(await UpgradeAuditAsync(store));
    }

    /// <summary>
    /// <see cref="ContentUpgradeDisposition.Applied"/> is REFUSED here. An applied row says a version was
    /// published, and only the publish commit can say that truthfully.
    /// </summary>
    [Fact]
    public virtual async Task RecordingAnAppliedUpgradeIsRefused()
    {
        IContentAuthoringStore store = await OpenAsync();
        IContentUpgradeLedger ledger = Ledger(store);

        await Assert.ThrowsAsync<ArgumentException>(
            () => ledger.RecordUpgradeAsync(
                new ContentUpgradeStamp("harvest-profiles", 1),
                ContentUpgradeDisposition.Applied,
                CatalogFixtures.Actor,
                CatalogFixtures.Operator));

        Assert.Empty(await ledger.ListUpgradesAsync());
    }

    /// <summary>
    /// The ledger reads ASCENDING by order and then by id, which is the order the upgrades ran in and the
    /// order the runner walks the pending ones in.
    /// </summary>
    [Fact]
    public virtual async Task TheLedgerReadsAscendingByOrderThenId()
    {
        IContentAuthoringStore store = await OpenAsync();
        IContentUpgradeLedger ledger = Ledger(store);

        foreach ((string id, int order) in new[] { ("second", 2), ("charlie", 1), ("alpha", 1) })
        {
            await ledger.RecordUpgradeAsync(
                new ContentUpgradeStamp(id, order),
                ContentUpgradeDisposition.Baseline,
                CatalogFixtures.Actor,
                CatalogFixtures.Operator);
        }

        IReadOnlyList<ContentUpgradeRecord> records = await ledger.ListUpgradesAsync();

        Assert.Equal(["alpha", "charlie", "second"], Ids(records));
    }

    /// <summary>
    /// Two recorders of ONE upgrade id at the same time write exactly one ledger row and one audit row, and
    /// neither of them throws.
    /// <para>
    /// <b>The record is a check then an insert, and the two have to be one decision.</b> Recording an id the
    /// ledger already holds is a no-op by contract, so the loser has nothing to report, but it may not be a
    /// deadlock victim either: a caller that asked for a no-op and got a provider error cannot tell the
    /// difference between "already there" and "the write failed". On SQL Server that is what the update range
    /// lock on the existence read is for, because two Serializable readers of an absent key would otherwise
    /// both hold a shared range lock and deadlock converting it.
    /// </para>
    /// </summary>
    [Fact]
    public virtual async Task TwoRecordersOfOneUpgradeIdWriteExactlyOneRow()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        IContentUpgradeLedger ledger = Ledger(store);
        var stamp = new ContentUpgradeStamp("shipped-in-the-bundle", 1);

        // Through Task.Run on both sides: the in-memory store answers synchronously, so a bare call would run
        // the first record to completion before the second one started.
        await Task.WhenAll(
            Task.Run(() => ledger.RecordUpgradeAsync(
                stamp, ContentUpgradeDisposition.Baseline, CatalogFixtures.Actor, CatalogFixtures.Operator)),
            Task.Run(() => ledger.RecordUpgradeAsync(
                stamp, ContentUpgradeDisposition.Adopted, CatalogFixtures.Actor, CatalogFixtures.Operator)));

        Assert.Single(await ledger.ListUpgradesAsync());
        Assert.Single(await UpgradeAuditAsync(store));
    }

    /// <summary>A publish request carrying an upgrade stamp, which is the only way an applied row is written.</summary>
    /// <param name="expectedBaseVersion">The base version the caller believes it is publishing onto.</param>
    /// <param name="upgradeId">The upgrade's stable id.</param>
    /// <param name="order">The upgrade's order.</param>
    protected static ContentPublishRequest Stamped(int expectedBaseVersion, string upgradeId, int order)
        => Request(expectedBaseVersion) with { Upgrade = new ContentUpgradeStamp(upgradeId, order) };

    /// <summary>Every <c>content-upgrade</c> audit row the store holds, which is one per ledger row.</summary>
    /// <param name="store">The store.</param>
    static async Task<IReadOnlyList<ContentAuditEntry>> UpgradeAuditAsync(IContentAuthoringStore store)
    {
        IReadOnlyList<ContentAuditEntry> all = await store.ListAuditAsync(default, 0, 0, 500);
        var rows = new List<ContentAuditEntry>();
        for (int i = 0; i < all.Count; i++)
        {
            if (string.Equals(all[i].Action, ContentAuditActions.ContentUpgrade, StringComparison.Ordinal))
            {
                rows.Add(all[i]);
            }
        }

        return rows;
    }

    /// <summary>Every id in a ledger read, in read order, which is what an ordering assertion reads.</summary>
    /// <param name="records">The ledger read.</param>
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
