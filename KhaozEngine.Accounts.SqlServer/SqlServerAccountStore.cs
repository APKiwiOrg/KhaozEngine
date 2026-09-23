using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Accounts.SqlServer;

/// <summary>
/// The SQL Server and Azure SQL <see cref="IAccountStore"/>: one flat accounts table in Grimhollow's layout behind a
/// fresh pooled connection per call. The production and shared store, on the same contract as the in-memory
/// reference and the SQLite backend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy bootstrap.</b> The constructor opens nothing. The first call opens a connection and runs the schema
/// ensure under the configured <see cref="AccountSchemaMode"/> (<see cref="SqlServerAccountSchema"/> has the rules),
/// so an auto-paused Azure SQL database costs the first caller a wait instead of costing the host its start. A
/// failed bootstrap is retried by the next call.
/// </para>
/// <para>
/// <b>Adopting an existing table.</b> A legacy Grimhollow table declares <c>display_name NOT NULL</c>, so on that
/// table a sign-in with no name writes an empty string, and an empty string reads back as <c>null</c>. A fresh
/// table keeps the column nullable and stores exactly what it is given. A legacy row with <c>banned = 1</c> and no
/// reason reads as a ban with an empty reason, since <see cref="AccountBan.Reason"/> is never null.
/// </para>
/// <para>
/// <b>Race-safe find-or-create.</b> One <c>MERGE ... WITH (HOLDLOCK)</c>, whose key-range lock is what stops two
/// simultaneous first sign-ins from both taking the insert branch, retried on a deadlock. No in-process gate: the
/// racers are as likely to be two hosts as two threads, and the database serializes both.
/// </para>
/// <para>
/// <b>Ordinal subjects on any table.</b> An exact match compares the subject's bytes and an ordering names
/// <c>Latin1_General_100_BIN2</c>, beside the plain predicate an index can seek on, so a legacy table in a
/// case-insensitive database default still answers exact matches and code-point order. Such a table cannot hold
/// two subjects its own collation calls equal, which is a limit of that table and not of the store. A ban expiry is
/// written in UTC and read back with offset zero. Nothing here logs, and no message names a subject, a display name,
/// a reason or the connection string.
/// </para>
/// </remarks>
public sealed class SqlServerAccountStore : IAccountStore
{
    private const int DeadlockVictim = 1205;
    private const int MaxFindOrCreateAttempts = 3;

    // An exact match on the subject's UTF-16 bytes: case and trailing spaces both count, whatever the column's
    // collation, which a plain equality under SQL Server's padded comparison would not guarantee.
    private const string ExactSubject = "CAST(subject AS varbinary(max)) = CAST(@subject AS varbinary(max))";
    private const string Inserted =
        "inserted.subject, inserted.display_name, inserted.whitelisted, inserted.banned, inserted.ban_reason, inserted.ban_until";

    private readonly string connectionString;
    private readonly string schemaName;
    private readonly string qualified;
    private readonly SemaphoreSlim schemaGate = new(1, 1);
    // Published once the bootstrap succeeds. Volatile because the fast path reads it outside the gate.
    private volatile TableShape? shape;

    private readonly string findOrCreateSql;
    private readonly string findSql;
    private readonly string firstPageSql;
    private readonly string nextPageSql;
    private readonly string bannedSql;
    private readonly string whitelistSql;
    private readonly string banSql;
    private readonly string unbanSql;

