using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The version 1 to version 2 migration, driven against a POPULATED version 1 database this build never
/// created: the fourteen tables as version 1 really declared them, with two published versions, the temporal
/// history they left, an open draft, a pin and audit rows already in it.
/// <para>
/// <b>The DDL below is a FROZEN COPY and that is the whole point.</b> A test that built its version 1
/// database from the provider's own constants would move with them, so the day the shipped schema drifts is
/// the day this test stops noticing. It is version 1 as it was released, and it stays that way.
/// </para>
/// <para>
/// What the migration is allowed to do is add one table. Everything the database already held has to come
/// back byte for byte, which is what the assertions below enumerate: the store epoch, both versions, the row
/// history, the audit count, the open draft and the operator's pin.
/// </para>
/// </summary>
public class SqliteCatalogSchemaMigrationTests
{
    /// <summary>The upgrade a migrated database is stamped with, to prove the ledger works after the move.</summary>
    const string UpgradeId = "harvest-profiles";

    const string Actor = "sqlite-migration-tests";
    const string Operator = "oid:tests";

    /// <summary>A fabricated manifest digest, which is 64 lower hex characters and nothing more.</summary>
    const string FirstHash = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>The second version's digest, different from the first so a mix-up is visible.</summary>
    const string SecondHash = "2222222222222222222222222222222222222222222222222222222222222222";

