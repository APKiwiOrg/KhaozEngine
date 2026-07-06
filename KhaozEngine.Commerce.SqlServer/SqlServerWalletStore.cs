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
/// <see cref="IsolationLevel.Serializable"/> transaction. Idempotency is enforced by a composite unique index on
/// <c>(account_id, currency_id, idempotency_key)</c>: the same key used for a different account, or a different
/// currency on the same account, is a distinct operation, not a replay.</summary>
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

    private void EnsureSchema()
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using SqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
IF OBJECT_ID(N'dbo.wallet_ledger', N'U') IS NULL
CREATE TABLE dbo.wallet_ledger (
  id BIGINT IDENTITY(1,1) PRIMARY KEY,
  account_id NVARCHAR(200) NOT NULL, currency_id NVARCHAR(100) NOT NULL, delta BIGINT NOT NULL,
  idempotency_key NVARCHAR(200) NOT NULL, reason INT NOT NULL, source_ref NVARCHAR(200) NULL,
  post_balance BIGINT NOT NULL, created_at DATETIME2 NOT NULL);
IF OBJECT_ID(N'dbo.ux_ledger_idem', N'U') IS NULL AND NOT EXISTS (
  SELECT 1 FROM sys.indexes WHERE name = N'ux_ledger_idem' AND object_id = OBJECT_ID(N'dbo.wallet_ledger'))
CREATE UNIQUE INDEX ux_ledger_idem ON dbo.wallet_ledger(account_id, currency_id, idempotency_key);
IF NOT EXISTS (
  SELECT 1 FROM sys.indexes WHERE name = N'ix_ledger_acct' AND object_id = OBJECT_ID(N'dbo.wallet_ledger'))
CREATE INDEX ix_ledger_acct ON dbo.wallet_ledger(account_id, currency_id, id DESC);
IF OBJECT_ID(N'dbo.wallet_balance', N'U') IS NULL
CREATE TABLE dbo.wallet_balance (
  account_id NVARCHAR(200) NOT NULL, currency_id NVARCHAR(100) NOT NULL, amount BIGINT NOT NULL,
  updated_at DATETIME2 NOT NULL, PRIMARY KEY(account_id, currency_id));
IF OBJECT_ID(N'dbo.grant_schedule', N'U') IS NULL
CREATE TABLE dbo.grant_schedule (
  account_id NVARCHAR(200) NOT NULL, reward_id NVARCHAR(200) NOT NULL, next_available_utc DATETIME2 NOT NULL,
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
        (bool applied, bool replayed, bool _, long balance) = await Mutate(a, c, amount, key, reason, src, isDebit: false, ct);
        return new CreditResult(applied, replayed, balance);
    }

    private async Task<DebitResult> MutateForDebitAsync(AccountId a, CurrencyId c, long amount, string key,
        LedgerReason reason, string? src, CancellationToken ct)
    {
        (bool applied, bool replayed, bool insufficient, long balance) = await Mutate(a, c, amount, key, reason, src, isDebit: true, ct);
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
            long? prior = await ScalarLongAsync(conn, tx,
                "SELECT post_balance FROM dbo.wallet_ledger WHERE account_id=@a AND currency_id=@c AND idempotency_key=@k",
                ct, ("@a", a.Value), ("@c", c.Value), ("@k", key)).ConfigureAwait(false);
            if (prior is long pb)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return (false, true, false, pb);
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
                    long replayedBal = await ScalarLongAsync(conn, tx,
                        "SELECT post_balance FROM dbo.wallet_ledger WHERE account_id=@a AND currency_id=@c AND idempotency_key=@k",
                        ct, ("@a", a.Value), ("@c", c.Value), ("@k", key)).ConfigureAwait(false) ?? 0;
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return (false, true, false, replayedBal);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return (true, false, false, newBal);
            }
            else
            {
                long bal = await ScalarLongAsync(conn, tx,
                    "SELECT amount FROM dbo.wallet_balance WHERE account_id=@a AND currency_id=@c",
                    ct, ("@a", a.Value), ("@c", c.Value)).ConfigureAwait(false) ?? 0;
                long newBal = bal + amount;

                await using SqlCommand merge = conn.CreateCommand();
                merge.Transaction = tx;
                merge.CommandText = @"
MERGE dbo.wallet_balance WITH (HOLDLOCK) AS t
USING (SELECT @a AS account_id, @c AS currency_id) AS s
  ON t.account_id = s.account_id AND t.currency_id = s.currency_id
WHEN MATCHED THEN UPDATE SET amount = @amt, updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (account_id, currency_id, amount, updated_at)
  VALUES (@a, @c, @amt, SYSUTCDATETIME());";
                Bind(merge, ("@a", a.Value), ("@c", c.Value), ("@amt", newBal));
                await merge.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                try
                {
                    await InsertLedgerAsync(conn, tx, a, c, amount, key, reason, src, newBal, ct).ConfigureAwait(false);
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627)
                {
                    long replayedBal = await ScalarLongAsync(conn, tx,
                        "SELECT post_balance FROM dbo.wallet_ledger WHERE account_id=@a AND currency_id=@c AND idempotency_key=@k",
                        ct, ("@a", a.Value), ("@c", c.Value), ("@k", key)).ConfigureAwait(false) ?? 0;
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return (false, true, false, replayedBal);
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

    private static void Bind(SqlCommand cmd, params (string name, object value)[] p)
    {
        foreach ((string name, object value) in p) cmd.Parameters.AddWithValue(name, value);
    }
}
