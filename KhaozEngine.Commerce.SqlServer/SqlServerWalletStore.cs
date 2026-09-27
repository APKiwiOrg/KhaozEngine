using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Commerce.SqlServer;

/// <summary>SQL Server / Azure SQL-backed wallet + grant-schedule store. A fresh pooled <see cref="SqlConnection"/>
/// per call, no in-process semaphore: the database serializes concurrent operations via a
/// <see cref="IsolationLevel.Serializable"/> transaction. The receipt lookup takes an update lock before the
/// balance mutation so overlapping missing-key ranges cannot deadlock against the shared balance row.
/// Idempotency is enforced by a composite unique index on
/// <c>(account_id, currency_id, idempotency_key)</c>: the same key used for a different account, or a different
/// currency on the same account, is a distinct operation, not a replay. Within that scope, the stored ledger row's
/// signed delta and reason distinguish an exact replay from an intent conflict.
/// <para>Keys are CASE SENSITIVE, matching the InMemory and SQLite backends: tables this store creates pin their
/// key columns to <c>Latin1_General_100_BIN2</c> instead of inheriting a database default that is usually
/// case-insensitive. A database whose tables predate that pin keeps its own collation, so see the package README
/// for the migration.</para></summary>
public sealed class SqlServerWalletStore : IWalletStore, IGrantScheduleStore
{
    private readonly string connectionString;

    /// <summary>Opens (bootstrapping the schema if needed) the SQL Server database at
    /// <paramref name="connectionString"/>.</summary>
    public SqlServerWalletStore(string connectionString)
    {
        this.connectionString = connectionString;
        EnsureSchema();
    }

    // Every key column is pinned to a binary collation rather than inheriting the database default. Most SQL Server
    // and Azure SQL databases default to a case-insensitive collation, under which "claim-ABC" and "claim-abc"
    // collide in ux_ledger_idem and the second call is answered as a replay of the first, silently swallowing a
    // credit or a debit. Account ids collide the same way in wallet_balance's primary key, so two accounts differing
    // only by case would share one wallet. The other two backends are case-sensitive by construction (InMemory keys
    // on ordinal string equality, SQLite's TEXT compares BINARY), so without this the same code paid out differently
    // per backend, on nothing more than what the hosting DBA had set as the database default. BIN2 compares by code
    // point, which is what ordinal and SQLite BINARY do.
    //
    // This only governs tables this store CREATES. An already-deployed table keeps whatever collation it was created
    // with (the IF OBJECT_ID guards skip it), so an existing database needs the migration in the package README.
    private const string KeyCollation = "COLLATE Latin1_General_100_BIN2";

    private void EnsureSchema()
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"
IF OBJECT_ID(N'dbo.wallet_ledger', N'U') IS NULL
CREATE TABLE dbo.wallet_ledger (
  id BIGINT IDENTITY(1,1) PRIMARY KEY,
  account_id NVARCHAR(200) {KeyCollation} NOT NULL, currency_id NVARCHAR(100) {KeyCollation} NOT NULL,
  delta BIGINT NOT NULL,
  idempotency_key NVARCHAR(200) {KeyCollation} NOT NULL, reason INT NOT NULL, source_ref NVARCHAR(200) NULL,
  post_balance BIGINT NOT NULL, created_at DATETIME2 NOT NULL);
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes WHERE name = N'ux_ledger_idem' AND object_id = OBJECT_ID(N'dbo.wallet_ledger'))
CREATE UNIQUE INDEX ux_ledger_idem ON dbo.wallet_ledger(account_id, currency_id, idempotency_key);
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes WHERE name = N'ix_ledger_acct' AND object_id = OBJECT_ID(N'dbo.wallet_ledger'))
CREATE INDEX ix_ledger_acct ON dbo.wallet_ledger(account_id, currency_id, id DESC);
IF OBJECT_ID(N'dbo.wallet_balance', N'U') IS NULL
CREATE TABLE dbo.wallet_balance (
  account_id NVARCHAR(200) {KeyCollation} NOT NULL, currency_id NVARCHAR(100) {KeyCollation} NOT NULL,
  amount BIGINT NOT NULL,
  updated_at DATETIME2 NOT NULL, PRIMARY KEY(account_id, currency_id));
