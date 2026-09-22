using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The version 1 to version 2 migration on SQL Server, driven against a POPULATED version 1 database: the
/// embedded <c>CatalogSchemaV1.sql</c> created as it shipped, with two published versions, the temporal
/// history they left, an open draft, a pin and audit rows already in it.
/// <para>
/// <b>The version 1 script is the EMBEDDED one rather than a copy</b>, which is the difference from the SQLite
/// leg: this provider ships both scripts as resources, so the file a test builds from is the same file an
/// operator would have run. It is never regenerated from version 2.
/// </para>
/// <para>
/// ENV GATED on <c>KE_CATALOG_SQLSERVER</c> like every other class here. CI has no SQL Server, and the SQLite
/// leg carries the always-on coverage of the same migration.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogSchemaMigrationTests
{
    /// <summary>The upgrade a migrated database is stamped with, to prove the ledger works after the move.</summary>
    const string UpgradeId = "harvest-profiles";

    const string Actor = "sqlserver-migration-tests";
    const string Operator = "oid:tests";

    /// <summary>A fabricated manifest digest, which is 64 lower hex characters and nothing more.</summary>
    const string FirstHash = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>The second version's digest, different from the first so a mix-up is visible.</summary>
    const string SecondHash = "2222222222222222222222222222222222222222222222222222222222222222";

    /// <summary>The store epoch the fixture seeds, which the migration must leave exactly as it found it.</summary>
    const string Epoch = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// A version 1 database with content in it: two published versions, three row revisions (one of them
    /// closed by the second version), the chunk rows of both versions, an open draft holding one edit, an
    /// operator pin on version 1, and three audit rows.
    /// <para>
    /// The identity columns are left to the database and the draft edit's field row is selected back off the
    /// row it belongs to, which is how a script inserts into a table whose key it does not choose.
    /// </para>
    /// </summary>
    const string PopulateVersionOne = """
        INSERT INTO dbo.catalog_type(
            type_id, type_key, chunk_slots, default_visibility, max_definition_id, first_seen_version)
        VALUES (1024, N'thing', 256, 0, NULL, 0);

        INSERT INTO dbo.catalog_version(
            version_number, server_manifest_hash, client_manifest_hash, minimum_server_build,
            minimum_client_build, format_generation, base_version, published_by, note, published_at_utc)
        VALUES (1, N'1111111111111111111111111111111111111111111111111111111111111111',
                   N'1111111111111111111111111111111111111111111111111111111111111111',
                   0, 0, 1, 0, N'seed', N'first', '2026-01-01T00:00:00+00:00'),
               (2, N'2222222222222222222222222222222222222222222222222222222222222222',
                   N'2222222222222222222222222222222222222222222222222222222222222222',
                   0, 0, 1, 1, N'seed', N'second', '2026-01-02T00:00:00+00:00');

        INSERT INTO dbo.catalog_row(
            type_id, definition_id, valid_from_version, replaced_in_version, content_key, parent_id,
            family_id, retired)
        VALUES (1024, 1, 1, 2, N'one', 0, NULL, 0),
               (1024, 1, 2, NULL, N'one', 0, NULL, 0),
               (1024, 2, 1, NULL, N'two', 0, NULL, 0);

        INSERT INTO dbo.catalog_row_field(
            type_id, definition_id, valid_from_version, field_name, field_kind, int_value, text_value,
            blob_value)
        VALUES (1024, 1, 1, N'value', 0, 11, NULL, NULL),
               (1024, 1, 2, N'value', 0, 12, NULL, NULL),
               (1024, 2, 1, N'value', 0, 22, NULL, NULL);

        INSERT INTO dbo.catalog_chunk(
            version_number, type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes, stored_bytes,
            visibility)
        VALUES (1, 1024, 0, N'1111111111111111111111111111111111111111111111111111111111111111', 2, 64, 48, 0),
               (1, 1024, 0, N'1111111111111111111111111111111111111111111111111111111111111111', 2, 64, 48, 1),
               (2, 1024, 0, N'2222222222222222222222222222222222222222222222222222222222222222', 2, 64, 48, 0),
               (2, 1024, 0, N'2222222222222222222222222222222222222222222222222222222222222222', 2, 64, 48, 1);

        INSERT INTO dbo.catalog_id_high_water(type_id, reserved_through, issued_through)
        VALUES (1024, 2, 2);

        INSERT INTO dbo.catalog_draft(
            draft_key, base_version, opened_by, opened_at_utc, note, frozen_for_base_version)
        VALUES (1, 2, N'operator', '2026-01-03T00:00:00+00:00', N'an open draft', NULL);

        INSERT INTO dbo.catalog_draft_edit(
            type_id, definition_id, content_key, operation, retire_policy, replacement_id, fork_key,
            fork_flag_field, family_id, imported_retired, edited_by, edited_at_utc)
        VALUES (1024, 2, N'two', 2, 0, 0, NULL, NULL, NULL, 0, N'operator', '2026-01-03T00:00:00+00:00');

        INSERT INTO dbo.catalog_draft_edit_field(
            edit_ordinal, field_name, field_kind, int_value, text_value, blob_value)
        SELECT edit_ordinal, N'value', 0, 33, NULL, NULL FROM dbo.catalog_draft_edit;

        INSERT INTO dbo.catalog_audit(
            occurred_at_utc, actor, [operator], action, type_id, definition_id, content_key, field_name,
            before_value, after_value, version_number, note)
        VALUES ('2026-01-01T00:00:00+00:00', N'seed', N'', N'publish', 1024, 1, N'one', N'value',
                NULL, N'11', 1, N'first'),
               ('2026-01-02T00:00:00+00:00', N'seed', N'', N'publish', 1024, 1, N'one', N'value',
                N'11', N'12', 2, N'second'),
               ('2026-01-03T00:00:00+00:00', N'operator', N'', N'draft-edit', 1024, 2, N'two', N'value',
                NULL, N'33', 0, N'');

        UPDATE dbo.catalog_metadata
        SET store_epoch = N'0123456789abcdef0123456789abcdef', active_version = 2, pinned_version = 1
        WHERE metadata_key = 1;
        """;

    static ContentTypeRegistry Registry() => CatalogFixtures.Registry(CatalogFixtures.ThingSpec);

    /// <summary>
    /// AutoCreate on a populated version 1 database MIGRATES it, and everything it held is still there.
    /// </summary>
    [CatalogSqlServerFact]
    public async Task AutoCreateMigratesAPopulatedVersionOneDatabaseAndKeepsEverythingItHeld()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionOne(database);

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(Epoch, await store.GetStoreEpochAsync());
        Assert.Equal(2, await store.GetActiveVersionAsync());
        Assert.Equal(1, await store.GetPinnedVersionAsync());

        IReadOnlyList<ContentVersionRecord> versions = await store.ListVersionsAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(2, versions[0].VersionNumber);
        Assert.Equal(SecondHash, versions[0].ServerManifestHash);
        Assert.Equal(FirstHash, versions[1].ServerManifestHash);

        // The temporal history is the part a careless migration would flatten: row 1 has two revisions and the
        // first of them is closed by version 2.
        IReadOnlyList<ContentRowRevision> history = await store.GetRowHistoryAsync(CatalogFixtures.Thing, 1);
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].ReplacedInVersion);
        Assert.Null(history[1].ReplacedInVersion);
        Assert.Equal(12, history[1].Row.Fields[0].Number);

        Assert.Equal(3, (await store.ListAuditAsync(default, 0, 0, 500)).Count);
        Assert.Equal(4, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_chunk;"));

        ContentDraft? draft = await store.GetOpenDraftAsync();
        Assert.NotNull(draft);
        Assert.Equal(2, draft.BaseVersion);
        Assert.Equal(1, draft.EditCount);

        // The ledger the migration added is EMPTY: a migrated catalog has run no upgrades, which is exactly
        // what the runner has to be told so it can apply the pending ones.
        Assert.Empty(await store.ListUpgradesAsync());
    }

    /// <summary>A migrated database publishes, and a stamped publish lands its applied row.</summary>
    [CatalogSqlServerFact]
    public async Task AMigratedDatabaseTakesAStampedPublish()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionOne(database);

        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        ContentPublishResult published = await store.PublishAsync(
            new ContentPublishRequest(Actor, Operator, "the upgrade", 2)
            {
                Upgrade = new ContentUpgradeStamp(UpgradeId, 1),
            });

        Assert.Equal(3, published.VersionNumber);
        ContentUpgradeRecord record = Assert.Single(await store.ListUpgradesAsync());
        Assert.Equal(UpgradeId, record.Id);
        Assert.Equal(ContentUpgradeDisposition.Applied, record.Disposition);
        Assert.Equal(3, record.VersionNumber);
    }

    /// <summary>
    /// ValidateOnly on a version 1 database REFUSES and names the migration. A production host opens this way,
    /// and a schema changed underneath it is the one thing that mode exists to prevent.
    /// </summary>
    [CatalogSqlServerFact]
    public async Task ValidateOnlyOnAVersionOneDatabaseRefusesAndNamesTheMigration()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionOne(database);

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("catalog-v2-content-upgrade-ledger", refused.Message, StringComparison.Ordinal);
        Assert.Contains("version '1'", refused.Message, StringComparison.Ordinal);

        // Refused means REFUSED: the database is still version 1 and still carries no ledger table.
        Assert.Equal(1, database.Scalar(
            "SELECT schema_version FROM dbo.catalog_metadata WHERE metadata_key = 1;"));
        Assert.Equal(0, database.Scalar(
            """
            SELECT COUNT(*) FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo') AND name = N'catalog_content_upgrade';
            """));
    }

    /// <summary>
    /// Reopening a version 2 database is a NO-OP under both modes. A migration that ran again would be a
    /// migration that could fail on its second run, which is how a database ends up neither version.
    /// </summary>
    [CatalogSqlServerFact]
    public async Task ReopeningAVersionTwoDatabaseChangesNothing()
    {
        using var database = new SqlServerCatalogDatabase();
        WriteVersionOne(database);

        var migrated = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await migrated.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        string epoch = await migrated.GetStoreEpochAsync();
        await migrated.RecordUpgradeAsync(
            new ContentUpgradeStamp(UpgradeId, 1),
            ContentUpgradeDisposition.Baseline,
            Actor,
            Operator);

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(epoch, await store.GetStoreEpochAsync());
        Assert.Single(await store.ListUpgradesAsync());
    }

    /// <summary>The embedded version 1 schema, then the content, into the test database.</summary>
    /// <param name="database">The test database, whose catalog schema the fixture already dropped.</param>
    static void WriteVersionOne(SqlServerCatalogDatabase database)
    {
        database.Execute(SqlServerCatalogSchema.VersionOneSchemaSql);
        database.Execute(PopulateVersionOne);
    }
}
