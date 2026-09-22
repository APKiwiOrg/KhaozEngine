using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The catalog RESET: every catalog object dropped and the schema recreated from the same script
/// <see cref="SqliteContentAuthoringStore.InitializeAsync"/> creates from, in one transaction, leaving a
/// database indistinguishable from one that has just been initialized for the first time.
/// <para>
/// <b>It exists because a game that ships its client pack inside the client build is always at content
/// version 1.</b> The connect door compares the version NUMBER as well as the manifest hash, and the number
/// is inside the hashed manifest, so such a game cannot publish its server store forward to version 2: it has
/// to REPLACE the store from its committed bundle on every content release. <c>ImportBundleAsync</c> refuses
/// a store that has published anything, and that refusal is right, so the way back to an importable store is
/// this.
/// </para>
/// <para>
/// <b>It is a separate type taking a connection string rather than a member on
/// <see cref="IContentAuthoringStore"/>, and the reason is the CREDENTIAL.</b> A reset is DDL: it drops and
/// creates tables. The everyday authoring path is DML and nothing more, and a deployment that denies its
/// application role DDL (which is the shape a production catalog should have) is doing the right thing. A
/// member on the seam would put a drop-everything verb on the object every console and every admin endpoint
/// already holds, reachable through the interface they were handed for editing rows, and would imply the
/// authoring credential can do it. Keeping it here means a caller reaches the reset by naming this type and
/// passing the migration credential's own connection string, which is a second, deliberate step, and the
/// authoring seam keeps the surface it had.
/// </para>
/// <para>
/// <b>A plain <c>DELETE</c> would not do.</b> <c>catalog_family.family_id</c>, <c>edit_ordinal</c> and
/// <c>audit_id</c> are <c>AUTOINCREMENT</c>, so their <c>sqlite_sequence</c> marks survive a delete and the
/// next family created after a reimport lands ABOVE the bundle's ids. Dropping the table takes its
/// <c>sqlite_sequence</c> row with it, which is what makes the reimport reproduce the bundle's ids exactly.
/// </para>
/// <para>
/// <b>What it does NOT touch is the pack store on disk.</b> The reset knows nothing about a pack root, and a
/// pack root left standing under a replaced catalog still holds a version pointer naming the manifest of the
/// content that was there before. A caller that replaces content at the same version number must CLEAR its
/// pack root or REBUILD it through <c>ContentPackRebuild</c>, rather than leave that to the boot to notice. The hashes that
/// STOOD come back on <see cref="ContentCatalogResetResult"/>, which is the last moment they can be read, so
/// an old pack root can be told from a new one.
/// </para>
/// </summary>
public static class SqliteCatalogReset
{
    /// <summary>The cap on <c>catalog_audit.actor</c>, which also requires at least one character.</summary>
    const int ActorMaxLength = 128;

    /// <summary>The cap on <c>catalog_audit.operator</c>, which may be empty.</summary>
    const int OperatorMaxLength = 128;

    /// <summary>The cap on <c>catalog_audit.note</c>, which may be empty.</summary>
    const int NoteMaxLength = 1024;

