using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using KhaozEngine.Tests.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The conformance suite against <see cref="SqlServerContentAuthoringStore"/>, ENV GATED on
/// <c>KE_CATALOG_SQLSERVER</c>. CI has no SQL Server, so every fact here skips unless an operator points the
/// variable at a test database, and the SQLite leg carries the always-on coverage of the same twenty-three
/// facts.
/// <para>
/// <b>Every fact is overridden to carry <see cref="CatalogSqlServerFactAttribute"/>.</b> xUnit decides a skip
/// at DISCOVERY from the attribute on the method, and an inherited <c>[Fact]</c> would run here with no
/// instance to run against, so the one-line overrides below are what turn the inherited suite into a gated
/// one. They add nothing else.
/// </para>
/// <para>
/// The database is the isolation unit rather than a file, which is why <see cref="ResetToEmptyAsync"/> drops
/// the schema instead of opening a second one, and why the class enlists in the serialized collection every
/// other SQL Server catalog class is in.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerContentAuthoringStoreConformanceTests : ContentAuthoringStoreConformance, IDisposable
{
    SqlServerCatalogDatabase? _database;
    int _packs;

    /// <inheritdoc />
    protected override IContentAuthoringStore NewStore()
    {
        SqlServerCatalogDatabase database = Database;
        _packs++;
        return new SqlServerContentAuthoringStore(
            database.ConnectionString,
            Registry,
            new KhaozEngine.Catalog.FileSystemPackStore(
                System.IO.Path.Combine(database.Root, "pack-" + _packs.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The schema DROPPED and created again, which is the same journey as a second database and the only one
    /// available: a catalog store owns the whole <c>dbo.catalog_*</c> schema rather than a key prefix, so two
    /// stores on one instance would be two databases and a test that created one would need rights nothing
    /// else here needs.
    /// </remarks>
    protected override Task<IContentAuthoringStore> ResetToEmptyAsync()
    {
        Database.DropSchema();
        return OpenAsync();
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Through an AFTER INSERT trigger on <c>dbo.catalog_audit</c> that throws.</b> It fails the insert
    /// inside the edit's own transaction, which is where a real failure would land, so what the store does
    /// next is the behaviour under test rather than a simulation of it.
    /// </remarks>
    protected override IDisposable ArmAnAuditWriteFault(IContentAuthoringStore store)
    {
        Database.Execute(
            """
            CREATE TRIGGER catalog_audit_fault ON dbo.catalog_audit AFTER INSERT AS
            BEGIN
                THROW 51000, 'audit fault', 1;
            END;
            """);
        return new Disarm(this);
    }

    /// <inheritdoc />
    /// <remarks>
    /// One statement on a pooled connection, so both halves come out of one read. The publish holds a
    /// serializable transaction, so this read either blocks until the commit or times out, and a timeout is
    /// reported as no look at all: a lock is not an inconsistency.
    /// </remarks>
    protected override async Task<CatalogPointerLook?> LookAsync(IContentAuthoringStore store, int versionNumber)
    {
        try
        {
            await using var connection = new SqlConnection(Database.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using SqlCommand command = connection.CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText =
                """
                SELECT (SELECT active_version FROM dbo.catalog_metadata WHERE metadata_key = 1),
                       (SELECT COUNT(*) FROM dbo.catalog_version WHERE version_number = @version);
                """;
            command.Parameters.AddWithValue("@version", versionNumber);
            await using SqlDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                return null;
            }

            return new CatalogPointerLook(reader.GetInt32(0), reader.GetInt32(1) == 1);
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    protected override KhaozEngine.Catalog.IPackStore PackOf(IContentAuthoringStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store is SqlServerContentAuthoringStore { PackStore: KhaozEngine.Catalog.IPackStore pack }
            ? pack
            : throw new InvalidOperationException(
                "This subclass opens every store over a pack directory of its own, so one without a pack target did not come from here.");
    }

    /// <inheritdoc />
    public void Dispose() => _database?.Dispose();

    /// <summary>
    /// The instance, opened on first use. Lazily, because a skipped fact never runs and the fixture's
    /// constructor requires the variable to be set and the database to be named a test one.
    /// </summary>
    SqlServerCatalogDatabase Database => _database ??= new SqlServerCatalogDatabase();

    sealed class Disarm(SqlServerContentAuthoringStoreConformanceTests owner) : IDisposable
    {
        public void Dispose() => owner.Database.Execute("DROP TRIGGER IF EXISTS catalog_audit_fault;");
    }

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact01_AutoCreateOnAnEmptyStoreCreatesTheSchemaAndReportsVersionOne()
        => base.Fact01_AutoCreateOnAnEmptyStoreCreatesTheSchemaAndReportsVersionOne();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact02_ValidateOnlyOnAnEmptyDatabaseRefusesAndNamesTheMigration()
        => base.Fact02_ValidateOnlyOnAnEmptyDatabaseRefusesAndNamesTheMigration();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact03_ValidateOnlyOnACorrectSchemaSucceeds()
        => base.Fact03_ValidateOnlyOnACorrectSchemaSucceeds();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact04_AKeyDifferingOnlyInCaseIsADifferentKey()
        => base.Fact04_AKeyDifferingOnlyInCaseIsADifferentKey();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact05_TwoAddsOfOneKeyCollideOnTheUniqueIndex()
        => base.Fact05_TwoAddsOfOneKeyCollideOnTheUniqueIndex();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact06_AnUpdateMergesFieldsRatherThanReplacingTheRow()
        => base.Fact06_AnUpdateMergesFieldsRatherThanReplacingTheRow();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact07_PublishAssignsOneThenTwoAndNeverSkips()
        => base.Fact07_PublishAssignsOneThenTwoAndNeverSkips();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact08_APublishAgainstAStaleBaseIsRefusedAndWritesNothing()
        => base.Fact08_APublishAgainstAStaleBaseIsRefusedAndWritesNothing();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact09_AnUntouchedRowKeepsItsValidFromVersion()
        => base.Fact09_AnUntouchedRowKeepsItsValidFromVersion();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact10_TheLiveSetAtAnOldVersionExcludesARowAddedLater()
        => base.Fact10_TheLiveSetAtAnOldVersionExcludesARowAddedLater();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact11_ARetireWritesASuccessorRowAndExactlyOneRule()
        => base.Fact11_ARetireWritesASuccessorRowAndExactlyOneRule();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact12_ARemapRuleCannotBeUpdatedOrDeletedThroughTheApi()
        => base.Fact12_ARemapRuleCannotBeUpdatedOrDeletedThroughTheApi();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact13_AllocationReservesBeforeItIssues()
        => base.Fact13_AllocationReservesBeforeItIssues();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact14_NoIdIsEverIssuedTwice()
        => base.Fact14_NoIdIsEverIssuedTwice();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact15_AFamilyStaysInItsBlockAndReservesASecondWhenFull()
        => base.Fact15_AFamilyStaysInItsBlockAndReservesASecondWhenFull();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact16_EveryAuditRowCarriesABeforeAndAnAfter()
        => base.Fact16_EveryAuditRowCarriesABeforeAndAnAfter();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact17_AnAuditInsertFailureRollsBackTheEdit()
        => base.Fact17_AnAuditInsertFailureRollsBackTheEdit();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task AStoreLevelAuditRow_NamesNoRowAndCarriesItsOwnNumber()
        => base.AStoreLevelAuditRow_NamesNoRowAndCarriesItsOwnNumber();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact18_AnImportIntoAnEmptyDatabaseKeepsTheSourceIds()
        => base.Fact18_AnImportIntoAnEmptyDatabaseKeepsTheSourceIds();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact19_AnImportIntoANonEmptyDatabaseIsRefusedAndWritesNothing()
        => base.Fact19_AnImportIntoANonEmptyDatabaseIsRefusedAndWritesNothing();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact20_AnExportAtAVersionImportsIntoAnEmptyStoreUnchanged()
        => base.Fact20_AnExportAtAVersionImportsIntoAnEmptyStoreUnchanged();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact21_APublishRefusedByTheValidatorLeavesTheDraftIntact()
        => base.Fact21_APublishRefusedByTheValidatorLeavesTheDraftIntact();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact22_TheActivePointerAndTheVersionRowCommitTogether()
        => base.Fact22_TheActivePointerAndTheVersionRowCommitTogether();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact23_APlainAllocationNeverLandsInsideAFamilyBlock()
        => base.Fact23_APlainAllocationNeverLandsInsideAFamilyBlock();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact24_AnImportRefusedWhileItStagesLeavesNothingStaged()
        => base.Fact24_AnImportRefusedWhileItStagesLeavesNothingStaged();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact25_ADraftEditArrivingWhileAPublishIsInFlightIsRefused()
        => base.Fact25_ADraftEditArrivingWhileAPublishIsInFlightIsRefused();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Fact26_AStoreLevelChangeWhoseAuditWriteFailsIsNotMade()
        => base.Fact26_AStoreLevelChangeWhoseAuditWriteFailsIsNotMade();
}
