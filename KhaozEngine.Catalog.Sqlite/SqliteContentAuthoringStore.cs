using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Catalog.Sqlite;

/// <summary>
/// The SQLite <see cref="IContentAuthoringStore"/> (spec 4.1 to 4.4): the fifteen catalog tables behind one
/// held connection, with a versioned schema that either auto-creates or validates.
/// <para>
/// <b>It sits on <see cref="SqliteStoreConnection"/> and that is not optional.</b> One held connection, one
/// semaphore gate, and a dispose that clears the provider's connection pool BEFORE closing so the file is
/// genuinely released. Every command runs under a lease from <c>EnterAsync</c>, and a transaction takes the
/// lease FIRST, because the gate is what keeps a second operation off the connection while one is open.
/// </para>
/// <para>
/// <b>The lease is not re-entrant, so no member holds one across a call back into this store.</b> Creating a
/// family and publishing both drive the allocator and the publish pipeline, which take their own leases, so
/// those members take the lease in short stretches around the call rather than wrapping it. That is also why
/// the publish commit CONFIRMS the version number inside its own transaction: nothing holds a lock across
/// steps 1 to 10, so the base really can move underneath a plan, and the confirmation is the check that
/// catches it.
/// </para>
/// <para>
/// Raw parameterized ADO.NET with <c>$name</c> parameters throughout, no EF and no ORM. Key columns are
/// <c>TEXT COLLATE BINARY</c> because comparison is ordinal always (contracts 5.3).
/// </para>
/// <para>
/// <b>There is no <c>UPDATE</c> and no <c>DELETE</c> statement for <c>catalog_remap_rule</c> in this class or
/// any of its partials.</b> Rules are append only (contracts 8.1).
/// </para>
/// </summary>
public sealed partial class SqliteContentAuthoringStore : IContentAuthoringStore, IContentIdPersistence, IDisposable
{
    /// <summary>The cap a page read is clamped to, which the seam leaves to the implementation.</summary>
    public const int MaxPageSize = 500;

    /// <summary>The active pointer of a database that has published nothing.</summary>
    public const int NoActiveVersion = 0;