    /// <summary>
    /// Drops every catalog object and recreates the schema, all or nothing, and files one audit row in the
    /// new store recording what stood.
    /// <para>
    /// <b>A database carrying NONE of the schema's tables is created rather than refused</b>, through the
    /// same script inside the same transaction, with the same audit row and a result saying nothing stood.
    /// The scripted release path is reset then import, so the first release against a new database takes
    /// that branch. A database carrying SOME of them is a half-finished deletion, refused under reason
    /// <c>catalog-partial</c> and repaired by <paramref name="force"/>, which drops what is left and
    /// recreates the schema.
    /// </para>
    /// <para>
    /// <b>The schema version decides the rest, whenever it can be read.</b> A catalog at an OLDER schema
    /// version than this build writes is reset like any other, and comes back at this build's version: the
    /// recreate runs this build's script, so the reset converges the schema as well as emptying it. A
    /// catalog at a NEWER one is refused under reason <c>schema-mismatch</c> before anything is dropped,
    /// whatever <paramref name="force"/> says, because recreating an older schema over it would move the
    /// database backwards.
    /// </para>
    /// <para>
    /// It opens its OWN connection, so the connection string has to name a durable database. A plain
    /// <c>Data Source=:memory:</c> database belongs to the connection that opened it, so this would open a
    /// second empty one, create a schema into it and throw both away. Under <c>Cache=Shared</c> an in-memory
    /// database is shared by NAME for as long as one connection to it stays open, and a reset does reach the
    /// store its holder is using.
    /// </para>
    /// <para>
    /// The store this resets is left needing <see cref="SqliteContentAuthoringStore.InitializeAsync"/>
    /// again, the same as a database that has just been created: <c>catalog_type</c> is empty until a store
    /// syncs its registry into it.
    /// </para>
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string, under a credential holding DDL rights.</param>
    /// <param name="actor">What the caller authenticated, at most 128 characters, written to the audit row.</param>
    /// <param name="operatorId">The stable identity the console asserted, empty when it asserted none.</param>
    /// <param name="note">Why the catalog was replaced, at most 1024 characters.</param>
    /// <param name="force">Whether to reset even though a draft is open, which DESTROYS that draft.</param>
    /// <param name="cancellationToken">Cancels the work. Nothing is committed on the way out.</param>
    /// <returns>What stood before the reset, and the schema version and epoch the recreated schema carries.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="actor"/> is empty, or an argument is longer than the audit column that holds it.</exception>
    /// <exception cref="ContentAuthoringException">The database carries a catalog schema version newer than this build writes, or a partial catalog or an open draft and <paramref name="force"/> is false.</exception>
    public static async Task<ContentCatalogResetResult> ResetAsync(
        string connectionString,
        string actor,
        string operatorId,
        string note,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ValidateAuditArguments(actor, operatorId, note);

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using (SqliteCommand bootstrap = connection.CreateCommand())
            {
                bootstrap.CommandText = SqliteCatalogSchema.BootstrapSql;
                await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return await ResetAsync(connection, actor, operatorId, note, force, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // The pool is cleared BEFORE the close, the same order SqliteStoreConnection disposes in, so the
            // database file is genuinely released rather than parked in the provider's pool.
            SqliteConnection.ClearPool(connection);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The whole reset inside ONE transaction. SQLite runs DDL transactionally, so a failure anywhere below
    /// (a drop, the create, the audit insert, or a deferred foreign key checked at the commit) rolls the drop
    /// back with it and leaves the catalog exactly as it was. That is not a nicety: a half-dropped catalog
    /// fails the next open outright, because the initializer creates only when it counts ZERO catalog tables
    /// and validates every object by name otherwise.
    /// </summary>
    static async Task<ContentCatalogResetResult> ResetAsync(
        SqliteConnection connection,
        string actor,
        string operatorId,
        string note,
        bool force,
        CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Foreign keys are on and every catalog table references another, so the drop order would otherwise
        // matter and would have to be maintained by hand beside the schema. Deferring moves the check to the
        // commit, by which point there is no row left to violate anything. It is the one pragma that may be
        // set INSIDE a transaction, and it resets itself when the transaction ends.
        await ExecuteAsync(connection, transaction, "PRAGMA defer_foreign_keys = ON;", cancellationToken)
            .ConfigureAwait(false);

        // The schema's own inventory intersected with what stands, which is the rule the whole drop runs
        // under. A name pattern would take a host's own catalogs, cataloguer or catalog_overrides_by_host
        // with it, and those tables are none of this schema's business.
        IReadOnlyList<string> tables = SqliteCatalogSchemaInventory.ReadExisting(connection, transaction);
        Stood stood = await ReadBeforeAsync(connection, transaction, tables, force, cancellationToken)
            .ConfigureAwait(false);

        // An index goes with the table that owns it, and so does the sqlite_sequence row behind AUTOINCREMENT.
        for (int i = 0; i < tables.Count; i++)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "DROP TABLE \"" + tables[i].Replace("\"", "\"\"", StringComparison.Ordinal) + "\";",
                cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, SqliteCatalogSchema.Tables, cancellationToken)
            .ConfigureAwait(false);

        // Both read back from the row the recreate just wrote rather than restated from a constant, so the
        // result reports what the database now says.
        long schemaVersion = await ReadLongAsync(
            connection,
            transaction,
            "SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;",
            cancellationToken).ConfigureAwait(false);
        string epoch = await ReadTextAsync(
            connection,
            transaction,
            "SELECT store_epoch FROM catalog_metadata WHERE metadata_key = 1;",
            cancellationToken).ConfigureAwait(false);
        var result = new ContentCatalogResetResult(
            stood.ActiveVersion,
            stood.ServerManifestHash,
            stood.ClientManifestHash,
            stood.VersionsDropped,
            stood.RowsDropped,
            epoch,
            (int)stood.SchemaVersion,
            (int)schemaVersion,
            stood.State);

        await AppendResetAuditAsync(connection, transaction, actor, operatorId, note, result, cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
        return result;
    }

    /// <summary>
    /// What the store stood at, read while it still exists, with the open-draft refusal on the way past.
    /// <para>
    /// <b>Three databases arrive here and only one of them has anything to say.</b> A database carrying NONE
    /// of the schema's tables is not an error: the scripted release path is reset then import, so the first
    /// release against a new database lands here and the reset creates the schema through the very same
    /// script, dropping nothing. A database whose standing tables are not the whole set its schema version
    /// declares is a half-finished deletion that no store can open and no read can describe, and
    /// <paramref name="force"/> repairs it. A whole catalog is read.
    /// </para>
    /// <para>
    /// A version 1 catalog is WHOLE when it stands at version 1's own set, which lacks the table version 2
    /// added. Judged against this build's set alone it would read as partial, and a plain reset of a store
    /// that is merely older would be refused.
    /// </para>
    /// </summary>
    static async Task<Stood> ReadBeforeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> tables,
        bool force,
        CancellationToken cancellationToken)
    {
        if (tables.Count == 0)
        {
            return Stood.Nothing(ContentCatalogPriorState.Absent);
        }

        long? version = Holds(tables, MetadataTable)
            ? await TryReadSchemaVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            : null;

        // A NEWER schema version is refused whatever else is true, partial or whole, forced or not. The
        // recreate runs this build's script, and moving a database backwards is not a reset and not a repair.
        if (version > SqliteCatalogSchema.CurrentVersion)
        {
            throw Newer(version.Value);
        }

        if (version is long readable && SqliteCatalogSchemaInventory.IsWhole(tables, readable))
        {
            return await ReadWholeAsync(connection, transaction, readable, force, cancellationToken)
                .ConfigureAwait(false);
        }

        // Every table this build declares stands and the version still cannot be read, which is not a
        // deletion and not something a repair can be sure about.
        if (version is null && SqliteCatalogSchemaInventory.IsWhole(tables, SqliteCatalogSchema.CurrentVersion))
        {
            throw Mismatch("carrying every catalog table and no readable schema version");
        }

        if (!force)
        {
            throw Partial(tables.Count, version);
        }

        return Stood.Nothing(ContentCatalogPriorState.Unreadable);
    }

    /// <summary>A whole catalog at <paramref name="schema"/>, which is this build's version or an older one.</summary>
    static async Task<Stood> ReadWholeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long schema,
        bool force,
        CancellationToken cancellationToken)
    {
        int active;
        int versions;
        int rows;
        long drafts;
        try
        {
            active = (int)await ReadLongAsync(
                connection,
                transaction,
                "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;",
                cancellationToken).ConfigureAwait(false);
            versions = (int)await ReadLongAsync(
                connection, transaction, "SELECT COUNT(*) FROM catalog_version;", cancellationToken)
                .ConfigureAwait(false);
            rows = (int)await ReadLongAsync(
                connection, transaction, "SELECT COUNT(*) FROM catalog_row;", cancellationToken)
                .ConfigureAwait(false);
            drafts = await ReadLongAsync(
                connection, transaction, "SELECT COUNT(*) FROM catalog_draft;", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            // Only the reads BEFORE the drop are folded into the schema refusal. Every table these name
            // stands, so a failure here is a table that is not the shape its name promises, and an operator
            // can act on being told that. A provider error after the drop is a different thing entirely and
            // is left to speak for itself, having rolled the reset back on the way out.
            throw Mismatch("unreadable, so what it holds could not be checked", exception);
        }

        if (drafts > 0 && !force)
        {
            throw new ContentAuthoringException(
                "This catalog carries an OPEN DRAFT, and a reset drops it with everything else. Publish or discard the draft, or pass the reset's force flag to destroy it deliberately.",
                default,
                0,
                ContentAuthoringException.DraftOpenReason);
        }

        string? server = null;
        string? client = null;
        if (active > 0)
        {
            using SqliteCommand command = Command(
                connection,
                transaction,
                """
                SELECT server_manifest_hash, client_manifest_hash
                FROM catalog_version WHERE version_number = $version;
                """);
            command.Parameters.AddWithValue("$version", (long)active);
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                server = reader.GetString(0);
                client = reader.GetString(1);
            }
        }

        return new Stood(active, server, client, versions, rows, schema, ContentCatalogPriorState.Read);
    }

    /// <summary>
    /// The audit row's own CHECK constraints, mirrored from <c>catalog_audit</c> in
    /// <see cref="SqliteCatalogSchema.Tables"/> and applied BEFORE anything is opened or dropped.
    /// <para>
    /// The insert is the last statement of the reset, so without this an empty actor dropped the whole
    /// catalog, recreated it, and then failed on its own argument list with a raw provider exception. The
    /// catalog came back, because the transaction is all or nothing, but nothing about that is an answer a
    /// caller should have to receive for a mistake it could be told about on the way in.
    /// </para>
    /// <para>
    /// SQLite's <c>length()</c> counts every character, trailing blanks included, so these are plain string
    /// lengths. The SQL Server sibling mirrors <c>LEN</c>, which does not.
    /// </para>
    /// </summary>
    static void ValidateAuditArguments(string actor, string operatorId, string note)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);

