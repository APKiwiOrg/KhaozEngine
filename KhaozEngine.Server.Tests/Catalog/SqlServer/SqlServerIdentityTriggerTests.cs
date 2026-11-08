using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;
using static KhaozEngine.Tests.Catalog.SqlServer.SqlServerTextFixtures;

namespace KhaozEngine.Tests.Catalog.SqlServer;

[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerIdentityTriggerTests
{
    [CatalogSqlServerFact]
    public async Task AFamilyTriggerResultCannotRedirectTheNewFamilysBlock()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await OpenAsync(database);
        ContentFamily original = await store.CreateFamilyAsync(Item, "weapons", 16, Actor, Operator);
        database.Execute("CREATE TABLE dbo.host_identity_probe(value int NOT NULL);");
        try
        {
            // A host trigger's result arrives before the insert batch's own result.
            database.Execute("""
                CREATE TRIGGER host_spoof_family_identity ON dbo.catalog_family AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.host_identity_probe VALUES (1);
                    SELECT MIN(family_id) FROM dbo.catalog_family;
                END;
                """);
            ContentFamily created = await store.CreateFamilyAsync(Item, "armour", 16, Actor, Operator);
            Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.host_identity_probe;"));
            Assert.Equal("armour", created.FamilyKey);
            Assert.NotEqual(original.FamilyId, created.FamilyId);
            ContentFamilyBlock block = Assert.Single(created.Blocks);
            Assert.NotEqual(Assert.Single(original.Blocks).BaseId, block.BaseId);
            ContentFamily retained = (await store.ListFamiliesAsync(Item)).Single(family => family.FamilyId == original.FamilyId);
            Assert.Equal(original.Blocks, retained.Blocks);
        }
        finally { database.Execute("DROP TRIGGER IF EXISTS dbo.host_spoof_family_identity; DROP TABLE dbo.host_identity_probe;"); }
    }

    [CatalogSqlServerFact]
    public async Task ADraftEditTriggerResultCannotAttachFieldsToTheStandingEdit()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await OpenAsync(database);
        await store.ApplyEditsAsync([Add(value: 1)], Actor, Operator, "");
        database.Execute("CREATE TABLE dbo.host_identity_probe(value int NOT NULL);");
        try
        {
            database.Execute("""
                CREATE TRIGGER host_spoof_edit_identity ON dbo.catalog_draft_edit AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    INSERT INTO dbo.host_identity_probe VALUES (1);
                    SELECT MIN(edit_ordinal) FROM dbo.catalog_draft_edit;
                END;
                """);
            ContentDraft draft = await store.ApplyEditsAsync([Add("shield", 2)], Actor, Operator, "");
            Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.host_identity_probe;"));
            Assert.Equal(2, draft.Changes.Edits.Count);
            Assert.Equal(new[] { Value(1) }, draft.Changes.Edits.Single(edit => edit.Key.Equals(Sword)).Fields);
            Assert.Equal(new[] { Value(2) }, draft.Changes.Edits.Single(edit => edit.Key.Equals(new ContentKey("shield"))).Fields);
        }
        finally { database.Execute("DROP TRIGGER IF EXISTS dbo.host_spoof_edit_identity; DROP TABLE dbo.host_identity_probe;"); }
    }
}
