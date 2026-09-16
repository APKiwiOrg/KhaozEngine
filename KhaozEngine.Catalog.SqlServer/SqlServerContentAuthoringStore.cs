using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// One open connection and the transaction, if any, every command taken on it enlists in. It stands in for
/// the SQLite provider's lease: there is no held connection here, so a call passes its own connection down to
/// the helpers it uses instead of taking a gate.
/// </summary>
/// <param name="Connection">The open connection.</param>
/// <param name="Transaction">The open transaction, or null for a plain read.</param>
internal readonly record struct SqlServerCatalogScope(SqlConnection Connection, SqlTransaction? Transaction);

/// <summary>
/// The SQL Server and Azure SQL <see cref="IContentAuthoringStore"/> (spec 4.1, 4.2, 4.5): the fourteen
/// catalog tables behind a fresh pooled connection per call, with a versioned schema that either auto-creates
/// or validates.
/// <para>
/// <b>It takes no in-process gate, and that is the one genuine behavioural difference from the SQLite
/// provider.</b> SQLite serializes in process behind one held connection and a semaphore, which is correct for
/// the single-node case it exists for. This backend exists for the shared case, where the second console is in
/// another process and often on another machine, so an in-process gate would guard nothing. The database does
/// the serializing instead, through an <see cref="IsolationLevel.Serializable"/> transaction, and two consoles
/// publishing at once are a deadlock or an abort rather than a race.
/// </para>
/// <para>
/// <b>A serialization failure surfaces as the 409 the optimistic check already produces.</b> SQL error 1205
/// (deadlock victim) and 3960 (snapshot update conflict) both mean the same thing to a caller as a base
/// version that moved: the plan was built over a state that is no longer there, and the answer is to re-read
/// and try again. Giving them a second reason token would make one condition two things for a console to
/// handle.
/// </para>
/// <para>
/// Raw parameterized ADO.NET with <c>@name</c> parameters throughout, no EF and no ORM. Key columns are
/// <c>nvarchar(N) COLLATE Latin1_General_100_BIN2</c> because comparison is ordinal always (contracts 5.3): a
/// case-insensitive database default is the usual SQL Server setting and it silently merged two accounts once.
/// </para>
/// <para>
/// <b>There is no <c>UPDATE</c> and no <c>DELETE</c> statement for <c>catalog_remap_rule</c> in this class or
/// any of its partials.</b> Rules are append only (contracts 8.1).
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore : IContentAuthoringStore, IContentIdPersistence
{
    /// <summary>The cap a page read is clamped to, which the seam leaves to the implementation.</summary>
    public const int MaxPageSize = 500;

    /// <summary>The active pointer of a database that has published nothing.</summary>
    public const int NoActiveVersion = 0;

    readonly string _connectionString;
    readonly ContentTypeRegistry _registry;
    readonly ContentIdAllocator _allocator;
    readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Records the target. NOTHING is opened here and the SCHEMA is not touched: that is
    /// <see cref="InitializeAsync"/>, because whether a database carrying no catalog table may be created into
    /// is the caller's decision and a constructor cannot report it as anything but a throw.
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string, for example <c>Server=.;Database=catalog;Integrated Security=true;TrustServerCertificate=true</c>.</param>
    /// <param name="registry">The registry this store's types are declared in, which is where a type's id ceiling and field schema come from. Per instance, never ambient.</param>
    /// <param name="packStore">The pack target a publish writes its files to, or null on a store that only holds a draft and allocates ids.</param>
    /// <param name="clock">The clock every stamp is read from, or null for the system clock.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public SqlServerContentAuthoringStore(
        string connectionString,
        ContentTypeRegistry registry,
        IPackStore? packStore = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(registry);

        _connectionString = connectionString;
        _registry = registry;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
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
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqlServerCatalogSchemaValidation.InitializeAsync(connection, mode, cancellationToken)
            .ConfigureAwait(false);
        await SyncTypesAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => ReadAsync(
            static (scope, token) => ReadIntAsync(
                scope, "SELECT schema_version FROM dbo.catalog_metadata WHERE metadata_key = 1;", token),
            cancellationToken);

    /// <inheritdoc />
    public Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default)
        => ReadAsync(
            static async (scope, token) =>
            {
                await using SqlCommand command = Command(
                    scope, "SELECT store_epoch FROM dbo.catalog_metadata WHERE metadata_key = 1;");
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string
                    ?? throw NoMetadata();
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
        => ReadAsync(
            static (scope, token) => ReadActiveVersionAsync(scope, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
        => ReadAsync(
            static async (scope, token) =>
            {
                await using SqlCommand command = Command(
                    scope, "SELECT pinned_version FROM dbo.catalog_metadata WHERE metadata_key = 1;");
                object? raw = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return raw is int pinned ? pinned : (int?)null;
            },
            cancellationToken);

    /// <inheritdoc />
    public Task SetPinnedVersionAsync(
        int? version,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);

        return WriteAsync(
            async (scope, token) =>
            {
                if (version is int held
                    && !await VersionExistsAsync(scope, held, token).ConfigureAwait(false))
                {
                    // The reason token is the one the in-memory reference store answers with, defect and all
                    // (https://github.com/APKiwiOrg/KhaozEngine/issues/919), so a console that keys on it
                    // reads the same answer whichever provider is behind the seam.
                    throw new ContentAuthoringException(
                        FormattableString.Invariant($"Version {held} does not exist, so it cannot be pinned."),
                        default,
                        0,
                        ContentAuthoringException.UnknownTypeReason);
                }

                int? before;
                await using (SqlCommand read = Command(
                    scope, "SELECT pinned_version FROM dbo.catalog_metadata WHERE metadata_key = 1;"))
                {
                    object? raw = await read.ExecuteScalarAsync(token).ConfigureAwait(false);
                    before = raw is int pinned ? pinned : null;
                }

                await using (SqlCommand write = Command(
                    scope,
                    """
                    UPDATE dbo.catalog_metadata
                    SET pinned_version = @pinned, updated_at_utc = @now
                    WHERE metadata_key = 1;
                    """))
                {
                    BindInt(write, "@pinned", version);
                    BindTime(write, "@now", _clock());
                    await write.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await AppendAuditAsync(
                    scope,
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
                    token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(
        CancellationToken cancellationToken = default)
        => ReadAsync(static (scope, token) => ReadVersionsAsync(scope, null, token), cancellationToken);

    /// <inheritdoc />
    public Task<ContentVersionRecord?> GetVersionAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
        => ReadAsync(
            async (scope, token) =>
            {
                IReadOnlyList<ContentVersionRecord> found = await ReadVersionsAsync(scope, versionNumber, token)
                    .ConfigureAwait(false);
                return found.Count == 0 ? null : found[0];
            },
            cancellationToken);

    /// <summary>
    /// Every published version NEWEST first, or the one named. The caller owns the scope.
    /// </summary>
    static async Task<IReadOnlyList<ContentVersionRecord>> ReadVersionsAsync(
        SqlServerCatalogScope scope,
        int? versionNumber,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT version_number, server_manifest_hash, client_manifest_hash, minimum_server_build,
                   minimum_client_build, format_generation, base_version, published_by, note, published_at_utc
            FROM dbo.catalog_version
            WHERE @version IS NULL OR version_number = @version
            ORDER BY version_number DESC;
            """);
        BindInt(command, "@version", versionNumber);

        var versions = new List<ContentVersionRecord>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            versions.Add(new ContentVersionRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetDateTimeOffset(9)));
        }

        return versions;
    }

    /// <summary>
    /// The registry's types written into <c>catalog_type</c>, which pins each id to its key.
    /// <para>
    /// A rename (the id is there under another key) and a reassignment (the key is there under another id)
    /// are both refused, because either one silently repoints every stored row of that type. The other three
    /// columns are a RECORD of the declaration this process carries and are refreshed, since the registry is
    /// the authority the publish actually reads them from.
    /// </para>
    /// </summary>
    async Task SyncTypesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using SqlTransaction transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var scope = new SqlServerCatalogScope(connection, transaction);
        int active = await ReadActiveVersionAsync(scope, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ContentTypeRegistration> registrations = _registry.ByTypeId;
        for (int i = 0; i < registrations.Count; i++)
        {
            ContentTypeRegistration registration = registrations[i];
            await RequireTypeAgreesAsync(scope, registration, cancellationToken).ConfigureAwait(false);

            await using SqlCommand upsert = Command(
                scope,
                """
                MERGE dbo.catalog_type WITH (HOLDLOCK) AS target
                USING (SELECT @type AS type_id) AS source ON target.type_id = source.type_id
                WHEN MATCHED THEN UPDATE SET
                    chunk_slots = @slots,
                    default_visibility = @visibility,
                    max_definition_id = @ceiling
                WHEN NOT MATCHED THEN INSERT (
                    type_id, type_key, chunk_slots, default_visibility, max_definition_id, first_seen_version)
                    VALUES (@type, @key, @slots, @visibility, @ceiling, @firstSeen);
                """);
            BindInt(upsert, "@type", (int)registration.Type.Value);
            BindText(upsert, "@key", registration.TypeKey);
            BindInt(upsert, "@slots", registration.ChunkSlots);
            BindInt(upsert, "@visibility", (int)registration.DefaultVisibility);
            BindInt(upsert, "@ceiling", registration.MaxDefinitionId);
            BindInt(upsert, "@firstSeen", active);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task RequireTypeAgreesAsync(
        SqlServerCatalogScope scope,
        ContentTypeRegistration registration,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope,
            """
            SELECT type_id, type_key FROM dbo.catalog_type
            WHERE type_id = @type OR type_key = @key;
            """);
        BindInt(command, "@type", (int)registration.Type.Value);
        BindText(command, "@key", registration.TypeKey);

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int storedId = reader.GetInt32(0);
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

    static async Task<bool> VersionExistsAsync(
        SqlServerCatalogScope scope,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(
            scope, "SELECT 1 FROM dbo.catalog_version WHERE version_number = @version;");
        BindInt(command, "@version", versionNumber);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    static Task<int> ReadActiveVersionAsync(SqlServerCatalogScope scope, CancellationToken cancellationToken)
        => ReadIntAsync(
            scope, "SELECT active_version FROM dbo.catalog_metadata WHERE metadata_key = 1;", cancellationToken);

    /// <summary>One int scalar. The caller owns the scope.</summary>
    static async Task<int> ReadIntAsync(
        SqlServerCatalogScope scope,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(scope, sql);
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return raw is int value ? value : throw NoMetadata();
    }

    /// <summary>A command on the scope's connection, enlisted in its transaction when one is open.</summary>
    static SqlCommand Command(SqlServerCatalogScope scope, string sql)
    {
        SqlCommand command = scope.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = scope.Transaction;
        return command;
    }

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
                $"{nameof(SqlServerContentAuthoringStore)}.{member} needs a pack store and this store was built with none. A publish writes files before it writes rows, so the pack target is not optional for it."),
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