    /// <summary>Records the target and validates the names. Opens nothing: see the lazy bootstrap above.</summary>
    /// <param name="connectionString">A Microsoft.Data.SqlClient connection string.</param>
    /// <param name="whitelistOnCreate">Whether a newly created account is past the whitelist gate. Required, with no
    /// default, because it is the highest-consequence value in an auth composition.</param>
    /// <param name="options">Where the accounts live and what the first call may do, or <c>null</c> for
    /// <c>dbo.accounts</c> under <see cref="AccountSchemaMode.AutoCreate"/>.</param>
    /// <exception cref="ArgumentException">The connection string is blank, a name is not a plain identifier, or the
    /// schema mode is undefined.</exception>
    public SqlServerAccountStore(string connectionString, bool whitelistOnCreate,
        SqlServerAccountStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SqlServerAccountStoreOptions resolved = options ?? new SqlServerAccountStoreOptions();
        string quotedSchema = SqlServerAccountIdentifier.Quote(resolved.Schema, "schema", nameof(options));
        string quotedTable = SqlServerAccountIdentifier.Quote(resolved.Table, "table", nameof(options));
        if (!Enum.IsDefined(resolved.SchemaMode))
            throw new ArgumentOutOfRangeException(nameof(options), resolved.SchemaMode, "The schema mode is not defined.");

        this.connectionString = connectionString;
        WhitelistOnCreate = whitelistOnCreate;
        Options = resolved;
        schemaName = resolved.Schema;
        qualified = quotedSchema + "." + quotedTable;

        const string columns = SqlServerAccountSchema.ReadColumns;
        const string ordinal = "subject COLLATE " + SqlServerAccountSchema.SubjectCollation;
        findOrCreateSql = $"""
            MERGE {qualified} WITH (HOLDLOCK) AS target
            USING (SELECT @subject AS subject) AS source
                ON target.subject = source.subject
                AND CAST(target.subject AS varbinary(max)) = CAST(source.subject AS varbinary(max))
            WHEN MATCHED THEN UPDATE SET display_name = COALESCE(@name, target.display_name)
            WHEN NOT MATCHED THEN INSERT (subject, display_name, whitelisted, banned)
                VALUES (@subject, @insertName, @whitelisted, 0)
            OUTPUT {Inserted};
            """;
        findSql = $"SELECT {columns} FROM {qualified} WHERE subject = @subject AND {ExactSubject};";
        firstPageSql = $"SELECT TOP (@limit) {columns} FROM {qualified} ORDER BY {ordinal};";
        nextPageSql = $"SELECT TOP (@limit) {columns} FROM {qualified} WHERE {ordinal} > @after ORDER BY {ordinal};";
        bannedSql = $"SELECT {columns} FROM {qualified} WHERE banned = 1 ORDER BY {ordinal};";
        whitelistSql =
            $"UPDATE {qualified} SET whitelisted = @whitelisted OUTPUT {Inserted} WHERE subject = @subject AND {ExactSubject};";
        banSql =
            $"UPDATE {qualified} SET banned = 1, ban_reason = @reason, ban_until = @until OUTPUT {Inserted} " +
            $"WHERE subject = @subject AND {ExactSubject};";
        // The reason and the expiry go with the flag, so a lifted ban leaves nothing that reads as one in force.
        unbanSql =
            $"UPDATE {qualified} SET banned = 0, ban_reason = NULL, ban_until = NULL OUTPUT {Inserted} " +
            $"WHERE subject = @subject AND {ExactSubject};";
    }

    /// <summary>Whether a newly created account is past the whitelist gate, as this store was built.</summary>
    public bool WhitelistOnCreate { get; }

    /// <summary>The schema, table and schema mode this store was built with.</summary>
    public SqlServerAccountStoreOptions Options { get; }

