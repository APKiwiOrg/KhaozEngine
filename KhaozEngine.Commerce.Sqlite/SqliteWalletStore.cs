using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Commerce.Sqlite;

/// <summary>SQLite-backed wallet + grant-schedule store. Single connection, semaphore-serialized. Idempotency is
/// enforced by a composite unique index on <c>(account_id, currency_id, idempotency_key)</c>: the same key used
/// for a different account, or a different currency on the same account, is a distinct operation, not a replay.</summary>
public sealed class SqliteWalletStore : IWalletStore, IGrantScheduleStore, IDisposable
{
    private readonly SqliteConnection conn;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Opens (creating if needed) the SQLite database at <paramref name="connectionString"/> and
    /// bootstraps the schema.</summary>
    public SqliteWalletStore(string connectionString)
    {
        conn = new SqliteConnection(connectionString);
        conn.Open();
        Exec(@"CREATE TABLE IF NOT EXISTS wallet_ledger (
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 account_id TEXT NOT NULL, currency_id TEXT NOT NULL, delta INTEGER NOT NULL,
                 idempotency_key TEXT NOT NULL, reason INTEGER NOT NULL, source_ref TEXT NULL,
                 post_balance INTEGER NOT NULL, created_at INTEGER NOT NULL);
               CREATE UNIQUE INDEX IF NOT EXISTS ux_ledger_idem ON wallet_ledger(account_id, currency_id, idempotency_key);
               CREATE INDEX IF NOT EXISTS ix_ledger_acct ON wallet_ledger(account_id, currency_id, id DESC);
               CREATE TABLE IF NOT EXISTS wallet_balance (
                 account_id TEXT NOT NULL, currency_id TEXT NOT NULL, amount INTEGER NOT NULL,
                 updated_at INTEGER NOT NULL, PRIMARY KEY(account_id, currency_id));
               CREATE TABLE IF NOT EXISTS grant_schedule (
                 account_id TEXT NOT NULL, reward_id TEXT NOT NULL, next_available_utc INTEGER NOT NULL,
                 PRIMARY KEY(account_id, reward_id));");
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
        await gate.WaitAsync(ct);
        try
        {
            using SqliteTransaction tx = conn.BeginTransaction();
            long? prior = ScalarLong(tx, "SELECT post_balance FROM wallet_ledger WHERE account_id=$a AND currency_id=$c AND idempotency_key=$k",
                ("$a", a.Value), ("$c", c.Value), ("$k", key));
            if (prior is long pb)
            {
                tx.Commit();
                return (false, true, false, pb);
            }
            long bal = ScalarLong(tx, "SELECT amount FROM wallet_balance WHERE account_id=$a AND currency_id=$c",
                ("$a", a.Value), ("$c", c.Value)) ?? 0;
            if (isDebit && bal < amount) { tx.Rollback(); return (false, false, true, bal); }
            long newBal = isDebit ? bal - amount : bal + amount;
            Exec(tx, @"INSERT INTO wallet_balance(account_id,currency_id,amount,updated_at)
                       VALUES($a,$c,$amt,$now)
                       ON CONFLICT(account_id,currency_id) DO UPDATE SET amount=$amt, updated_at=$now",
                ("$a", a.Value), ("$c", c.Value), ("$amt", newBal), ("$now", Now()));
            Exec(tx, @"INSERT INTO wallet_ledger(account_id,currency_id,delta,idempotency_key,reason,source_ref,post_balance,created_at)
                       VALUES($a,$c,$d,$k,$r,$s,$pb,$now)",
                ("$a", a.Value), ("$c", c.Value), ("$d", isDebit ? -amount : amount), ("$k", key),
                ("$r", (long)reason), ("$s", (object?)src ?? DBNull.Value), ("$pb", newBal), ("$now", Now()));
            tx.Commit();
            return (true, false, false, newBal);
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<long> GetBalanceAsync(AccountId account, CurrencyId currency, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return ScalarLong(null, "SELECT amount FROM wallet_balance WHERE account_id=$a AND currency_id=$c", ("$a", account.Value), ("$c", currency.Value)) ?? 0; }
        finally { gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LedgerEntry>> GetLedgerAsync(AccountId account, CurrencyId currency, int limit, CancellationToken ct = default)
    {
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        await gate.WaitAsync(ct);
        try
        {
            List<LedgerEntry> rows = new();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id,delta,idempotency_key,reason,source_ref,created_at FROM wallet_ledger
                                WHERE account_id=$a AND currency_id=$c ORDER BY id DESC LIMIT $lim";
            Bind(cmd, ("$a", account.Value), ("$c", currency.Value), ("$lim", (long)limit));
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new LedgerEntry(r.GetInt64(0), account, currency, r.GetInt64(1), r.GetString(2),
                    (LedgerReason)r.GetInt32(3), r.IsDBNull(4) ? null : r.GetString(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5))));
            return rows;
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> GetNextAvailableAsync(AccountId account, string rewardId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            long? v = ScalarLong(null, "SELECT next_available_utc FROM grant_schedule WHERE account_id=$a AND reward_id=$r", ("$a", account.Value), ("$r", rewardId));
            return v is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
        finally { gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task SetNextAvailableAsync(AccountId account, string rewardId, DateTimeOffset nextUtc, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Exec(null, @"INSERT INTO grant_schedule(account_id,reward_id,next_available_utc) VALUES($a,$r,$v)
                         ON CONFLICT(account_id,reward_id) DO UPDATE SET next_available_utc=$v",
                ("$a", account.Value), ("$r", rewardId), ("$v", nextUtc.ToUnixTimeMilliseconds()));
        }
        finally { gate.Release(); }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Exec(string sql, params (string, object)[] p) => Exec(null, sql, p);

    private void Exec(SqliteTransaction? tx, string sql, params (string, object)[] p)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, p);
        cmd.ExecuteNonQuery();
    }

    private long? ScalarLong(SqliteTransaction? tx, string sql, params (string, object)[] p)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, p);
        object? o = cmd.ExecuteScalar();
        return o is null or DBNull ? null : Convert.ToInt64(o, CultureInfo.InvariantCulture);
    }

    private static void Bind(SqliteCommand cmd, params (string name, object value)[] p)
    {
        foreach ((string name, object value) in p) cmd.Parameters.AddWithValue(name, value);
    }

    /// <summary>Closes the underlying connection.</summary>
    public void Dispose()
    {
        conn.Dispose();
        gate.Dispose();
    }
}
