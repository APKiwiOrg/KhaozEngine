using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// Schema version 3 of the SQLite content catalog: every row records when it was created, every row that
/// changes after insert records when it last changed, and the store writes both from its own clock. An update
/// time moves only when a statement changes a value in the row.
/// </summary>
public sealed partial class SqliteCatalogRowTimestampTests
{
    const string MigrationName = "catalog-v3-row-timestamps";
    const string Actor = "sqlite-row-timestamps";
    const string Operator = "oid:tests";

    static readonly DateTimeOffset T0 = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every column version 3 adds, which is exactly what the migration has to add.</summary>
    static readonly (string Table, string Column)[] NewColumns =
    [
        ("catalog_metadata", "created_at_utc"),
        ("catalog_type", "created_at_utc"),
        ("catalog_type", "updated_at_utc"),
        ("catalog_family", "created_at_utc"),
        ("catalog_family_block", "created_at_utc"),
        ("catalog_family_block", "updated_at_utc"),
        ("catalog_row", "created_at_utc"),
        ("catalog_row", "updated_at_utc"),
        ("catalog_row_field", "created_at_utc"),
        ("catalog_id_high_water", "created_at_utc"),
        ("catalog_id_high_water", "updated_at_utc"),
        ("catalog_draft", "updated_at_utc"),
        ("catalog_draft_edit", "created_at_utc"),
        ("catalog_draft_edit_field", "created_at_utc"),
        ("catalog_remap_rule", "created_at_utc"),
        ("catalog_chunk", "created_at_utc"),
    ];

    /// <summary>Every catalog table and the column that holds its creation time.</summary>
    static readonly (string Table, string Column)[] CreationTimes =
    [
        ("catalog_audit", "occurred_at_utc"),
        ("catalog_chunk", "created_at_utc"),
        ("catalog_content_upgrade", "recorded_at_utc"),
        ("catalog_draft", "opened_at_utc"),
        ("catalog_draft_edit", "created_at_utc"),
        ("catalog_draft_edit_field", "created_at_utc"),
        ("catalog_family", "created_at_utc"),
        ("catalog_family_block", "created_at_utc"),
        ("catalog_id_high_water", "created_at_utc"),
        ("catalog_metadata", "created_at_utc"),
        ("catalog_remap_rule", "created_at_utc"),
        ("catalog_row", "created_at_utc"),
        ("catalog_row_field", "created_at_utc"),
        ("catalog_type", "created_at_utc"),
        ("catalog_version", "published_at_utc"),
    ];

    /// <summary>Every catalog table whose rows change after insert and the column that holds that time.</summary>
    static readonly (string Table, string Column)[] UpdateTimes =
    [
        ("catalog_draft", "updated_at_utc"),
        ("catalog_draft_edit", "edited_at_utc"),
        ("catalog_family_block", "updated_at_utc"),
        ("catalog_id_high_water", "updated_at_utc"),
        ("catalog_metadata", "updated_at_utc"),
        ("catalog_row", "updated_at_utc"),
        ("catalog_type", "updated_at_utc"),
    ];

    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    static ContentTypeRegistry Registry() => PublishFixtures.Registry(PublishFixtures.Thing);

