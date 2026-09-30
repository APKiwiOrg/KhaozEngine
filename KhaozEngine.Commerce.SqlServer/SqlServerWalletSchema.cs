using System.Data;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Commerce.SqlServer;

/// <summary>Creates and widens the complete wallet schema under one database-scoped application lock.</summary>
internal static class SqlServerWalletSchema
{
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

    internal static void Ensure(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using SqlTransaction transaction = conn.BeginTransaction();
        using SqlCommand cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $@"
SET XACT_ABORT ON;
DECLARE @granted int;
EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive',
    @LockOwner = N'Transaction', @LockTimeout = @timeout;
IF @granted < 0
    THROW 51000, N'The wallet schema lock was not granted.', 1;
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
  updated_at DATETIME2 NOT NULL, created_at DATETIME2 NULL, PRIMARY KEY(account_id, currency_id));
IF OBJECT_ID(N'dbo.grant_schedule', N'U') IS NULL
CREATE TABLE dbo.grant_schedule (
  account_id NVARCHAR(200) {KeyCollation} NOT NULL, reward_id NVARCHAR(200) {KeyCollation} NOT NULL,
  next_available_utc DATETIME2 NOT NULL, created_at DATETIME2 NULL, updated_at DATETIME2 NULL,
  PRIMARY KEY(account_id, reward_id));
IF COL_LENGTH(N'dbo.wallet_balance', N'created_at') IS NULL
ALTER TABLE dbo.wallet_balance ADD created_at DATETIME2 NULL;
IF COL_LENGTH(N'dbo.grant_schedule', N'created_at') IS NULL
ALTER TABLE dbo.grant_schedule ADD created_at DATETIME2 NULL;
IF COL_LENGTH(N'dbo.grant_schedule', N'updated_at') IS NULL
ALTER TABLE dbo.grant_schedule ADD updated_at DATETIME2 NULL;";
        cmd.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = "KhaozEngine.Commerce.Schema";
        cmd.Parameters.Add("@timeout", SqlDbType.Int).Value = 30_000;
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

}