    readonly SqliteStoreConnection _connection;
    readonly ContentTypeRegistry _registry;
    readonly ContentIdAllocator _allocator;
    readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Opens the database and runs the bootstrap pragma. The SCHEMA is not touched here: that is
    /// <see cref="InitializeAsync"/>, because whether an empty database may be created into is the caller's
    /// decision and a constructor cannot report it as anything but a throw.
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string, for example <c>Data Source=catalog.db</c>.</param>
    /// <param name="registry">The registry this store's types are declared in, which is where a type's id ceiling and field schema come from. Per instance, never ambient.</param>
    /// <param name="packStore">The pack target a publish writes its files to, or null on a store that only holds a draft and allocates ids.</param>
    /// <param name="clock">The clock every stamp is read from, or null for the system clock.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public SqliteContentAuthoringStore(
        string connectionString,
        ContentTypeRegistry registry,
        IPackStore? packStore = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _connection = new SqliteStoreConnection(connectionString, SqliteCatalogSchema.BootstrapSql);
        _allocator = new ContentIdAllocator(this);
        PackStore = packStore;
    }

    /// <summary>The allocator this store hands its two allocation members to.</summary>
    public ContentIdAllocator Allocator => _allocator;

    /// <summary>
    /// The pack store a publish writes to, or null on a store that can hold a draft and allocate ids and
    /// cannot publish. A publish writes files before it writes rows, so the target is not optional for it.
    /// </summary>
    public IPackStore? PackStore { get; }

    /// <inheritdoc />
    /// <remarks>
    /// It also SYNCS the registry's types into <c>catalog_type</c>, which every other table's foreign keys
    /// need, and which is where a rename or a reassignment is refused: the row pins a type id to its key.
    /// </remarks>
    public async Task InitializeAsync(
        ContentAuthoringSchemaMode mode,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        SqliteCatalogSchemaValidation.Initialize(_connection.Connection, mode);
        await SyncTypesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => (int)await ReadLongAsync(
            "SELECT schema_version FROM catalog_metadata WHERE metadata_key = 1;", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = Command("SELECT store_epoch FROM catalog_metadata WHERE metadata_key = 1;");
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw as string ?? throw NoMetadata();
    }

    /// <inheritdoc />
    public async Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => (int)await ReadLongAsync(
            "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = Command("SELECT pinned_version FROM catalog_metadata WHERE metadata_key = 1;");
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is long pinned ? (int)pinned : null;
    }

    /// <inheritdoc />
    public async Task SetPinnedVersionAsync(
        int? version,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = _connection.BeginTransaction();

        if (version is int held && !await VersionExistsAsync(held, transaction, cancellationToken).ConfigureAwait(false))
        {
            // The reason token is the one the in-memory reference store answers with, so a console that keys
            // on it reads the same answer whichever provider is behind the seam.
            throw new ContentAuthoringException(
                FormattableString.Invariant($"Version {held} does not exist, so it cannot be pinned."),
                default,
                0,
                ContentAuthoringException.UnknownTypeReason);
        }

        int? before;
        using (SqliteCommand read = Command(
            "SELECT pinned_version FROM catalog_metadata WHERE metadata_key = 1;", transaction))
        {
            object? raw = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            before = raw is long pinned ? (int)pinned : null;
        }

        using (SqliteCommand write = Command(
            """
            UPDATE catalog_metadata
            SET pinned_version = $pinned, updated_at_utc = $now
            WHERE metadata_key = 1;
            """,
            transaction))
        {
            Bind(write, "$pinned", version);
            Bind(write, "$now", Millis(_clock()));
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AppendAuditAsync(
            transaction,
            ContentAuditActions.Pin,
            actor,
            operatorId,
            default,
            0,
            default,
            string.Empty,
            Render(before),
            Render(version),
            0,
            string.Empty,
            cancellationToken).ConfigureAwait(false);

        transaction.Commit();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadVersionsAsync(null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentVersionRecord?> GetVersionAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentVersionRecord> found = await ReadVersionsAsync(versionNumber, null, cancellationToken)
            .ConfigureAwait(false);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>Closes the database, releasing the OS handle rather than parking it in the provider's pool.</summary>
    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Every published version NEWEST first, or the one named. The caller already holds the lease.
    /// </summary>
    async Task<IReadOnlyList<ContentVersionRecord>> ReadVersionsAsync(
        int? versionNumber,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT version_number, server_manifest_hash, client_manifest_hash, minimum_server_build,
                   minimum_client_build, format_generation, base_version, published_by, note, published_at_utc
            FROM catalog_version
            WHERE $version IS NULL OR version_number = $version
            ORDER BY version_number DESC;
            """,
            transaction);
        Bind(command, "$version", versionNumber);

        var versions = new List<ContentVersionRecord>();
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(new ContentVersionRecord(
                (int)reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                (int)reader.GetInt64(3),
                (int)reader.GetInt64(4),
                (int)reader.GetInt64(5),
                (int)reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9))));
        }

        return versions;
    }

    /// <summary>
    /// The registry's types written into <c>catalog_type</c>, which pins each id to its key. The caller
    /// already holds the lease.
    /// <para>
    /// A rename (the id is there under another key) and a reassignment (the key is there under another id)
    /// are both refused, because either one silently repoints every stored row of that type. The other three
    /// columns are a RECORD of the declaration this process carries and are refreshed, since the registry is
    /// the authority the publish actually reads them from.
    /// </para>
    /// </summary>
    async Task SyncTypesAsync(CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        long active = await ReadLongAsync(
            "SELECT active_version FROM catalog_metadata WHERE metadata_key = 1;", transaction, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ContentTypeRegistration> registrations = _registry.ByTypeId;
        for (int i = 0; i < registrations.Count; i++)
        {
            ContentTypeRegistration registration = registrations[i];
            await RequireTypeAgreesAsync(registration, transaction, cancellationToken).ConfigureAwait(false);

            using SqliteCommand upsert = Command(
                """
                INSERT INTO catalog_type(
                    type_id, type_key, chunk_slots, default_visibility, max_definition_id, first_seen_version)
                VALUES ($type, $key, $slots, $visibility, $ceiling, $firstSeen)
                ON CONFLICT(type_id) DO UPDATE SET
                    chunk_slots = excluded.chunk_slots,
                    default_visibility = excluded.default_visibility,
                    max_definition_id = excluded.max_definition_id;
                """,
                transaction);
            Bind(upsert, "$type", (long)registration.Type.Value);
            Bind(upsert, "$key", registration.TypeKey);
            Bind(upsert, "$slots", (long)registration.ChunkSlots);
            Bind(upsert, "$visibility", (long)registration.DefaultVisibility);
            Bind(upsert, "$ceiling", registration.MaxDefinitionId);
            Bind(upsert, "$firstSeen", active);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    async Task RequireTypeAgreesAsync(
        ContentTypeRegistration registration,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            """
            SELECT type_id, type_key FROM catalog_type
            WHERE type_id = $type OR type_key = $key;
            """,
            transaction);
        Bind(command, "$type", (long)registration.Type.Value);
        Bind(command, "$key", registration.TypeKey);

        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int storedId = (int)reader.GetInt64(0);
            string storedKey = reader.GetString(1);
            if (storedId == registration.Type.Value
                && string.Equals(storedKey, registration.TypeKey, StringComparison.Ordinal))
            {
                continue;
            }

            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"This database already pins content type {storedId} to key '{storedKey}', and the registry declares type {registration.Type.Value} as '{registration.TypeKey}'. Neither a rename nor a reassignment is possible: both repoint every row already stored under the old pairing."),
                registration.Type,
                0,
                ContentAuthoringException.UnknownTypeReason);
        }
    }

    async Task<bool> VersionExistsAsync(
        int versionNumber,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(
            "SELECT 1 FROM catalog_version WHERE version_number = $version;", transaction);
        Bind(command, "$version", (long)versionNumber);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>One scalar under a fresh lease, for the reads that are one number and nothing else.</summary>
    async Task<long> ReadLongAsync(string sql, CancellationToken cancellationToken)
    {
        using SqliteStoreLease lease = await _connection.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLongAsync(sql, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One scalar. The caller already holds the lease.</summary>
    async Task<long> ReadLongAsync(string sql, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        using SqliteCommand command = Command(sql, transaction);
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is long value ? value : throw NoMetadata();
    }

    /// <summary>A command on the held connection, enlisted in a transaction when one is open.</summary>
    SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    /// <summary>
    /// Binds one parameter, mapping a null to <see cref="DBNull"/> so a nullable column takes NULL rather
    /// than the provider throwing on a null value.
    /// </summary>
    static void Bind(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    static long Millis(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    static ContentAuthoringException NoMetadata()
        => new(
            FormattableString.Invariant(
                $"The database carries no catalog_metadata row, so it has not been initialized. Call {nameof(InitializeAsync)} first."),
            default,
            0,
            ContentAuthoringException.SchemaMismatchReason);

    static ContentAuthoringException UnknownVersion(int versionNumber)
        => new(
            FormattableString.Invariant($"This store holds no version {versionNumber}."),
            default,
            0,
            ContentAuthoringException.UnknownVersionReason);

    static ContentAuthoringException NoPackStore(string member)
        => new(
            FormattableString.Invariant(
                $"{nameof(SqliteContentAuthoringStore)}.{member} needs a pack store and this store was built with none. A publish writes files before it writes rows, so the pack target is not optional for it."),
            default,
            0,
            ContentAuthoringException.NoPackStoreReason);

    ContentTypeRegistration RequireType(ContentTypeId type)
        => _registry.TryGet(type, out ContentTypeRegistration? registration)
            ? registration
            : throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {type.Value} is not registered, so this store carries no declaration for it."),
                type,
                0,
                ContentAuthoringException.UnknownTypeReason);
}