    /// <summary>The store epoch the fixture seeds, which the migration must leave exactly as it found it.</summary>
    const string Epoch = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// The content catalog schema EXACTLY as version 1 shipped it: the fourteen tables, their indexes and a
    /// metadata seed declaring version 1. Frozen on purpose, never regenerated from the provider.
    /// </summary>
    const string VersionOneDdl = """
        CREATE TABLE IF NOT EXISTS catalog_metadata (
            metadata_key INTEGER NOT NULL PRIMARY KEY CHECK (metadata_key = 1),
            schema_version INTEGER NOT NULL CHECK (schema_version >= 1),
            store_epoch TEXT COLLATE BINARY NOT NULL CHECK (length(store_epoch) IN (32, 36)),
            active_version INTEGER NOT NULL DEFAULT 0 CHECK (active_version >= 0),
            pinned_version INTEGER NULL CHECK (pinned_version IS NULL OR pinned_version >= 1),
            updated_at_utc INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS catalog_type (
            type_id INTEGER NOT NULL PRIMARY KEY CHECK (type_id BETWEEN 1 AND 65535),
            type_key TEXT COLLATE BINARY NOT NULL CHECK (length(type_key) BETWEEN 1 AND 64),
            chunk_slots INTEGER NOT NULL CHECK (chunk_slots BETWEEN 256 AND 65536),
            default_visibility INTEGER NOT NULL CHECK (default_visibility IN (0, 1)),
            max_definition_id INTEGER NULL CHECK (max_definition_id IS NULL OR max_definition_id >= 1),
            first_seen_version INTEGER NOT NULL CHECK (first_seen_version >= 0));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_type_key ON catalog_type(type_key);

        CREATE TABLE IF NOT EXISTS catalog_version (
            version_number INTEGER NOT NULL PRIMARY KEY CHECK (version_number >= 1),
            server_manifest_hash TEXT COLLATE BINARY NOT NULL CHECK (length(server_manifest_hash) = 64),
            client_manifest_hash TEXT COLLATE BINARY NOT NULL CHECK (length(client_manifest_hash) = 64),
            minimum_server_build INTEGER NOT NULL CHECK (minimum_server_build >= 0),
            minimum_client_build INTEGER NOT NULL CHECK (minimum_client_build >= 0),
            format_generation INTEGER NOT NULL CHECK (format_generation >= 1),
            base_version INTEGER NOT NULL CHECK (base_version >= 0),
            published_by TEXT COLLATE BINARY NOT NULL CHECK (length(published_by) BETWEEN 1 AND 128),
            note TEXT COLLATE BINARY NOT NULL CHECK (length(note) <= 1024),
            published_at_utc INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS catalog_row (
            type_id INTEGER NOT NULL,
            definition_id INTEGER NOT NULL CHECK (definition_id >= 1),
            valid_from_version INTEGER NOT NULL CHECK (valid_from_version >= 1),
            replaced_in_version INTEGER NULL CHECK (replaced_in_version IS NULL
                OR replaced_in_version > valid_from_version),
            content_key TEXT COLLATE BINARY NOT NULL CHECK (length(content_key) BETWEEN 1 AND 64),
            parent_id INTEGER NOT NULL DEFAULT 0 CHECK (parent_id >= 0),
            family_id INTEGER NULL,
            retired INTEGER NOT NULL DEFAULT 0 CHECK (retired IN (0, 1)),
            PRIMARY KEY (type_id, definition_id, valid_from_version),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id),
            FOREIGN KEY (valid_from_version) REFERENCES catalog_version(version_number),
            FOREIGN KEY (family_id) REFERENCES catalog_family(family_id));
        CREATE INDEX IF NOT EXISTS ix_catalog_row_live ON catalog_row(type_id, replaced_in_version, definition_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_row_key ON catalog_row(type_id, content_key, valid_from_version);
        CREATE INDEX IF NOT EXISTS ix_catalog_row_changed ON catalog_row(valid_from_version, type_id, definition_id);

        CREATE TABLE IF NOT EXISTS catalog_row_field (
            type_id INTEGER NOT NULL,
            definition_id INTEGER NOT NULL,
            valid_from_version INTEGER NOT NULL,
            field_name TEXT COLLATE BINARY NOT NULL CHECK (length(field_name) BETWEEN 1 AND 64),
            field_kind INTEGER NOT NULL CHECK (field_kind BETWEEN 0 AND 6),
            int_value INTEGER NULL,
            text_value TEXT COLLATE BINARY NULL CHECK (text_value IS NULL OR length(text_value) <= 192),
            blob_value BLOB NULL CHECK (blob_value IS NULL OR length(blob_value) <= 4096),
            PRIMARY KEY (type_id, definition_id, valid_from_version, field_name),
            FOREIGN KEY (type_id, definition_id, valid_from_version)
                REFERENCES catalog_row(type_id, definition_id, valid_from_version));

        CREATE TABLE IF NOT EXISTS catalog_family (
            family_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            type_id INTEGER NOT NULL,
            family_key TEXT COLLATE BINARY NOT NULL CHECK (length(family_key) BETWEEN 1 AND 64),
            block_size INTEGER NOT NULL CHECK (block_size BETWEEN 16 AND 65536),
            retired INTEGER NOT NULL DEFAULT 0 CHECK (retired IN (0, 1)),
            created_in_version INTEGER NOT NULL CHECK (created_in_version >= 1),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_family_key ON catalog_family(type_id, family_key);

        CREATE TABLE IF NOT EXISTS catalog_family_block (
            family_id INTEGER NOT NULL,
            block_ordinal INTEGER NOT NULL CHECK (block_ordinal >= 0),
            base_id INTEGER NOT NULL CHECK (base_id >= 1),
            block_size INTEGER NOT NULL CHECK (block_size BETWEEN 16 AND 65536),
            next_free_id INTEGER NOT NULL CHECK (next_free_id >= base_id),
            reserved_in_version INTEGER NOT NULL CHECK (reserved_in_version >= 1),
            PRIMARY KEY (family_id, block_ordinal),
            FOREIGN KEY (family_id) REFERENCES catalog_family(family_id),
            CHECK (base_id % block_size = 0),
            CHECK (next_free_id <= base_id + block_size));

        CREATE TABLE IF NOT EXISTS catalog_id_high_water (
            type_id INTEGER NOT NULL PRIMARY KEY,
            reserved_through INTEGER NOT NULL CHECK (reserved_through >= 0),
            issued_through INTEGER NOT NULL CHECK (issued_through >= 0 AND issued_through <= reserved_through),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));

        CREATE TABLE IF NOT EXISTS catalog_draft (
            draft_key INTEGER NOT NULL PRIMARY KEY CHECK (draft_key = 1),
            base_version INTEGER NOT NULL CHECK (base_version >= 0),
            opened_by TEXT COLLATE BINARY NOT NULL CHECK (length(opened_by) BETWEEN 1 AND 128),
            opened_at_utc INTEGER NOT NULL,
            note TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(note) <= 1024),
            frozen_for_base_version INTEGER NULL CHECK (frozen_for_base_version IS NULL
                OR frozen_for_base_version >= 0));

        CREATE TABLE IF NOT EXISTS catalog_draft_edit (
            edit_ordinal INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            type_id INTEGER NOT NULL,
            definition_id INTEGER NOT NULL DEFAULT 0 CHECK (definition_id >= 0),
            content_key TEXT COLLATE BINARY NOT NULL CHECK (length(content_key) BETWEEN 1 AND 64),
            operation INTEGER NOT NULL CHECK (operation IN (1, 2, 3, 4)),
            retire_policy INTEGER NOT NULL DEFAULT 0 CHECK (retire_policy IN (0, 1, 2)),
            replacement_id INTEGER NOT NULL DEFAULT 0 CHECK (replacement_id >= 0),
            fork_key TEXT COLLATE BINARY NULL CHECK (fork_key IS NULL OR length(fork_key) BETWEEN 1 AND 64),
            fork_flag_field TEXT COLLATE BINARY NULL CHECK (fork_flag_field IS NULL OR length(fork_flag_field) BETWEEN 1 AND 64),
            family_id INTEGER NULL,
            imported_retired INTEGER NOT NULL DEFAULT 0 CHECK (imported_retired IN (0, 1)),
            edited_by TEXT COLLATE BINARY NOT NULL CHECK (length(edited_by) BETWEEN 1 AND 128),
            edited_at_utc INTEGER NOT NULL,
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_catalog_draft_edit_target
            ON catalog_draft_edit(type_id, definition_id, content_key);

        CREATE TABLE IF NOT EXISTS catalog_draft_edit_field (
            edit_ordinal INTEGER NOT NULL,
            field_name TEXT COLLATE BINARY NOT NULL CHECK (length(field_name) BETWEEN 1 AND 64),
            field_kind INTEGER NOT NULL CHECK (field_kind BETWEEN 0 AND 6),
            int_value INTEGER NULL,
            text_value TEXT COLLATE BINARY NULL CHECK (text_value IS NULL OR length(text_value) <= 192),
            blob_value BLOB NULL CHECK (blob_value IS NULL OR length(blob_value) <= 4096),
            PRIMARY KEY (edit_ordinal, field_name),
            FOREIGN KEY (edit_ordinal) REFERENCES catalog_draft_edit(edit_ordinal) ON DELETE CASCADE);

        CREATE TABLE IF NOT EXISTS catalog_audit (
            audit_id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc INTEGER NOT NULL,
            actor TEXT COLLATE BINARY NOT NULL CHECK (length(actor) BETWEEN 1 AND 128),
            operator TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(operator) <= 128),
            action TEXT COLLATE BINARY NOT NULL CHECK (length(action) BETWEEN 1 AND 32),
            type_id INTEGER NOT NULL DEFAULT 0,
            definition_id INTEGER NOT NULL DEFAULT 0,
            content_key TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(content_key) <= 64),
            field_name TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(field_name) <= 64),
            before_value TEXT COLLATE BINARY NULL CHECK (before_value IS NULL OR length(before_value) <= 4096),
            after_value TEXT COLLATE BINARY NULL CHECK (after_value IS NULL OR length(after_value) <= 4096),
            version_number INTEGER NOT NULL DEFAULT 0,
            note TEXT COLLATE BINARY NOT NULL DEFAULT '' CHECK (length(note) <= 1024));
        CREATE INDEX IF NOT EXISTS ix_catalog_audit_time ON catalog_audit(occurred_at_utc, audit_id);
        CREATE INDEX IF NOT EXISTS ix_catalog_audit_target ON catalog_audit(type_id, definition_id, audit_id);

        CREATE TABLE IF NOT EXISTS catalog_remap_rule (
            sequence INTEGER NOT NULL PRIMARY KEY CHECK (sequence >= 1),
            introduced_in INTEGER NOT NULL CHECK (introduced_in >= 1),
            type_id INTEGER NOT NULL,
            kind INTEGER NOT NULL CHECK (kind BETWEEN 1 AND 255),
            from_id INTEGER NOT NULL CHECK (from_id >= 1),
            to_id INTEGER NOT NULL DEFAULT 0 CHECK (to_id >= 0),
            payload BLOB NOT NULL DEFAULT x'' CHECK (length(payload) <= 64),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id),
            FOREIGN KEY (introduced_in) REFERENCES catalog_version(version_number));

        CREATE TABLE IF NOT EXISTS catalog_chunk (
            version_number INTEGER NOT NULL,
            type_id INTEGER NOT NULL,
            chunk_index INTEGER NOT NULL CHECK (chunk_index >= 0),
            chunk_hash TEXT COLLATE BINARY NOT NULL CHECK (length(chunk_hash) = 64),
            row_count INTEGER NOT NULL CHECK (row_count >= 0),
            uncompressed_bytes INTEGER NOT NULL CHECK (uncompressed_bytes >= 0),
            stored_bytes INTEGER NOT NULL CHECK (stored_bytes >= 0),
            visibility INTEGER NOT NULL CHECK (visibility IN (0, 1)),
            PRIMARY KEY (version_number, type_id, chunk_index, visibility),
            FOREIGN KEY (version_number) REFERENCES catalog_version(version_number),
            FOREIGN KEY (type_id) REFERENCES catalog_type(type_id));
        CREATE INDEX IF NOT EXISTS ix_catalog_chunk_hash ON catalog_chunk(chunk_hash);

        INSERT OR IGNORE INTO catalog_metadata(metadata_key, schema_version, store_epoch, active_version, pinned_version, updated_at_utc)
        VALUES (1, 1, lower(hex(randomblob(16))), 0, NULL, CAST(strftime('%s', 'now') AS INTEGER) * 1000);
        """;

