using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using KhaozEngine.Tests.Gpu;
using Xunit;
using static KhaozEngine.Tests.Catalog.SqlServer.SqlServerTextFixtures;

namespace KhaozEngine.Tests.Catalog.SqlServer;

[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerStoreRefusalTests
{
    [Fact]
    public void SqlServerStoreNeverConstructsAMessageOnlyRefusal()
    {
        var legacy = typeof(ContentAuthoringException).GetConstructors()
            .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(ContentAuthoringRefusal)));
        Assert.DoesNotContain(RefusalConstructorFence.EngineCallers([typeof(SqlServerContentAuthoringStore).Assembly], legacy),
            caller => caller.StartsWith("KhaozEngine.Catalog.SqlServer.SqlServerContentAuthoringStore", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1205)]
    [InlineData(3960)]
    public void TransactionContentionKeepsItsProviderNumberAndLegacyToken(int number)
    {
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            SqlServerContentAuthoringStore.RefuseTransactionContentionForTests(number));
        Assert.Equal(ContentAuthoringRefusal.TransactionContended, refused.Refusal);
        Assert.Equal(number, refused.SqlErrorNumber);
        Assert.Equal("base-version-moved", refused.Reason);
        SqlServerContentAuthoringStore.RefuseTransactionContentionForTests(50000);
    }

    [CatalogSqlServerFact]
    public async Task MissingMetadataRefusesReadsUntilTheCatalogRowIsRestored()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await OpenAsync(database);
        database.Execute("SELECT * INTO dbo.host_saved_metadata FROM dbo.catalog_metadata; DELETE FROM dbo.catalog_metadata;");
        try
        {
            ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.GetActiveVersionAsync());
            Assert.Equal(ContentAuthoringRefusal.SchemaMetadataMissing, refused.Refusal);
            Assert.Equal("schema-mismatch", refused.Reason);
            database.Execute("INSERT INTO dbo.catalog_metadata SELECT * FROM dbo.host_saved_metadata;");
            Assert.Equal(0, await store.GetActiveVersionAsync());
        }
        finally { database.Execute("DROP TABLE dbo.host_saved_metadata;"); }
    }

    [CatalogSqlServerFact]
    public async Task AHostTriggerThatRemovesTheOpenedDraftRefusesAndRollsBackItsOwnWrite()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await OpenAsync(database);
        database.Execute("CREATE TABLE dbo.host_refusal_probe(value int NOT NULL);");
        try
        {
            database.Execute("""
                CREATE TRIGGER host_remove_draft ON dbo.catalog_draft AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.host_refusal_probe VALUES (1);
                    DELETE FROM dbo.catalog_draft WHERE draft_key IN (SELECT draft_key FROM inserted);
                END;
                """);
            await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
            ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
                store.ApplyEditsAsync([], Actor, Operator, ""));
            Assert.Equal(ContentAuthoringRefusal.DraftRowDisappeared, refused.Refusal);
            Assert.Equal("no-open-draft", refused.Reason);
            Assert.Null(await store.GetOpenDraftAsync());
            Assert.Empty(await store.ListAuditAsync(default, 0, 0, 500));
            Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.host_refusal_probe;"));
            database.Execute("DROP TRIGGER dbo.host_remove_draft;");
            Assert.Equal(0, (await store.ApplyEditsAsync([], Actor, Operator, "")).EditCount);
            Assert.NotNull(await store.GetOpenDraftAsync());
        }
        finally { database.Execute("DROP TRIGGER IF EXISTS dbo.host_remove_draft; DROP TABLE dbo.host_refusal_probe;"); }
    }

    [CatalogSqlServerFact]
    public async Task AnUnrecognizedStoredOperationNamesItsPersistedOrdinalAndReadsAfterRepair()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await OpenAsync(database);
        ContentDraft before = await store.ApplyEditsAsync([ContentEdit.Import(Item, 7, Sword, [Value(1)])], Actor, Operator, "");
        var audit = await store.ListAuditAsync(default, 0, 0, 500);
        database.Execute("ALTER TABLE dbo.catalog_draft_edit NOCHECK CONSTRAINT ck_catalog_draft_edit_operation; UPDATE dbo.catalog_draft_edit SET operation = 99;");
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.GetOpenDraftAsync());
        Assert.Equal(ContentAuthoringRefusal.StoredEditOperationUnknown, refused.Refusal);
        Assert.Equal("unknown-edit-operation", refused.Reason);
        Assert.Equal(Item, refused.Type);
        Assert.Equal(7, refused.Id);
        Assert.Equal(99, refused.ActualOperation);
        Assert.Equal(1L, refused.StoredEditOrdinal);
        Assert.Equal(99, database.Scalar("SELECT operation FROM dbo.catalog_draft_edit;"));
        Assert.Equal(audit, await store.ListAuditAsync(default, 0, 0, 500));
        database.Execute("UPDATE dbo.catalog_draft_edit SET operation = 1; ALTER TABLE dbo.catalog_draft_edit WITH CHECK CHECK CONSTRAINT ck_catalog_draft_edit_operation;");
        ContentDraft repaired = (await store.GetOpenDraftAsync())!;
        Assert.Equal((before.BaseVersion, before.OpenedBy, before.OpenedAtUtc, before.Note, before.FrozenForBaseVersion),
            (repaired.BaseVersion, repaired.OpenedBy, repaired.OpenedAtUtc, repaired.Note, repaired.FrozenForBaseVersion));
        Assert.True(before.TextState!.IsSameAs(repaired.TextState));
        ContentEdit original = Assert.Single(before.Changes.Edits);
        ContentEdit restored = Assert.Single(repaired.Changes.Edits);
        Assert.Equal((original.Type, original.DefinitionId, original.Key, original.Operation, original.RetirePolicy,
            original.ReplacementId, original.ForkKey, original.ForkFlagField, original.FamilyId, original.ImportedAsRetired),
            (restored.Type, restored.DefinitionId, restored.Key, restored.Operation, restored.RetirePolicy,
            restored.ReplacementId, restored.ForkKey, restored.ForkFlagField, restored.FamilyId, restored.ImportedAsRetired));
        Assert.Equal(original.Fields, restored.Fields);
    }

    [CatalogSqlServerFact]
    public async Task AChangedStoredScalarKindReportsItsClrTypesAndReadsAfterRepair()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await OpenAsync(database);
        database.Execute("""
            ALTER TABLE dbo.catalog_metadata DROP CONSTRAINT ck_catalog_metadata_active, df_catalog_metadata_active;
            ALTER TABLE dbo.catalog_metadata ALTER COLUMN active_version bigint NOT NULL;
            """);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.GetActiveVersionAsync());
        Assert.Equal(ContentAuthoringRefusal.StoredScalarTypeMismatch, refused.Refusal);
        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Equal(typeof(int), refused.ExpectedScalarType);
        Assert.Equal(typeof(long), refused.ActualScalarType);
        Assert.Equal(0, database.Scalar("SELECT CAST(active_version AS int) FROM dbo.catalog_metadata;"));
        database.Execute("""
            ALTER TABLE dbo.catalog_metadata ALTER COLUMN active_version int NOT NULL;
            ALTER TABLE dbo.catalog_metadata ADD CONSTRAINT ck_catalog_metadata_active CHECK (active_version >= 0);
            ALTER TABLE dbo.catalog_metadata ADD CONSTRAINT df_catalog_metadata_active DEFAULT 0 FOR active_version;
            """);
        Assert.Equal(0, await store.GetActiveVersionAsync());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
    }

    [CatalogSqlServerFact]
    public async Task ASuppressedDraftEditInsertReportsItsMissingIdentityAndRollsBack()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await OpenAsync(database);
        database.Execute("CREATE TABLE dbo.host_refusal_probe(value int NOT NULL);");
        try
        {
            database.Execute("""
                CREATE TRIGGER host_suppress_edit ON dbo.catalog_draft_edit INSTEAD OF INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.host_refusal_probe VALUES (1);
                END;
                """);
            await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
            ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
                store.ApplyEditsAsync([Add()], Actor, Operator, ""));
            Assert.Equal(ContentAuthoringRefusal.DraftEditIdentityMissing, refused.Refusal);
            Assert.Equal(Item, refused.Type);
            Assert.Equal(0, refused.Id);
            Assert.Equal("schema-mismatch", refused.Reason);
            Assert.Null(await store.GetOpenDraftAsync());
            Assert.Empty(await store.ListAuditAsync(default, 0, 0, 500));
            Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.host_refusal_probe;"));
            database.Execute("DROP TRIGGER dbo.host_suppress_edit;");
            Assert.Equal("sword", Assert.Single((await store.ApplyEditsAsync([Add()], Actor, Operator, "")).Changes.Edits).Key.ToString());
        }
        finally { database.Execute("DROP TRIGGER IF EXISTS dbo.host_suppress_edit; DROP TABLE dbo.host_refusal_probe;"); }
    }

    [CatalogSqlServerFact]
    public async Task ASuppressedFamilyInsertReportsItsMissingIdentityAndRollsBack()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await OpenAsync(database);
        var mark = await ((IContentIdPersistence)store).ReadHighWaterAsync(Item);
        database.Execute("CREATE TABLE dbo.host_refusal_probe(value int NOT NULL);");
        try
        {
            database.Execute("""
                CREATE TRIGGER host_suppress_family ON dbo.catalog_family INSTEAD OF INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.host_refusal_probe VALUES (1);
                END;
                """);
            await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
            ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
                store.CreateFamilyAsync(Item, "weapons", 16, Actor, Operator));
            Assert.Equal(ContentAuthoringRefusal.FamilyIdentityMissing, refused.Refusal);
            Assert.Equal(Item, refused.Type);
            Assert.Equal("weapons", refused.FamilyKey);
            Assert.Equal("schema-mismatch", refused.Reason);
            Assert.Empty(await store.ListFamiliesAsync(Item));
            Assert.Equal(mark, await ((IContentIdPersistence)store).ReadHighWaterAsync(Item));
            Assert.Empty(await store.ListAuditAsync(default, 0, 0, 500));
            Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.host_refusal_probe;"));
            database.Execute("DROP TRIGGER dbo.host_suppress_family;");
            Assert.Equal(16, (await store.CreateFamilyAsync(Item, "weapons", 16, Actor, Operator)).BlockSize);
        }
        finally { database.Execute("DROP TRIGGER IF EXISTS dbo.host_suppress_family; DROP TABLE dbo.host_refusal_probe;"); }
    }
}
