using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Accounts.Sqlite;

/// <summary>
/// The SQLite <see cref="IAccountStore"/>: one flat accounts table in Grimhollow's layout behind one held,
/// unpooled connection. The zero-infra dev, test and single-node store, on the same contract as the in-memory
/// reference and the SQL Server backend.
/// </summary>
/// <remarks>
/// <para>
/// <b>Adopting an existing table.</b> The constructor creates the table when absent and adds a missing
/// <c>ban_reason</c>, <c>ban_until</c>, <c>created_at_utc</c> or <c>updated_at_utc</c> when present
/// (<see cref="SqliteAccountSchema"/> has the rules). A legacy
/// Grimhollow table declares <c>display_name NOT NULL</c>, so on that table a sign-in with no name writes an
/// empty string, and an empty string reads back as <c>null</c>. A fresh table keeps the column nullable and
/// stores exactly what it is given. A legacy row with <c>banned = 1</c> and no reason reads as a ban with an
/// empty reason, since <see cref="AccountBan.Reason"/> is never null.
/// </para>
/// <para>
/// <b>Race-safe find-or-create.</b> One <c>INSERT ... ON CONFLICT DO UPDATE ... RETURNING</c> statement, so a
/// first sign-in racing itself produces one row whether the racers share this store (the lease serializes them)
/// or run in two processes on one file (the statement is atomic).
/// </para>
/// <para>
/// <b>Row times.</b> Find-or-create stamps both times on a new row from <see cref="AccountTableOptions.TimeProvider"/>.
/// Every write sets the update time in the same statement, to the clock when a written value differs from the stored
/// one (<c>IS NOT</c>, which is NULL-safe, under <c>BINARY</c>) and to itself otherwise, so a repeat sign-in or an
/// operator write that changes nothing moves neither time. The creation time is never written again.
/// </para>
/// <para>
/// <b>Ordinal subjects.</b> Every comparison and ordering names <c>COLLATE BINARY</c>, so a column declared with
/// another collation still answers exact matches and code-point order. The upsert's conflict target cannot name a
/// collation that way, so the ensure refuses a subject KEY that is not <c>BINARY</c>, and find-or-create refuses (and
/// rolls back) a returned row whose subject is not ordinally the minted one. A ban expiry and both row times are
/// round-trip UTC text, read back with offset zero. Nothing here logs, and no message names a subject, a display name, a reason or the
/// connection string.
/// </para>
/// </remarks>
public sealed class SqliteAccountStore : IAccountStore, IDisposable
{
    private readonly SqliteStoreConnection db;
    private readonly TimeProvider clock;
    private readonly bool displayNameRequired;
    private readonly string findOrCreateSql;
    private readonly string findSql;
    private readonly string firstPageSql;
    private readonly string nextPageSql;
    private readonly string bannedSql;
    private readonly string whitelistSql;
    private readonly string banSql;
    private readonly string unbanSql;