    /// <summary>
    /// A version 1 database with content in it: two published versions, three row revisions (one of them
    /// closed by the second version), the chunk rows of both versions, an open draft holding one edit, an
    /// operator pin on version 1, and three audit rows.
    /// </summary>
    const string PopulateVersionOne = """
        INSERT INTO catalog_type(
            type_id, type_key, chunk_slots, default_visibility, max_definition_id, first_seen_version)
        VALUES (1024, 'thing', 256, 0, NULL, 0);

        INSERT INTO catalog_version(
            version_number, server_manifest_hash, client_manifest_hash, minimum_server_build,
            minimum_client_build, format_generation, base_version, published_by, note, published_at_utc)
        VALUES (1, '1111111111111111111111111111111111111111111111111111111111111111',
                   '1111111111111111111111111111111111111111111111111111111111111111',
                   0, 0, 1, 0, 'seed', 'first', 1700000000000),
               (2, '2222222222222222222222222222222222222222222222222222222222222222',
                   '2222222222222222222222222222222222222222222222222222222222222222',
                   0, 0, 1, 1, 'seed', 'second', 1700000001000);

        INSERT INTO catalog_row(
            type_id, definition_id, valid_from_version, replaced_in_version, content_key, parent_id,
            family_id, retired)
        VALUES (1024, 1, 1, 2, 'one', 0, NULL, 0),
               (1024, 1, 2, NULL, 'one', 0, NULL, 0),
               (1024, 2, 1, NULL, 'two', 0, NULL, 0);

        INSERT INTO catalog_row_field(
            type_id, definition_id, valid_from_version, field_name, field_kind, int_value, text_value,
            blob_value)
        VALUES (1024, 1, 1, 'value', 0, 11, NULL, NULL),
               (1024, 1, 2, 'value', 0, 12, NULL, NULL),
               (1024, 2, 1, 'value', 0, 22, NULL, NULL);

        INSERT INTO catalog_chunk(
            version_number, type_id, chunk_index, chunk_hash, row_count, uncompressed_bytes, stored_bytes,
            visibility)
        VALUES (1, 1024, 0, '1111111111111111111111111111111111111111111111111111111111111111', 2, 64, 48, 0),
               (1, 1024, 0, '1111111111111111111111111111111111111111111111111111111111111111', 2, 64, 48, 1),
               (2, 1024, 0, '2222222222222222222222222222222222222222222222222222222222222222', 2, 64, 48, 0),
               (2, 1024, 0, '2222222222222222222222222222222222222222222222222222222222222222', 2, 64, 48, 1);

        INSERT INTO catalog_id_high_water(type_id, reserved_through, issued_through)
        VALUES (1024, 2, 2);

        INSERT INTO catalog_draft(draft_key, base_version, opened_by, opened_at_utc, note, frozen_for_base_version)
        VALUES (1, 2, 'operator', 1700000002000, 'an open draft', NULL);

        INSERT INTO catalog_draft_edit(
            edit_ordinal, type_id, definition_id, content_key, operation, retire_policy, replacement_id,
            fork_key, fork_flag_field, family_id, imported_retired, edited_by, edited_at_utc)
        VALUES (1, 1024, 2, 'two', 2, 0, 0, NULL, NULL, NULL, 0, 'operator', 1700000002000);

        INSERT INTO catalog_draft_edit_field(edit_ordinal, field_name, field_kind, int_value, text_value, blob_value)
        VALUES (1, 'value', 0, 33, NULL, NULL);

        INSERT INTO catalog_audit(
            occurred_at_utc, actor, operator, action, type_id, definition_id, content_key, field_name,
            before_value, after_value, version_number, note)
        VALUES (1700000000000, 'seed', '', 'publish', 1024, 1, 'one', 'value', NULL, '11', 1, 'first'),
               (1700000001000, 'seed', '', 'publish', 1024, 1, 'one', 'value', '11', '12', 2, 'second'),
               (1700000002000, 'operator', '', 'draft-edit', 1024, 2, 'two', 'value', NULL, '33', 0, '');

        UPDATE catalog_metadata
        SET store_epoch = '0123456789abcdef0123456789abcdef', active_version = 2, pinned_version = 1
        WHERE metadata_key = 1;
        """;

