using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// Two STORE INSTANCES over one database file recording the SAME upgrade id at the same time, which is the
/// shape a deploy that ran twice and two replicas booting together both have.
/// <para>
/// <b><c>RecordUpgradeAsync</c> is a check then an insert</b>, and the two have to be one decision. One store
/// holds one connection behind a semaphore, so its own callers cannot interleave, and two stores on one file
/// are two connections that the semaphore says nothing about. What serialises them is the transaction:
/// <c>SqliteConnection.BeginTransaction()</c> in Microsoft.Data.Sqlite 10.0.9 issues <c>BEGIN IMMEDIATE</c>,
/// so the write lock is taken at the BEGIN rather than at the first write and the second recorder cannot read
/// an empty ledger and then insert into a table the first one has already written.
/// </para>
/// <para>
/// Recording an id the ledger already holds is a NO-OP by contract, so the loser writes nothing and throws
/// nothing. Exactly one ledger row and exactly one audit row is the assertion, because a second audit row
/// with no ledger row behind it would tell an operator the upgrade was recorded twice.
/// </para>
/// <para>
/// It LOOPS, because a race that shows one time in ten is the one that reaches production.
/// </para>
/// </summary>
public sealed class SqliteUpgradeLedgerRaceTests
{
    const string Actor = "sqlite-ledger-race";
    const string Operator = "oid:tests";
    const string UpgradeId = "shipped-in-the-bundle";

    /// <summary>How many times the race is run.</summary>
    const int Iterations = 20;

    [Fact]
    public async Task TwoStoresRecordingOneUpgradeIdAtOnceWriteExactlyOneRow()
    {
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            using var database = new TemporaryCatalogDatabase();
            ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);

            using var first = new SqliteContentAuthoringStore(
                database.ConnectionString, registry, database.Pack());
            await first.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            using var second = new SqliteContentAuthoringStore(
                database.ConnectionString, registry, database.Pack());
            await second.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

            var stamp = new ContentUpgradeStamp(UpgradeId, 1);

            // Through Task.Run on both sides: the first call would otherwise run to completion before the
            // second one started, and the test would prove nothing about interleaving.
            await Task.WhenAll(
                Task.Run(() => first.RecordUpgradeAsync(
                    stamp, ContentUpgradeDisposition.Baseline, Actor, Operator)),
                Task.Run(() => second.RecordUpgradeAsync(
                    stamp, ContentUpgradeDisposition.Baseline, Actor, Operator)));

            ContentUpgradeRecord record = Assert.Single(await first.ListUpgradesAsync());
            Assert.Equal(UpgradeId, record.Id);
            Assert.Equal(ContentUpgradeDisposition.Baseline, record.Disposition);
            Assert.Single(await UpgradeAuditAsync(first));
        }
    }

    /// <summary>Every <c>content-upgrade</c> audit row the store holds, which is one per ledger row.</summary>
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
}