    /// <summary>
    /// Opens the database, then creates or widens the account table. The store is usable when this returns.
    /// </summary>
    /// <param name="connectionString">A Microsoft.Data.Sqlite connection string, for example
    /// <c>Data Source=accounts.db</c>. A <c>Pooling</c> keyword is overridden to off.</param>
    /// <param name="whitelistOnCreate">Whether a newly created account is past the whitelist gate. Required, with no
    /// default, because it is the highest-consequence value in an auth composition.</param>
    /// <param name="table">Where the accounts live and the clock their times come from, or <c>null</c> for the
    /// <c>accounts</c> table and the system clock.</param>
    /// <exception cref="ArgumentException">The connection string is blank, the table name is not a plain
    /// identifier, or the clock is null.</exception>
    /// <exception cref="InvalidOperationException">The named table exists without one of the four original
    /// columns, or with a subject key that is not <c>BINARY</c> collated.</exception>
    public SqliteAccountStore(string connectionString, bool whitelistOnCreate, AccountTableOptions? table = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        AccountTableOptions resolved = table ?? new AccountTableOptions();
        string name = resolved.Table;
        string quoted = SqliteAccountIdentifier.Quote(name, nameof(table));
        clock = resolved.TimeProvider ?? throw new ArgumentNullException(nameof(table), "Time provider cannot be null.");
        WhitelistOnCreate = whitelistOnCreate;
        TableName = name;

        db = new SqliteStoreConnection(connectionString, string.Empty);
        try
        {
            displayNameRequired = SqliteAccountSchema.Ensure(db, name, quoted);
        }
        catch
        {
            db.Dispose();
            throw;
        }

        const string columns = SqliteAccountSchema.ReadColumns;
        // In the upsert's update arm and in every UPDATE, a bare column is the row as it was before the statement.
        findOrCreateSql =
            $"INSERT INTO {quoted} (subject, display_name, whitelisted, banned, created_at_utc, updated_at_utc) " +
            "VALUES ($subject, $insertName, $whitelisted, 0, $now, $now) " +
            "ON CONFLICT(subject) DO UPDATE SET display_name = COALESCE($name, display_name), " +
            $"updated_at_utc = {Stamp("$name IS NOT NULL AND display_name IS NOT $name COLLATE BINARY")} " +
            $"RETURNING {columns};";
        findSql = $"SELECT {columns} FROM {quoted} WHERE subject = $subject COLLATE BINARY;";
        firstPageSql = $"SELECT {columns} FROM {quoted} ORDER BY subject COLLATE BINARY LIMIT $limit;";
        nextPageSql =
            $"SELECT {columns} FROM {quoted} WHERE subject > $after COLLATE BINARY ORDER BY subject COLLATE BINARY LIMIT $limit;";
        bannedSql = $"SELECT {columns} FROM {quoted} WHERE banned <> 0 ORDER BY subject COLLATE BINARY;";
        whitelistSql =
            $"UPDATE {quoted} SET whitelisted = $whitelisted, updated_at_utc = {Stamp("whitelisted IS NOT $whitelisted")} " +
            $"WHERE subject = $subject COLLATE BINARY RETURNING {columns};";
        banSql =
            $"UPDATE {quoted} SET banned = 1, ban_reason = $reason, ban_until = $until, updated_at_utc = " +
            Stamp("banned IS NOT 1 OR ban_reason IS NOT $reason COLLATE BINARY OR ban_until IS NOT $until COLLATE BINARY") +
            $" WHERE subject = $subject COLLATE BINARY RETURNING {columns};";
        // The reason and the expiry go with the flag, so a lifted ban leaves nothing that reads as one in force.
        unbanSql =
            $"UPDATE {quoted} SET banned = 0, ban_reason = NULL, ban_until = NULL, updated_at_utc = " +
            Stamp("banned IS NOT 0 OR ban_reason IS NOT NULL OR ban_until IS NOT NULL") +
            $" WHERE subject = $subject COLLATE BINARY RETURNING {columns};";
    }

    // The update time a write sets: the clock when it changes a stored value, and what it was otherwise.
    private static string Stamp(string changed) => $"CASE WHEN {changed} THEN $now ELSE updated_at_utc END";

    /// <summary>Whether a newly created account is past the whitelist gate, as this store was built.</summary>
    public bool WhitelistOnCreate { get; }

    /// <summary>The table this store reads and writes, unquoted.</summary>
    public string TableName { get; }