    static ContentTypeRegistry Registry() => PublishFixtures.Registry(PublishFixtures.Thing);

    /// <summary>
    /// AutoCreate on a populated version 1 file MIGRATES it in place, and everything it held is still there.
    /// </summary>
    [Fact]
    public async Task AutoCreateMigratesAPopulatedVersionOneDatabaseAndKeepsEverythingItHeld()
    {
        using var database = new TemporaryCatalogDatabase();
        WriteVersionOne(database);

        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack());
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
        IReadOnlyList<ContentRowRevision> history = await store.GetRowHistoryAsync(
            new ContentTypeId(PublishFixtures.ThingTypeId), 1);
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].ReplacedInVersion);
        Assert.Null(history[1].ReplacedInVersion);
        Assert.Equal(12, history[1].Row.Fields[0].Number);

        Assert.Equal(3, (await store.ListAuditAsync(default, 0, 0, 500)).Count);
        Assert.Equal(4L, database.Scalar("SELECT COUNT(*) FROM catalog_chunk;"));

        ContentDraft? draft = await store.GetOpenDraftAsync();
        Assert.NotNull(draft);
        Assert.Equal(2, draft.BaseVersion);
        Assert.Equal(1, draft.EditCount);

        // The ledger the migration added is EMPTY: a migrated catalog has run no upgrades, which is exactly
        // what the runner has to be told so it can apply the pending ones.
        Assert.Empty(await store.ListUpgradesAsync());
    }

    /// <summary>A migrated database publishes, and a stamped publish lands its applied row.</summary>
    [Fact]
    public async Task AMigratedDatabaseTakesAStampedPublish()
    {
        using var database = new TemporaryCatalogDatabase();
        WriteVersionOne(database);

        using var store = new SqliteContentAuthoringStore(
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
    /// ValidateOnly on a version 1 file REFUSES and names the migration. A production host opens this way, and
    /// a file rewritten underneath it is the one thing that mode exists to prevent.
    /// </summary>
    [Fact]
    public async Task ValidateOnlyOnAVersionOneDatabaseRefusesAndNamesTheMigration()
    {
        using var database = new TemporaryCatalogDatabase();
        WriteVersionOne(database);

        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("catalog-v2-content-upgrade-ledger", refused.Message, StringComparison.Ordinal);
        Assert.Contains("version '1'", refused.Message, StringComparison.Ordinal);

        // Refused means REFUSED: the file is still version 1 and still carries no ledger table.
        Assert.Equal(
            1L,
            database.Scalar("SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;"));
        Assert.Equal(0L, database.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'catalog_content_upgrade';"));
    }

    /// <summary>
    /// Reopening a version 2 file is a NO-OP under both modes. A migration that ran again would be a migration
    /// that could fail on its second run, which is how a database ends up neither version.
    /// </summary>
    [Fact]
    public async Task ReopeningAVersionTwoDatabaseChangesNothing()
    {
        using var database = new TemporaryCatalogDatabase();
        WriteVersionOne(database);

        string epoch;
        using (var migrated = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            await migrated.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            epoch = await migrated.GetStoreEpochAsync();
            await migrated.RecordUpgradeAsync(
                new ContentUpgradeStamp(UpgradeId, 1),
                ContentUpgradeDisposition.Baseline,
                Actor,
                Operator);
        }

        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(epoch, await store.GetStoreEpochAsync());
        Assert.Single(await store.ListUpgradesAsync());
    }

    /// <summary>
    /// Many hosts opening ONE version 1 file at the same time all succeed. Two replicas booting together is
    /// an ordinary deployment, so exactly one of them migrates and the rest have to accept the result.
    /// <para>
    /// <b>The failure this pins is a validation reading a TORN view.</b> The open takes an object snapshot
    /// and a schema version, and a host that read the objects while the file was version 1 and the version
    /// after someone else's migration would compare a pre-migration snapshot against the version 2 DDL and
    /// refuse a correct database for a missing <c>catalog_content_upgrade</c>. The objects are read after the
    /// version is settled, immediately before they are compared, so the two always describe one state.
    /// </para>
    /// <para>
    /// It LOOPS, because a window measured in microseconds shows up once in many runs and never in one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ManyHostsOpeningOneVersionOneFileAtOnceAllSucceed()
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            using var database = new TemporaryCatalogDatabase();
            WriteVersionOne(database);

            var opens = new Task[8];
            for (int i = 0; i < opens.Length; i++)
            {
                opens[i] = Task.Run(async () =>
                {
                    using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
                    await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
                    Assert.Equal(2, await store.GetSchemaVersionAsync());
                });
            }

            await Task.WhenAll(opens);
        }
    }

    /// <summary>
    /// A migration that cannot take the write lock reports the LOCK, not a schema to migrate. A second writer
    /// holding the file is transient, and "apply migration" is advice to an operator about a migration this
    /// open was already running.
    /// </summary>
    [Fact]
    public async Task AMigrationThatCannotTakeTheWriteLockReportsTheLockRatherThanASchemaToMigrate()
    {
        using var database = new TemporaryCatalogDatabase();
        WriteVersionOne(database);

        using (var holder = new SqliteConnection(database.ConnectionString + ";Pooling=False"))
        {
            holder.Open();
            using (SqliteCommand begin = holder.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                begin.ExecuteNonQuery();
            }

            using var store = new SqliteContentAuthoringStore(database.ConnectionString + ";Default Timeout=1", Registry());
            SqliteException busy = await Assert.ThrowsAsync<SqliteException>(
                () => store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate));

            Assert.Equal(SQLitePCL.raw.SQLITE_BUSY, busy.SqliteErrorCode);
        }

        Assert.Equal(1L, database.Scalar("SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;"));
    }

    /// <summary>The frozen version 1 schema, then the content, into the test's own temporary file.</summary>
    /// <param name="database">The temporary database.</param>
    static void WriteVersionOne(TemporaryCatalogDatabase database)
    {
        database.Execute(VersionOneDdl);
        database.Execute(PopulateVersionOne);
    }
}
