using System;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.Sqlite;
using KhaozEngine.Tests.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

public sealed class SqliteWalletStoreBootstrapTests
{
    private static readonly AccountId Account = new("acct:bootstrap");
    private static readonly CurrencyId Currency = new("shard");

    [Fact]
    public async Task Current_schema_opens_while_another_connection_holds_a_write_transaction()
    {
        using var file = new SqliteScratchFile("ke-wallet-bootstrap-");
        using (var initial = new SqliteWalletStore(file.ConnectionString))
            await initial.CreditAsync(Account, Currency, 7, "seed", LedgerReason.Grant, null);

        using var writer = new SqliteConnection(file.ConnectionString + ";Pooling=False");
        writer.Open();
        using SqliteTransaction transaction = writer.BeginTransaction(deferred: false);

        using var reopened = new SqliteWalletStore(file.ConnectionString + ";Default Timeout=1");
        Assert.Equal(7, await reopened.GetBalanceAsync(Account, Currency));
    }

    [Fact]
    public async Task Current_schema_opens_on_a_read_only_connection()
    {
        using var file = new SqliteScratchFile("ke-wallet-read-bootstrap-");
        using (var initial = new SqliteWalletStore(file.ConnectionString))
            await initial.CreditAsync(Account, Currency, 9, "seed", LedgerReason.Grant, null);

        using var reopened = new SqliteWalletStore(file.ConnectionString + ";Mode=ReadOnly");
        Assert.Equal(9, await reopened.GetBalanceAsync(Account, Currency));
    }

    [Fact]
    public async Task Existing_balance_table_does_not_hide_missing_ledger_and_schedule_tables()
    {
        using var file = new SqliteScratchFile("ke-wallet-partial-bootstrap-");
        file.Execute("""
            CREATE TABLE wallet_balance (
              account_id TEXT NOT NULL, currency_id TEXT NOT NULL, amount INTEGER NOT NULL,
              updated_at INTEGER NOT NULL, created_at INTEGER NULL, PRIMARY KEY(account_id, currency_id));
            INSERT INTO wallet_balance VALUES ('acct:bootstrap', 'shard', 5, 1700000000000, NULL);
            """);

        using var store = new SqliteWalletStore(file.ConnectionString);
        Assert.True((await store.CreditAsync(Account, Currency, 2, "first", LedgerReason.Grant, null)).Applied);
        Assert.Equal(7, await store.GetBalanceAsync(Account, Currency));
        Assert.Single(await store.GetLedgerAsync(Account, Currency, 10));
        DateTimeOffset next = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        await store.SetNextAvailableAsync(Account, "daily", next);
        Assert.Equal(next, await store.GetNextAvailableAsync(Account, "daily"));
        Assert.Null(file.ReadPair("SELECT created_at, updated_at FROM wallet_balance;").First);
    }

    [Theory]
    [InlineData("ux_ledger_idem", 1)]
    [InlineData("ix_ledger_acct", 0)]
    public void Missing_index_is_repaired_even_when_all_tables_and_columns_exist(string indexName, long unique)
    {
        using var file = new SqliteScratchFile("ke-wallet-index-bootstrap-");
        using (var initial = new SqliteWalletStore(file.ConnectionString)) { }
        file.Execute($"DROP INDEX {indexName};");

        using var reopened = new SqliteWalletStore(file.ConnectionString);
        using var connection = new SqliteConnection(file.ConnectionString + ";Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT \"unique\" FROM pragma_index_list('wallet_ledger') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", indexName);
        Assert.Equal(unique, command.ExecuteScalar());
    }
}