    static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "row timestamps", expectedBaseVersion);

    [Fact]
    public async Task Fresh_catalog_is_version_3_with_nullable_timestamp_columns()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            Assert.Equal(3, await store.GetSchemaVersionAsync());
        }

        foreach ((string table, string column) in NewColumns)
        {
            string info = $"FROM pragma_table_info('{table}') WHERE name = '{column}'";
            Assert.Equal(1L, database.Scalar($"SELECT COUNT(*) {info};"));
            Assert.Equal("INTEGER", SqliteCatalogResetHarness.Text(database, $"SELECT type {info};"));
            Assert.Equal(0L, database.Scalar($"SELECT [notnull] {info};"));
        }

        // The migration adds exactly these columns and no others.
        Assert.Equal(
            NewColumns,
            SqliteCatalogSchema.VersionThreeColumns.Select(value => (value.Table, value.Column)));
    }

    [Fact]
    public async Task Every_catalog_write_path_stamps_its_rows()
    {
        ContentBundle bundle = await SourceBundleAsync(withFamily: true);
        using var database = new TemporaryCatalogDatabase();
        var clock = new ManualClock(T0);
        ContentTypeRegistry registry = Registry();
        long? metadataCreated;
        long t0;
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, registry, database.Pack(), clock.Read))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            t0 = clock.Millis;
            metadataCreated = Value(database, "SELECT created_at_utc FROM catalog_metadata;");
            Assert.NotNull(metadataCreated);
            Assert.Equal(Value(database, "SELECT updated_at_utc FROM catalog_metadata;"), metadataCreated);
            Assert.Equal((t0, t0), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_type;"));

            // The import stages the families and the id marks, then publishes version 1.
            long t1 = clock.Tick();
            await store.ImportBundleAsync(bundle, Actor, Operator, "seed");
            Assert.Equal(t1, Value(database, "SELECT published_at_utc FROM catalog_version WHERE version_number = 1;"));
            AssertEvery(database, "catalog_family", "1 = 1", t1, "created_at_utc");
            AssertEvery(database, "catalog_family_block", "1 = 1", t1, "created_at_utc", "updated_at_utc");
            AssertEvery(database, "catalog_id_high_water", "1 = 1", t1, "created_at_utc", "updated_at_utc");
            AssertEvery(database, "catalog_row", "1 = 1", t1, "created_at_utc", "updated_at_utc");
            AssertEvery(database, "catalog_row_field", "1 = 1", t1, "created_at_utc");
            AssertEvery(database, "catalog_remap_rule", "1 = 1", t1, "created_at_utc");
            AssertEvery(database, "catalog_chunk", "1 = 1", t1, "created_at_utc");

            // A draft opened with two edits.
            long t2 = clock.Tick();
            await store.ApplyEditsAsync(
                [
                    ContentEdit.Add(Thing, new ContentKey("three"), PublishFixtures.Fields(33)),
                    ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(12)),
                ],
                Actor,
                Operator,
                "first");
            Assert.Equal((t2, t2), DraftTimes(database));
            AssertEvery(database, "catalog_draft_edit", "1 = 1", t2, "created_at_utc", "edited_at_utc");
            AssertEvery(database, "catalog_draft_edit_field", "1 = 1", t2, "created_at_utc");

            // The same row edited again under a new note. The edit keeps its creation time and its field rows
            // are new rows.
            long t3 = clock.Tick();
            await store.ApplyEditsAsync(
                [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(13))],
                Actor,
                Operator,
                "second");
            Assert.Equal((t2, t3), DraftTimes(database));
            Assert.Equal((t2, t3), EditTimes(database, "one"));
            Assert.Equal((t2, t2), EditTimes(database, "three"));
            AssertEvery(database, "catalog_draft_edit_field", EditFieldOf("one"), t3, "created_at_utc");
            AssertEvery(database, "catalog_draft_edit_field", EditFieldOf("three"), t2, "created_at_utc");

            // A family and its first block, then an id issued out of the block.
            long t4 = clock.Tick();
            ContentFamily shields = await store.CreateFamilyAsync(Thing, "shields", 16, Actor, Operator);
            string ofShields = FormattableString.Invariant($"family_id = {shields.FamilyId}");
            AssertEvery(database, "catalog_family", ofShields, t4, "created_at_utc");
            Assert.Equal((t4, t4), Pair(database, $"SELECT created_at_utc, updated_at_utc FROM catalog_family_block WHERE {ofShields};"));
            Assert.Equal((t1, t4), HighWaterTimes(database));
            long t5 = clock.Tick();
            await store.AllocateInFamilyAsync(shields.FamilyId);
            Assert.Equal((t4, t5), Pair(database, $"SELECT created_at_utc, updated_at_utc FROM catalog_family_block WHERE {ofShields};"));

            // The freeze and its release are changes to the draft row, and a pin moves only the metadata update.
            long t6 = clock.Tick();
            await store.FreezeDraftAsync(1);
            Assert.Equal((t2, t6), DraftTimes(database));
            long t7 = clock.Tick();
            await store.ClearDraftFreezeAsync();
            Assert.Equal((t2, t7), DraftTimes(database));

            // A marker naming a base the catalog no longer stands at is a dead publish's leftover, and the next
            // baseline read clears it.
            clock.Tick();
            await store.FreezeDraftAsync(0);
            long cleared = clock.Tick();
            await store.ReadPublishBaselineAsync();
            Assert.Null(Value(database, "SELECT frozen_for_base_version FROM catalog_draft;"));
            Assert.Equal((t2, cleared), DraftTimes(database));
            long t8 = clock.Tick();
            await store.SetPinnedVersionAsync(1, Actor, Operator);
            Assert.Equal((metadataCreated, t8), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_metadata;"));

            // Version 2 closes one revision, inserts two and issues the id the added row takes.
            long t9 = clock.Tick();
            await store.PublishAsync(Request(1));
            Assert.Equal(t9, Value(database, "SELECT published_at_utc FROM catalog_version WHERE version_number = 2;"));
            Assert.Equal((t1, t9), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_row WHERE definition_id = 1 AND valid_from_version = 1;"));
            AssertEvery(database, "catalog_row", "valid_from_version = 2", t9, "created_at_utc", "updated_at_utc");
            AssertEvery(database, "catalog_row", "definition_id = 2", t1, "created_at_utc", "updated_at_utc");
            AssertEvery(database, "catalog_row_field", "valid_from_version = 2", t9, "created_at_utc");
            AssertEvery(database, "catalog_chunk", "version_number = 2", t9, "created_at_utc");
            AssertEvery(database, "catalog_chunk", "version_number = 1", t1, "created_at_utc");
            Assert.Equal((t1, t9), HighWaterTimes(database));
            Assert.Equal((metadataCreated, t9), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_metadata;"));
            Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_draft;"));

            // A publish whose draft outlives it. An edit that arrived after the freeze was released is carried
            // forward, and the rebase is a change to the draft row at the publish time.
            long t10 = clock.Tick();
            await store.ApplyEditsAsync(
                [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(15))],
                Actor,
                Operator,
                "third");
            long t11 = clock.Tick();
            var publisher = new ContentPublisher(store, store, registry);
            ContentPublishPlan plan = PublishFixtures.AssertValid(
                await publisher.PrepareAsync(Request(2), await store.ReadPublishBaselineAsync()));
            Assert.Equal((t10, t11), DraftTimes(database));
            long t12 = clock.Tick();
            await store.ClearDraftFreezeAsync();
            await store.ApplyEditsAsync(
                [ContentEdit.Add(Thing, new ContentKey("four"), PublishFixtures.Fields(44))],
                Actor,
                Operator,
                string.Empty);
            long t13 = clock.Tick();
            await store.CommitPublishAsync(plan, Request(2), null);
            Assert.Equal(3L, database.Scalar("SELECT base_version FROM catalog_draft;"));
            Assert.Equal((t10, t13), DraftTimes(database));
            Assert.Equal((t12, t12), EditTimes(database, "four"));

            await store.RecordUpgradeAsync(
                new ContentUpgradeStamp("row-timestamps", 1), ContentUpgradeDisposition.Baseline, Actor, Operator);
        }

        AssertEveryRowCarriesItsTimes(database);

        // A changed type declaration is a change to its row.
        long t14 = clock.Tick();
        using (var capped = new SqliteContentAuthoringStore(
            database.ConnectionString, PublishFixtures.Registry(PublishFixtures.CappedThing), database.Pack(), clock.Read))
        {
            await capped.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        }

        Assert.Equal((t0, t14), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_type;"));
    }

    /// <summary>
    /// Every row a publish writes carries its version's <c>published_at_utc</c> exactly, which is also what the
    /// migration fills a legacy row with. The clock moves on every read, so a second reading anywhere in the
    /// commit would show.
    /// </summary>
    [Fact]
    public async Task A_publish_stamps_every_row_it_writes_with_the_version_publish_time()
    {
        using var database = new TemporaryCatalogDatabase();
        DateTimeOffset now = T0;
        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), () => now = now.AddMilliseconds(1));
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(22)),
            ],
            Actor,
            Operator,
            "two adds");
        await store.PublishAsync(Request(0));
        await store.ApplyEditsAsync(
            [
                ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(12)),
                ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0),
            ],
            Actor,
            Operator,
            "revise and retire");
        await store.PublishAsync(Request(1));

        long first = Value(database, "SELECT published_at_utc FROM catalog_version WHERE version_number = 1;")!.Value;
        long second = Value(database, "SELECT published_at_utc FROM catalog_version WHERE version_number = 2;")!.Value;
        Assert.Equal((first, second), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_row WHERE definition_id = 1 AND valid_from_version = 1;"));
        AssertEvery(database, "catalog_row", "valid_from_version = 2", second, "created_at_utc", "updated_at_utc");
        AssertEvery(database, "catalog_row_field", "valid_from_version = 1", first, "created_at_utc");
        AssertEvery(database, "catalog_row_field", "valid_from_version = 2", second, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "version_number = 1", first, "created_at_utc");
        AssertEvery(database, "catalog_chunk", "version_number = 2", second, "created_at_utc");
        AssertEvery(database, "catalog_remap_rule", "introduced_in = 2", second, "created_at_utc");
    }

    [Fact]
    public async Task Writes_that_change_nothing_leave_the_update_time_alone()
    {
        ContentBundle bundle = await SourceBundleAsync(withFamily: false);
        using var database = new TemporaryCatalogDatabase();
        var clock = new ManualClock(T0);
        long t0 = clock.Millis;
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await store.AllocateAsync(Thing, 5);
        }

        Assert.Equal((t0, t0), HighWaterTimes(database));

        clock.Tick();
        using var reopened = new SqliteContentAuthoringStore(
            database.ConnectionString, Registry(), database.Pack(), clock.Read);
        await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        Assert.Equal((t0, t0), Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_type;"));

        // The bundle's ids sit below both marks, so seeding them raises nothing.
        clock.Tick();
        await reopened.ImportBundleAsync(bundle, Actor, Operator, "seed");
        Assert.Equal((t0, t0), HighWaterTimes(database));

        long t3 = clock.Tick();
        await reopened.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(12))], Actor, Operator, "note");
        Assert.Equal((t3, t3), DraftTimes(database));
        clock.Tick();
        await reopened.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(13))], Actor, Operator, string.Empty);
        clock.Tick();
        await reopened.ApplyEditsAsync(
            [ContentEdit.Update(Thing, 1, new ContentKey("one"), PublishFixtures.Fields(14))], Actor, Operator, "note");
        Assert.Equal((t3, t3), DraftTimes(database));

        long t6 = clock.Tick();
        await reopened.FreezeDraftAsync(1);
        clock.Tick();
        await reopened.FreezeDraftAsync(1);
        Assert.Equal((t3, t6), DraftTimes(database));

        long t8 = clock.Tick();
        await reopened.ClearDraftFreezeAsync();
        clock.Tick();
        await reopened.ClearDraftFreezeAsync();
        Assert.Equal((t3, t8), DraftTimes(database));
    }

    /// <summary>
    /// A bundle exported from a second database: two rows, one of them retired so a remap rule rides along,
    /// and optionally a family with one block.
    /// </summary>
    static async Task<ContentBundle> SourceBundleAsync(bool withFamily)
    {
        using var source = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(source.ConnectionString, Registry(), source.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.ApplyEditsAsync(
            [
                ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(11)),
                ContentEdit.Add(Thing, new ContentKey("two"), PublishFixtures.Fields(22)),
            ],
            Actor,
            Operator,
            "two adds");
        await store.PublishAsync(Request(0));
        if (withFamily)
        {
            await store.CreateFamilyAsync(Thing, "swords", 16, Actor, Operator);
        }

        await store.ApplyEditsAsync(
            [ContentEdit.Retire(Thing, 2, new ContentKey("two"), ContentRetirePolicy.Placeholder, 0)],
            Actor,
            Operator,
            "retire two");
        await store.PublishAsync(Request(1));
        return await store.ExportBundleAsync(2);
    }

    /// <summary>
    /// Every catalog table holds rows, every row carries its creation time, and every row of a table that
    /// changes after insert carries its update time.
    /// </summary>
    static void AssertEveryRowCarriesItsTimes(TemporaryCatalogDatabase database)
    {
        Assert.Equal(
            CreationTimes.Select(value => value.Table),
            SqliteCatalogResetHarness.Tables(database).OrderBy(value => value, StringComparer.Ordinal));
        foreach ((string table, string column) in CreationTimes.Concat(UpdateTimes))
        {
            Assert.True(database.Scalar($"SELECT COUNT(*) FROM {table};") > 0, $"{table} has no row to prove");
            Assert.True(
                database.Scalar($"SELECT COUNT(*) FROM {table} WHERE {column} IS NULL;") == 0,
                $"{table}.{column} is NULL on a row this build wrote");
        }
    }

    /// <summary>Every row of a table matching a filter, holding one time in each named column.</summary>
    static void AssertEvery(
        TemporaryCatalogDatabase database,
        string table,
        string where,
        long expected,
        params string[] columns)
    {
        Assert.True(database.Scalar($"SELECT COUNT(*) FROM {table} WHERE {where};") > 0, $"{table} has no row where {where}");
        foreach (string column in columns)
        {
            long wrong = database.Scalar(FormattableString.Invariant(
                $"SELECT COUNT(*) FROM {table} WHERE {where} AND {column} IS NOT {expected};"));
            Assert.True(wrong == 0, $"{wrong} row(s) of {table} where {where} do not carry {column} = {expected}");
        }
    }

    static string EditFieldOf(string key)
        => $"edit_ordinal IN (SELECT edit_ordinal FROM catalog_draft_edit WHERE content_key = '{key}')";

    static (long?, long?) DraftTimes(TemporaryCatalogDatabase database)
        => Pair(database, "SELECT opened_at_utc, updated_at_utc FROM catalog_draft;");

    static (long?, long?) EditTimes(TemporaryCatalogDatabase database, string key)
        => Pair(database, $"SELECT created_at_utc, edited_at_utc FROM catalog_draft_edit WHERE content_key = '{key}';");

    static (long?, long?) HighWaterTimes(TemporaryCatalogDatabase database)
        => Pair(database, "SELECT created_at_utc, updated_at_utc FROM catalog_id_high_water;");

    /// <summary>One nullable integer on a raw connection.</summary>
    static long? Value(TemporaryCatalogDatabase database, string sql)
    {
        (long? value, _) = Read(database, sql, 1);
        return value;
    }

    /// <summary>The two nullable integers of the ONE row a statement returns.</summary>
    static (long?, long?) Pair(TemporaryCatalogDatabase database, string sql) => Read(database, sql, 2);

    static (long?, long?) Read(TemporaryCatalogDatabase database, string sql, int columns)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        var values = new List<(long?, long?)>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                values.Add((
                    reader.IsDBNull(0) ? null : reader.GetInt64(0),
                    columns < 2 || reader.IsDBNull(1) ? null : reader.GetInt64(1)));
            }
        }

        SqliteConnection.ClearPool(connection);
        return Assert.Single(values);
    }

    /// <summary>The store's clock, moved one minute at a time by the test.</summary>
    sealed class ManualClock(DateTimeOffset start)
    {
        DateTimeOffset _now = start;

        public Func<DateTimeOffset> Read => () => _now;

        public long Millis => _now.ToUnixTimeMilliseconds();

        public long Tick()
        {
            _now = _now.AddMinutes(1);
            return Millis;
        }
    }
}
