using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using KhaozEngine.Tests.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The text conformance suite against <see cref="SqlServerContentAuthoringStore"/>, ENV GATED on
/// <c>KE_CATALOG_SQLSERVER</c> and enlisted in the serialized SQL Server catalog collection, because every
/// store here owns the whole <c>dbo.catalog_*</c> schema of the one test database.
/// <para>
/// <b>Every fact is overridden to carry <see cref="CatalogSqlServerFactAttribute"/>.</b> xUnit decides a skip
/// at DISCOVERY from the attribute on the method, and an inherited <c>[Fact]</c> would run here with no
/// instance to run against. The one-line overrides add nothing else.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerContentAuthoringTextConformanceTests : ContentAuthoringTextStoreConformance, IDisposable
{
    SqlServerCatalogDatabase? _database;
    int _packs;

    /// <inheritdoc />
    /// <remarks>
    /// The schema is DROPPED before each store, which is the same journey as a fresh database and the only one
    /// available, and each store writes a pack directory of its own.
    /// </remarks>
    protected override async Task<IContentTextAuthoringStore> OpenAsync(Func<DateTimeOffset>? clock = null)
    {
        Database.DropSchema();
        _packs++;
        var store = new SqlServerContentAuthoringStore(
            Database.ConnectionString,
            TextRegistry(),
            new FileSystemPackStore(Path.Combine(Database.Root, "pack-" + _packs.ToString(CultureInfo.InvariantCulture))),
            clock);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Through an AFTER INSERT trigger on <c>dbo.catalog_audit</c> that throws only for a row carrying a
    /// language, so the batch's row audit lands first and the text audit fails inside the same Serializable
    /// transaction.
    /// </remarks>
    protected override IDisposable ArmATextAuditFault(IContentTextAuthoringStore store)
    {
        Database.Execute(
            """
            CREATE TRIGGER catalog_audit_text_fault ON dbo.catalog_audit AFTER INSERT AS
            BEGIN
                IF EXISTS (SELECT 1 FROM inserted WHERE language_tag IS NOT NULL)
                    THROW 51000, 'text audit fault', 1;
            END;
            """);
        return new Disarm(this);
    }

    /// <inheritdoc />
    protected override IPackStore PackOf(IContentTextAuthoringStore store)
        => store is SqlServerContentAuthoringStore { PackStore: IPackStore pack }
            ? pack
            : throw new InvalidOperationException("Every store here is opened over a pack directory of its own.");

    /// <inheritdoc />
    public void Dispose() => _database?.Dispose();

    /// <summary>
    /// The instance, opened on first use. Lazily, because a skipped fact never runs and the fixture's
    /// constructor requires the variable to be set and the database to be named a test one.
    /// </summary>
    SqlServerCatalogDatabase Database => _database ??= new SqlServerCatalogDatabase();

    sealed class Disarm(SqlServerContentAuthoringTextConformanceTests owner) : IDisposable
    {
        public void Dispose() => owner.Database.Execute("DROP TRIGGER IF EXISTS catalog_audit_text_fault;");
    }

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text01_MixedApplyIsAtomicAndItsAuditCarriesTheLanguage()
        => base.Text01_MixedApplyIsAtomicAndItsAuditCarriesTheLanguage();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text02_SetThenRemoveKeepsTheIntroductionAndOnlyAnUndeclaredRemoveIsRefused()
        => base.Text02_SetThenRemoveKeepsTheIntroductionAndOnlyAnUndeclaredRemoveIsRefused();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text03_TheLastIntentWinsAndKeepsItsFirstOrdinal()
        => base.Text03_TheLastIntentWinsAndKeepsItsFirstOrdinal();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text04_ATextAuditFaultTakesTheWholeMixedBatchDown()
        => base.Text04_ATextAuditFaultTakesTheWholeMixedBatchDown();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text05_AFullValueIsHeldWhileItsAuditIsAbbreviatedOnAWholeCharacter()
        => base.Text05_AFullValueIsHeldWhileItsAuditIsAbbreviatedOnAWholeCharacter();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text06_EveryNewCommitIsCompleteAndAMixedPublishLandsTogether()
        => base.Text06_EveryNewCommitIsCompleteAndAMixedPublishLandsTogether();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text07_SetThenRemovePublishesAnEmptyLanguageAndRemovingTheLastValueKeepsIt()
        => base.Text07_SetThenRemovePublishesAnEmptyLanguageAndRemovingTheLastValueKeepsIt();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text08_FreezeRefusesAMovedOrEmptyBaseAndAFrozenDraftTakesNoWrite()
        => base.Text08_FreezeRefusesAMovedOrEmptyBaseAndAFrozenDraftTakesNoWrite();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text09_TheExpectedDraftDiscardKeepsARivalAndAuditsEveryCategory()
        => base.Text09_TheExpectedDraftDiscardKeepsARivalAndAuditsEveryCategory();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text10_AnOldDtoReconstructionIsRefusedByTheBackend()
        => base.Text10_AnOldDtoReconstructionIsRefusedByTheBackend();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text11_LegacyRollbackAndExportRefuseTextVersionsAndRunOnTextFreeOnes()
        => base.Text11_LegacyRollbackAndExportRefuseTextVersionsAndRunOnTextFreeOnes();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text12_ALegacyForkOfARowHoldingTextIsRefusedAndTheCompanionCopiesIt()
        => base.Text12_ALegacyForkOfARowHoldingTextIsRefusedAndTheCompanionCopiesIt();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text13_CompanionImportAndRollbackStayExplicitlyUnavailable()
        => base.Text13_CompanionImportAndRollbackStayExplicitlyUnavailable();

    /// <inheritdoc />
    [CatalogSqlServerFact]
    public override Task Text14_ATextPlanWhoseDraftChangedSinceItsFreezeIsRefusedAtCommit()
        => base.Text14_ATextPlanWhoseDraftChangedSinceItsFreezeIsRefusedAtCommit();
}