    /// <inheritdoc />
    public async Task<AccountRecord> FindOrCreateAsync(AccountSignIn signIn, CancellationToken ct = default)
    {
        string subject = AccountStoreRules.MintSubject(signIn);
        ct.ThrowIfCancellationRequested();
        using SqliteStoreLease _ = await db.EnterAsync(ct).ConfigureAwait(false);
        // In a transaction so a returned row that is not the minted subject's is rolled back with its refreshed name.
        using SqliteTransaction tx = db.BeginTransaction();
        using SqliteCommand cmd = Command(findOrCreateSql, subject);
        cmd.Transaction = tx;
        // Two bindings of one name: what a NEW row stores (empty for none on a legacy NOT NULL table), and what a
        // repeat refreshes to (null for none, which COALESCE turns into "keep the stored name").
        cmd.Parameters.Add("$insertName", SqliteType.Text).Value = StoredName(signIn.DisplayName);
        cmd.Parameters.Add("$name", SqliteType.Text).Value = (object?)signIn.DisplayName ?? DBNull.Value;
        cmd.Parameters.Add("$whitelisted", SqliteType.Integer).Value = WhitelistOnCreate ? 1L : 0L;
        cmd.Parameters.Add("$now", SqliteType.Text).Value = FormatUtc(clock.GetUtcNow());
        AccountRecord account = await ReadOneAsync(cmd, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account upsert returned no row.");
        // The ensure refuses a key that matches under another collation, but the table can change after it ran. A
        // row whose subject is not exactly the minted one is someone else's account, and never goes back to a caller
        // that would sign a token for it. Neither subject is quoted.
        if (!string.Equals(account.Subject, subject, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The account upsert returned a different account than the sign-in names, so the table's subject key " +
                "no longer matches exactly (a collation other than BINARY). The write was rolled back.");
        tx.Commit();
        return account;
    }

    /// <inheritdoc />
    public async Task<AccountRecord?> FindAsync(string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ct.ThrowIfCancellationRequested();
        if (subject.Length > AccountStoreRules.MaxSubjectChars) return null;
        using SqliteStoreLease _ = await db.EnterAsync(ct).ConfigureAwait(false);
        using SqliteCommand cmd = Command(findSql, subject);
        return await ReadOneAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountRecord>> ListAsync(string? afterSubject = null, int limit = 500,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ct.ThrowIfCancellationRequested();
        using SqliteStoreLease _ = await db.EnterAsync(ct).ConfigureAwait(false);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = afterSubject is null ? firstPageSql : nextPageSql;
        if (afterSubject is not null) cmd.Parameters.Add("$after", SqliteType.Text).Value = afterSubject;
        cmd.Parameters.Add("$limit", SqliteType.Integer).Value = (long)limit;
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountRecord>> ListBannedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using SqliteStoreLease _ = await db.EnterAsync(ct).ConfigureAwait(false);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = bannedSql;
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AccountRecord?> SetWhitelistedAsync(string subject, bool whitelisted, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        return UpdateAsync(whitelistSql, subject,
            cmd => cmd.Parameters.Add("$whitelisted", SqliteType.Integer).Value = whitelisted ? 1L : 0L, ct);
    }

    /// <inheritdoc />
    public Task<AccountRecord?> BanAsync(string subject, string reason, DateTimeOffset? until,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        AccountStoreRules.ValidateBanReason(reason);
        return UpdateAsync(banSql, subject, cmd =>
        {
            cmd.Parameters.Add("$reason", SqliteType.Text).Value = reason;
            cmd.Parameters.Add("$until", SqliteType.Text).Value = until is { } at ? FormatUtc(at) : (object)DBNull.Value;
        }, ct);
    }

    /// <inheritdoc />
    public Task<AccountRecord?> UnbanAsync(string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        return UpdateAsync(unbanSql, subject, static _ => { }, ct);
    }

    /// <summary>Closes the database. The connection is in no pool, so the OS handle on the file is released and no
    /// other connection on the file is touched.</summary>
    public void Dispose() => db.Dispose();

    // Every write names an existing account and none creates one: no row updated is an unknown subject, null.
    private async Task<AccountRecord?> UpdateAsync(string sql, string subject, Action<SqliteCommand> bind,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (subject.Length > AccountStoreRules.MaxSubjectChars) return null;
        using SqliteStoreLease _ = await db.EnterAsync(ct).ConfigureAwait(false);
        using SqliteCommand cmd = Command(sql, subject);
        cmd.Parameters.Add("$now", SqliteType.Text).Value = FormatUtc(clock.GetUtcNow());
        bind(cmd);
        return await ReadOneAsync(cmd, ct).ConfigureAwait(false);
    }

    private SqliteCommand Command(string sql, string subject)
    {
        SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add("$subject", SqliteType.Text).Value = subject;
        return cmd;
    }

    private object StoredName(string? displayName) =>
        (object?)displayName ?? (displayNameRequired ? string.Empty : DBNull.Value);

    private async Task<AccountRecord?> ReadOneAsync(SqliteCommand cmd, CancellationToken ct)
    {
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    private async Task<IReadOnlyList<AccountRecord>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var rows = new List<AccountRecord>();
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Read(reader));
        return rows;
    }

    private AccountRecord Read(SqliteDataReader reader)
    {
        string? name = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (displayNameRequired && name is { Length: 0 }) name = null;
        AccountBan? ban = reader.GetInt64(3) != 0
            ? new AccountBan(
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                ReadUtc(reader, 5, "ban_until"))
            : null;
        return new AccountRecord(reader.GetString(0), name, reader.GetInt64(2) != 0, ban)
        {
            CreatedAtUtc = ReadUtc(reader, 6, "created_at_utc"),
            UpdatedAtUtc = ReadUtc(reader, 7, "updated_at_utc"),
        };
    }

    // Round-trip UTC text, Grimhollow's format. Once every value is UTC it sorts as it sorts in time.
    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    // A NULL is null. Anything else must parse, and a value that does not is refused without being echoed.
    private static DateTimeOffset? ReadUtc(SqliteDataReader reader, int ordinal, string column)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
            ? at.ToUniversalTime()
            : throw new InvalidOperationException(
                $"A {column} value in the account table is not a date and time. The store writes round-trip UTC " +
                "text, so the row was written by something else.");
    }
}