    /// <inheritdoc />
    public async Task<AccountRecord> FindOrCreateAsync(AccountSignIn signIn, CancellationToken ct = default)
    {
        string subject = AccountStoreRules.MintSubject(signIn);
        string? name = signIn.DisplayName;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await RunAsync(async (connection, table, token) =>
                {
                    await using SqlCommand cmd = Command(connection, findOrCreateSql, subject);
                    // Two bindings of one name: what a NEW row stores (empty for none on a legacy NOT NULL table),
                    // and what a repeat refreshes to (null for none, which COALESCE turns into "keep the stored name").
                    cmd.Parameters.Add("@insertName", SqlDbType.NVarChar, AccountStoreRules.MaxDisplayNameChars).Value =
                        (object?)name ?? (table.DisplayNameRequired ? string.Empty : DBNull.Value);
                    cmd.Parameters.Add("@name", SqlDbType.NVarChar, AccountStoreRules.MaxDisplayNameChars).Value =
                        (object?)name ?? DBNull.Value;
                    cmd.Parameters.Add("@whitelisted", SqlDbType.Bit).Value = WhitelistOnCreate;
                    return await ReadOneAsync(cmd, table, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The account upsert returned no row.");
                }, ct).ConfigureAwait(false);
            }
            catch (SqlException exception) when (IsRetryable(exception) && attempt < MaxFindOrCreateAttempts)
            {
                // The MERGE is idempotent, so running it again is the whole recovery.
            }
            catch (SqlException exception) when (IsDuplicateKey(exception))
            {
                // Not wrapped: the provider's message quotes the duplicate key, which is the subject.
                throw new InvalidOperationException(
                    $"The account could not be created (SQL error {exception.Number}): its subject collides with an " +
                    "existing one under the table's own collation. A legacy table keeps its collation, so two subjects " +
                    "it calls equal cannot both exist there.");
            }
        }
    }

    /// <inheritdoc />
    public Task<AccountRecord?> FindAsync(string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ct.ThrowIfCancellationRequested();
        if (subject.Length > AccountStoreRules.MaxSubjectChars) return Task.FromResult<AccountRecord?>(null);
        return RunAsync(async (connection, table, token) =>
        {
            await using SqlCommand cmd = Command(connection, findSql, subject);
            return await ReadOneAsync(cmd, table, token).ConfigureAwait(false);
        }, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AccountRecord>> ListAsync(string? afterSubject = null, int limit = 500,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return RunAsync(async (connection, table, token) =>
        {
            await using SqlCommand cmd = connection.CreateCommand();
            cmd.CommandText = afterSubject is null ? firstPageSql : nextPageSql;
            cmd.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
            // No stored subject is longer than the limit, and for such subjects "after the whole cursor" and
            // "after its first 128 code units" select the same rows, so the cut is exact rather than lossy.
            if (afterSubject is not null)
                cmd.Parameters.Add("@after", SqlDbType.NVarChar, AccountStoreRules.MaxSubjectChars).Value =
                    afterSubject.Length > AccountStoreRules.MaxSubjectChars
                        ? afterSubject[..AccountStoreRules.MaxSubjectChars]
                        : afterSubject;
            return await ReadAllAsync(cmd, table, token).ConfigureAwait(false);
        }, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AccountRecord>> ListBannedAsync(CancellationToken ct = default) =>
        RunAsync(async (connection, table, token) =>
        {
            await using SqlCommand cmd = connection.CreateCommand();
            cmd.CommandText = bannedSql;
            return await ReadAllAsync(cmd, table, token).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    public Task<AccountRecord?> SetWhitelistedAsync(string subject, bool whitelisted, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        return UpdateAsync(whitelistSql, subject,
            cmd => cmd.Parameters.Add("@whitelisted", SqlDbType.Bit).Value = whitelisted, ct);
    }

    /// <inheritdoc />
    public Task<AccountRecord?> BanAsync(string subject, string reason, DateTimeOffset? until,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        AccountStoreRules.ValidateBanReason(reason);
        return UpdateAsync(banSql, subject, cmd =>
        {
            cmd.Parameters.Add("@reason", SqlDbType.NVarChar, AccountStoreRules.MaxBanReasonChars).Value = reason;
            cmd.Parameters.Add("@until", SqlDbType.DateTimeOffset).Value =
                until is { } at ? at.ToUniversalTime() : (object)DBNull.Value;
        }, ct);
    }

    /// <inheritdoc />
    public Task<AccountRecord?> UnbanAsync(string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        return UpdateAsync(unbanSql, subject, static _ => { }, ct);
    }

    // Every write names an existing account and none creates one: no row updated is an unknown subject, null.
    private Task<AccountRecord?> UpdateAsync(string sql, string subject, Action<SqlCommand> bind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (subject.Length > AccountStoreRules.MaxSubjectChars) return Task.FromResult<AccountRecord?>(null);
        return RunAsync(async (connection, table, token) =>
        {
            await using SqlCommand cmd = Command(connection, sql, subject);
            bind(cmd);
            return await ReadOneAsync(cmd, table, token).ConfigureAwait(false);
        }, ct);
    }

    // One pooled connection per call, the bootstrap on the first, and a cancelled call surfacing as a cancellation
    // whatever the provider threw for it.
    private async Task<T> RunAsync<T>(Func<SqlConnection, TableShape, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            TableShape table = shape ?? await BootstrapAsync(connection, ct).ConfigureAwait(false);
            return await body(connection, table, ct).ConfigureAwait(false);
        }
        catch (SqlException exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The account store call was cancelled.", exception, ct);
        }
    }

    private async Task<TableShape> BootstrapAsync(SqlConnection connection, CancellationToken ct)
    {
        await schemaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (shape is { } known) return known;
            bool displayNameRequired = await SqlServerAccountSchema
                .EnsureAsync(connection, schemaName, qualified, Options.SchemaMode, ct).ConfigureAwait(false);
            var bootstrapped = new TableShape(displayNameRequired);
            shape = bootstrapped;
            return bootstrapped;
        }
        finally
        {
            schemaGate.Release();
        }
    }

    private static SqlCommand Command(SqlConnection connection, string sql, string subject)
    {
        SqlCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add("@subject", SqlDbType.NVarChar, AccountStoreRules.MaxSubjectChars).Value = subject;
        return cmd;
    }

    private static bool IsDuplicateKey(SqlException exception) => exception.Number is 2601 or 2627;

    private static bool IsRetryable(SqlException exception) =>
        exception.Number == DeadlockVictim || IsDuplicateKey(exception);

    private static async Task<AccountRecord?> ReadOneAsync(SqlCommand cmd, TableShape table, CancellationToken ct)
    {
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader, table) : null;
    }

    private static async Task<IReadOnlyList<AccountRecord>> ReadAllAsync(SqlCommand cmd, TableShape table,
        CancellationToken ct)
    {
        var rows = new List<AccountRecord>();
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Read(reader, table));
        return rows;
    }

    private static AccountRecord Read(SqlDataReader reader, TableShape table)
    {
        string? name = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (table.DisplayNameRequired && name is { Length: 0 }) name = null;
        AccountBan? ban = reader.GetBoolean(3)
            ? new AccountBan(
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5).ToUniversalTime())
            : null;
        return new AccountRecord(reader.GetString(0), name, reader.GetBoolean(2), ban);
    }

    // What the bootstrap learned about the table that every read and write needs.
    private sealed record TableShape(bool DisplayNameRequired);
}