        // CHECK (length(actor) BETWEEN 1 AND 128)
        if (actor.Length == 0)
        {
            throw new ArgumentException(
                "The reset's actor is written to catalog_audit.actor, which requires at least one character.",
                nameof(actor));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(actor.Length, ActorMaxLength, nameof(actor));

        // CHECK (length(operator) <= 128) and CHECK (length(note) <= 1024). Both may be empty.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            operatorId.Length, OperatorMaxLength, nameof(operatorId));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(note.Length, NoteMaxLength, nameof(note));
    }

    /// <summary>
    /// The ONE audit row the new store opens with. <c>catalog_audit</c> carries no foreign key to
    /// <c>catalog_version</c> and defaults every target column, so a row naming no version and no row is a
    /// shape the schema already accepts and nothing about it had to bend to record this.
    /// </summary>
    static async Task AppendResetAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actor,
        string operatorId,
        string note,
        ContentCatalogResetResult result,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            connection,
            transaction,
            """
            INSERT INTO catalog_audit(
                occurred_at_utc, actor, operator, action, before_value, version_number, note)
            VALUES ($at, $actor, $operator, $action, $before, 0, $note);
            """);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$operator", operatorId);
        command.Parameters.AddWithValue("$action", ContentAuditActions.Reset);
        command.Parameters.AddWithValue("$before", result.Summary);
        command.Parameters.AddWithValue("$note", note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task<long> ReadLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is long value ? value : throw Mismatch("missing its metadata row");
    }

    static async Task<string> ReadTextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw Mismatch("missing its metadata row after the recreate");
    }

    static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    /// <summary>The metadata table, which is the one a partial catalog's schema version can still be read from.</summary>
    const string MetadataTable = "catalog_metadata";

    /// <summary>Whether the standing tables include the named one. SQLite resolves names case insensitively.</summary>
    static bool Holds(IReadOnlyList<string> tables, string name)
    {
        for (int i = 0; i < tables.Count; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(tables[i], name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The schema version of a catalog that may be in pieces, or null when even that cannot be read. A
    /// partial catalog is expected to fail here, and the caller treats the failure as the answer.
    /// </summary>
    static async Task<long?> TryReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            using SqliteCommand command = Command(
                connection,
                transaction,
                "SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;");
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>
    /// The PARTIAL refusal: the standing tables are not the whole set any readable schema version declares,
    /// which no store can open and no read can describe. It carries its own reason token rather than
    /// <c>schema-mismatch</c>, because the remedy is not a migration. The reset itself is the remedy, and the
    /// sentence says so.
    /// </summary>
    static ContentAuthoringException Partial(int standing, long? version)
        => new(
            FormattableString.Invariant(
                $"The SQLite content catalog is PARTIAL: {standing} of the {SqliteCatalogSchemaInventory.Tables.Count} tables schema version {SqliteCatalogSchema.CurrentVersion} declares stand, which is not the whole set {Declared(version)} declares, so what it holds cannot be read. Pass the reset's force flag to drop what is left and recreate the schema at version {SqliteCatalogSchema.CurrentVersion}, which repairs it."),
            default,
            0,
            ContentAuthoringException.CatalogPartialReason);

    /// <summary>Which schema version the partial refusal measured the standing tables against.</summary>
    static string Declared(long? version)
        => version is long known
            ? FormattableString.Invariant($"the schema version '{known}' its metadata row names")
            : "any schema version";

    /// <summary>
    /// The refusal a database at a NEWER schema version gets, which no force flag overrides. It carries the
    /// schema reason token so a console keys on one answer, and names the remedy, which is a newer build
    /// rather than a migration.
    /// </summary>
    static ContentAuthoringException Newer(long version)
        => new(
            FormattableString.Invariant(
                $"The SQLite content catalog schema is at version '{version}', newer than version {SqliteCatalogSchema.CurrentVersion} this build writes, so a reset by this build would move it backwards. Nothing was dropped. Reset it from a build that writes schema version {version} or later."),
            default,
            0,
            ContentAuthoringException.SchemaMismatchReason);

    /// <summary>
    /// The schema refusal, in the words and under the reason token the provider's own validation uses, so a
    /// console that keys on <c>schema-mismatch</c> reads one answer whether the schema was wrong at open or
    /// wrong at reset.
    /// </summary>
    static ContentAuthoringException Mismatch(string actual, Exception? innerException = null)
        => new(
            FormattableString.Invariant(
                $"The SQLite content catalog schema is {actual}{Because(innerException)}. Apply migration '{SqliteCatalogSchema.RequiredMigration}'."),
            default,
            0,
            ContentAuthoringException.SchemaMismatchReason);

    static string Because(Exception? innerException)
        => innerException is null ? string.Empty : ": " + innerException.Message;

    /// <summary>
    /// What the pre-drop read found, held until the recreate has run and the result can be built once, with
    /// the new schema version and epoch, rather than built early and patched.
    /// </summary>
    readonly record struct Stood(
        int ActiveVersion,
        string? ServerManifestHash,
        string? ClientManifestHash,
        int VersionsDropped,
        int RowsDropped,
        long SchemaVersion,
        ContentCatalogPriorState State)
    {
        /// <summary>A read that found nothing it may claim, which is all an absent or partial catalog allows.</summary>
        internal static Stood Nothing(ContentCatalogPriorState state) => new(0, null, null, 0, 0, 0, state);
    }
}