IF OBJECT_ID(N'dbo.grant_schedule', N'U') IS NULL
CREATE TABLE dbo.grant_schedule (
  account_id NVARCHAR(200) {KeyCollation} NOT NULL, reward_id NVARCHAR(200) {KeyCollation} NOT NULL,
  next_available_utc DATETIME2 NOT NULL,
  PRIMARY KEY(account_id, reward_id));";
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc/>
    public Task<CreditResult> CreditAsync(AccountId account, CurrencyId currency, long amount,
        string idempotencyKey, LedgerReason reason, string? sourceRef, CancellationToken ct = default)
        => MutateForCreditAsync(account, currency, amount, idempotencyKey, reason, sourceRef, ct);

    /// <inheritdoc/>
    public Task<DebitResult> DebitAsync(AccountId account, CurrencyId currency, long amount,
        string idempotencyKey, LedgerReason reason, string? sourceRef, CancellationToken ct = default)
        => MutateForDebitAsync(account, currency, amount, idempotencyKey, reason, sourceRef, ct);

    private async Task<CreditResult> MutateForCreditAsync(AccountId a, CurrencyId c, long amount, string key,
        LedgerReason reason, string? src, CancellationToken ct)
    {
        (bool applied, bool replayed, bool _, long balance) =
            await Mutate(a, c, amount, key, reason, src, isDebit: false, ct);
        return new CreditResult(applied, replayed, balance);
    }

    private async Task<DebitResult> MutateForDebitAsync(AccountId a, CurrencyId c, long amount, string key,
        LedgerReason reason, string? src, CancellationToken ct)
    {
        (bool applied, bool replayed, bool insufficient, long balance) =
            await Mutate(a, c, amount, key, reason, src, isDebit: true, ct);
        return new DebitResult(applied, replayed, insufficient, balance);
    }

    private async Task<(bool applied, bool replayed, bool insufficient, long balance)> Mutate(
        AccountId a, CurrencyId c, long amount, string key, LedgerReason reason, string? src, bool isDebit, CancellationToken ct)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Idempotency key required.", nameof(key));

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using SqlTransaction tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false);
        try
        {
            long signedDelta = isDebit ? -amount : amount;
            // Claim the receipt key range before touching the shared balance row. A plain serializable read takes
            // RangeS-S for a missing key, which lets several transactions reach the balance UPDATE. The winner then
            // holds the balance X lock while waiting to convert its receipt range for INSERT, as the waiters retain
            // RangeS-S and wait on that balance lock. UPDLOCK makes the initial range lock exclusive to one updater,
            // preserving receipt-first replay checks while preventing that circular wait.
            Receipt? prior = await ReadReceiptAsync(conn, tx,
                "SELECT delta, reason, post_balance FROM dbo.wallet_ledger WITH (UPDLOCK) WHERE account_id=@a AND currency_id=@c AND idempotency_key=@k",
                ct, ("@a", a.Value), ("@c", c.Value), ("@k", key)).ConfigureAwait(false);
            if (prior is Receipt receipt)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return ResolveReceipt(receipt, signedDelta, reason);
            }

            if (isDebit)
            {
                await using SqlCommand upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = @"UPDATE dbo.wallet_balance SET amount = amount - @amt, updated_at = SYSUTCDATETIME()
                                     WHERE account_id=@a AND currency_id=@c AND amount >= @amt;";
                Bind(upd, ("@a", a.Value), ("@c", c.Value), ("@amt", amount));
                int rows = await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                {
                    long bal = await ScalarLongAsync(conn, tx,
                        "SELECT amount FROM dbo.wallet_balance WHERE account_id=@a AND currency_id=@c",
                        ct, ("@a", a.Value), ("@c", c.Value)).ConfigureAwait(false) ?? 0;
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return (false, false, true, bal);
                }

                long newBal = await ScalarLongAsync(conn, tx,
                    "SELECT amount FROM dbo.wallet_balance WHERE account_id=@a AND currency_id=@c",
                    ct, ("@a", a.Value), ("@c", c.Value)).ConfigureAwait(false) ?? 0;

                try
                {
                    await InsertLedgerAsync(conn, tx, a, c, -amount, key, reason, src, newBal, ct).ConfigureAwait(false);
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    Receipt winningReceipt = await ReadReceiptAsync(conn, tx,
                        "SELECT delta, reason, post_balance FROM dbo.wallet_ledger WHERE account_id=@a AND currency_id=@c AND idempotency_key=@k",
                        ct, ("@a", a.Value), ("@c", c.Value), ("@k", key)).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The conflicting wallet receipt was not found.");
                    // Duplicate-key resolution: the balance UPDATE above is relative (amount - @amt), so this
                    // transaction already applied its own debit before losing the ledger insert race. The
                    // winning transaction holds the single authoritative mutation and ledger row, so this
                    // transaction's debit must be rolled back, not committed, or the balance double-debits.
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return ResolveReceipt(winningReceipt, signedDelta, reason);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return (true, false, false, newBal);
            }
            else
            {
                // Correctness of concurrent credits rests on this atomic `amount = amount + @amt` update plus the
                // ledger's composite unique index. A duplicate-key insert below is resolved from the winning receipt.
                // This path must be exercised by the gated SQL Server tests against a live server before real-money use.
                await using SqlCommand upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = @"UPDATE dbo.wallet_balance SET amount = amount + @amt, updated_at = SYSUTCDATETIME()
                                     OUTPUT inserted.amount
                                     WHERE account_id=@a AND currency_id=@c;";
                Bind(upd, ("@a", a.Value), ("@c", c.Value), ("@amt", amount));
                await using SqlDataReader updReader = await upd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                long newBal;
                if (await updReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    newBal = updReader.GetInt64(0);
                    await updReader.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await updReader.DisposeAsync().ConfigureAwait(false);
                    await using SqlCommand ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = @"INSERT INTO dbo.wallet_balance(account_id, currency_id, amount, updated_at)
                                         VALUES (@a, @c, @amt, SYSUTCDATETIME());";
                    Bind(ins, ("@a", a.Value), ("@c", c.Value), ("@amt", amount));
                    await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    newBal = amount;
                }

                try
                {
                    await InsertLedgerAsync(conn, tx, a, c, amount, key, reason, src, newBal, ct).ConfigureAwait(false);
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    Receipt winningReceipt = await ReadReceiptAsync(conn, tx,
                        "SELECT delta, reason, post_balance FROM dbo.wallet_ledger WHERE account_id=@a AND currency_id=@c AND idempotency_key=@k",
                        ct, ("@a", a.Value), ("@c", c.Value), ("@k", key)).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The conflicting wallet receipt was not found.");
                    // Duplicate-key resolution: the balance UPDATE above is relative (amount + @amt), so this
                    // transaction already applied its own credit before losing the ledger insert race. The
                    // winning transaction holds the single authoritative mutation and ledger row, so this
                    // transaction's credit must be rolled back, not committed, or the balance double-credits.
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return ResolveReceipt(winningReceipt, signedDelta, reason);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return (true, false, false, newBal);
            }
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task InsertLedgerAsync(SqlConnection conn, SqlTransaction tx, AccountId a, CurrencyId c,
        long delta, string key, LedgerReason reason, string? src, long postBalance, CancellationToken ct)
    {
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO dbo.wallet_ledger(account_id,currency_id,delta,idempotency_key,reason,source_ref,post_balance,created_at)
                             VALUES(@a,@c,@d,@k,@r,@s,@pb,SYSUTCDATETIME());";
        Bind(cmd, ("@a", a.Value), ("@c", c.Value), ("@d", delta), ("@k", key),
            ("@r", (int)reason), ("@s", (object?)src ?? DBNull.Value), ("@pb", postBalance));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<long> GetBalanceAsync(AccountId account, CurrencyId currency, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await ScalarLongAsync(conn, null,
            "SELECT amount FROM dbo.wallet_balance WHERE account_id=@a AND currency_id=@c",
            ct, ("@a", account.Value), ("@c", currency.Value)).ConfigureAwait(false) ?? 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(AccountId account, CurrencyId currency, int limit, CancellationToken ct = default)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        List<LedgerEntry> rows = new();
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT TOP (@lim) id,delta,idempotency_key,reason,source_ref,created_at FROM dbo.wallet_ledger
                             WHERE account_id=@a AND currency_id=@c ORDER BY id DESC";
        Bind(cmd, ("@a", account.Value), ("@c", currency.Value), ("@lim", limit));
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new LedgerEntry(r.GetInt64(0), account, currency, r.GetInt64(1), r.GetString(2),
                (LedgerReason)r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4),
                new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc), TimeSpan.Zero)));
        return rows;
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> GetNextAvailableAsync(AccountId account, string rewardId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT next_available_utc FROM dbo.grant_schedule WHERE account_id=@a AND reward_id=@r";
        Bind(cmd, ("@a", account.Value), ("@r", rewardId));
        object? o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return o is null or DBNull ? null : new DateTimeOffset(DateTime.SpecifyKind((DateTime)o, DateTimeKind.Utc), TimeSpan.Zero);
    }

    /// <inheritdoc/>
    public async Task SetNextAvailableAsync(AccountId account, string rewardId, DateTimeOffset nextUtc, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
MERGE dbo.grant_schedule WITH (HOLDLOCK) AS t
USING (SELECT @a AS account_id, @r AS reward_id) AS s
  ON t.account_id = s.account_id AND t.reward_id = s.reward_id
WHEN MATCHED THEN UPDATE SET next_available_utc = @v
WHEN NOT MATCHED THEN INSERT (account_id, reward_id, next_available_utc) VALUES (@a, @r, @v);";
        Bind(cmd, ("@a", account.Value), ("@r", rewardId), ("@v", nextUtc.UtcDateTime));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<long?> ScalarLongAsync(SqlConnection conn, SqlTransaction? tx, string sql,
        CancellationToken ct, params (string, object)[] p)
    {
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, p);
        object? o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return o is null or DBNull ? null : Convert.ToInt64(o, CultureInfo.InvariantCulture);
    }

    private static async Task<Receipt?> ReadReceiptAsync(SqlConnection conn, SqlTransaction tx, string sql,
        CancellationToken ct, params (string, object)[] p)
    {
        await using SqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, p);
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new Receipt(reader.GetInt64(0), (LedgerReason)reader.GetInt32(1), reader.GetInt64(2))
            : null;
    }

    private static (bool applied, bool replayed, bool insufficient, long balance) ResolveReceipt(
        Receipt receipt, long signedDelta, LedgerReason reason)
    {
        bool conflict = receipt.Delta != signedDelta || receipt.Reason != reason;
        return (false, !conflict, false, receipt.PostBalance);
    }

    private readonly record struct Receipt(long Delta, LedgerReason Reason, long PostBalance);

    private static void Bind(SqlCommand cmd, params (string name, object value)[] p)
    {
        foreach ((string name, object value) in p) cmd.Parameters.AddWithValue(name, value);
    }
}
