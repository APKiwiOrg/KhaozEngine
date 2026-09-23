using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Accounts.SqlServer;

/// <summary>
/// What the SQL Server store does to a table it did not create: Grimhollow's two layouts with rows already in them,
/// a legacy case-insensitive collation, both schema modes, and the checks that refuse a table the store cannot read.
/// Gated on <c>KE_ACCOUNTS_SQLSERVER</c> like the conformance leg, and reading the table raw, because the claims are
/// about the table.
/// </summary>
public sealed class SqlServerAccountStoreLayoutTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] EngineColumns =
        { "subject", "display_name", "whitelisted", "banned", "ban_reason", "ban_until" };

    private SqlServerAccountDatabase? database;

    public void Dispose() => database?.Dispose();

    private SqlServerAccountDatabase Database => database ??= new SqlServerAccountDatabase();

    // Grimhollow's original SQL Server table: four columns, subject in the database default collation, which this
    // pins to the usual case-insensitive default so the fact does not depend on how the test database was made.
    private string OriginalTable()
    {
        string table = Database.NewTableName();
        Database.Execute(
            $"CREATE TABLE dbo.[{table}] (subject NVARCHAR(128) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL PRIMARY KEY, " +
            "display_name NVARCHAR(128) NOT NULL, whitelisted BIT NOT NULL, banned BIT NOT NULL);");
        return table;
    }

    // The same table after Grimhollow's widening, built the way its store built it.
    private string WidenedTable()
    {
        string table = OriginalTable();
        Database.Execute(
            $"ALTER TABLE dbo.[{table}] ADD ban_reason NVARCHAR(256) NULL; " +
            $"ALTER TABLE dbo.[{table}] ADD ban_until DATETIMEOFFSET NULL; " +
            $"ALTER TABLE dbo.[{table}] ADD debug BIT NOT NULL DEFAULT 0;");
        return table;
    }

    private SqlServerAccountStore Open(string table, AccountSchemaMode mode = AccountSchemaMode.AutoCreate,
        string schema = "dbo") =>
        new(Database.ConnectionString, whitelistOnCreate: false, new SqlServerAccountStoreOptions(schema, table, mode));

    private static AccountSignIn SignIn(string providerSubject, string? displayName) =>
        new("discord", providerSubject, displayName, new Dictionary<string, string>(), Now);

    [AccountsSqlServerFact]
    public async Task TheOriginalFourColumnTable_IsRead_WidenedWithTheBanPairOnly_AndBannable()
    {
        string table = OriginalTable();
        Database.Execute($"INSERT INTO dbo.[{table}] VALUES (N'discord:1', N'Ferret', 1, 0), (N'discord:2', N'Weasel', 0, 1);");
        SqlServerAccountStore store = Open(table);

        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, null), await store.FindAsync("discord:1"));
        Assert.Equal(new AccountRecord("discord:2", "Weasel", false, new AccountBan("", null)),
            await store.FindAsync("discord:2"));
        Assert.Equal(EngineColumns, Database.Columns(table));

        await store.BanAsync("discord:1", "griefing", Now.AddDays(1));

        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, new AccountBan("griefing", Now.AddDays(1))),
            await Open(table).FindAsync("discord:1"));
    }

    [AccountsSqlServerFact]
    public async Task TheWidenedTable_IsAdoptedAsItIs_AndEveryWriteLeavesDebugAlone()
    {
        string table = WidenedTable();
        Database.Execute(
            $"INSERT INTO dbo.[{table}] (subject, display_name, whitelisted, banned, ban_reason, ban_until, debug) VALUES " +
            "(N'discord:1', N'Ferret', 1, 0, NULL, NULL, 1), " +
            "(N'discord:2', N'Weasel', 1, 1, N'cheating', '2026-03-01T12:00:00+11:00', 0);");
        SqlServerAccountStore store = Open(table);

        DateTimeOffset until = (await store.FindAsync("discord:2"))!.Ban!.Value.Until!.Value;
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 1, 0, 0, TimeSpan.Zero), until);
        Assert.Equal(TimeSpan.Zero, until.Offset);

        await store.FindOrCreateAsync(SignIn("1", "Ferret the Second"));
        await store.SetWhitelistedAsync("discord:1", false);
        await store.BanAsync("discord:1", "griefing", null);
        await store.UnbanAsync("discord:1");
        await store.BanAsync("discord:2", "again", Now);
        AccountRecord nameless = await store.FindOrCreateAsync(SignIn("3", displayName: null));

        Assert.Null(nameless.DisplayName);
        Assert.Null((await store.FindAsync("discord:3"))!.DisplayName);
        Assert.Equal(new[] { "discord:1|1", "discord:2|0", "discord:3|0" },
            Database.Query($"SELECT subject, CAST(debug AS int) FROM dbo.[{table}] ORDER BY subject;"));
        Assert.Equal(new[] { "" }, Database.Query($"SELECT display_name FROM dbo.[{table}] WHERE subject = N'discord:3';"));
        Assert.Equal(EngineColumns.Append("debug"), Database.Columns(table));
    }

    [AccountsSqlServerFact]
    public async Task ACaseInsensitiveLegacyTable_StillListsInOrdinalOrder_AndMatchesExactly()
    {
        string table = WidenedTable();
        Database.Execute($"INSERT INTO dbo.[{table}] (subject, display_name, whitelisted, banned) VALUES " +
            "(N'discord:a', N'A', 0, 0), (N'discord:B', N'B', 0, 0), (N'discord:_', N'U', 0, 0);");
        SqlServerAccountStore store = Open(table);

        // The table's own collation would answer _, a, B.
        Assert.Equal(new[] { "discord:B", "discord:_", "discord:a" }, (await store.ListAsync()).Select(a => a.Subject));
        Assert.Equal(new[] { "discord:_" }, (await store.ListAsync("discord:B", limit: 1)).Select(a => a.Subject));
        Assert.Null(await store.FindAsync("discord:b"));
        Assert.Null(await store.SetWhitelistedAsync("discord:b", true));
        Assert.False((await store.FindAsync("discord:B"))!.Whitelisted);

        InvalidOperationException collision =
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindOrCreateAsync(SignIn("b", "b")));
        Assert.DoesNotContain("discord:b", collision.Message, StringComparison.Ordinal);
        Assert.Null(collision.InnerException);
    }

    [AccountsSqlServerFact]
    public async Task AFreshTable_PinsABinarySubject_AndKeepsTheNameNullable()
    {
        string table = Database.NewTableName();
        SqlServerAccountStore store = Open(table);

        await store.FindOrCreateAsync(SignIn("1", displayName: null));

        Assert.Equal(new[] { "subject|Latin1_General_100_BIN2|False", "display_name|-|True" },
            Database.Query("SELECT name, CASE WHEN name = N'subject' THEN collation_name ELSE N'-' END, is_nullable " +
                $"FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name IN (N'subject', N'display_name') " +
                "ORDER BY column_id;"));
        Assert.Equal(new[] { "NULL" }, Database.Query($"SELECT display_name FROM dbo.[{table}];"));
    }

    [AccountsSqlServerFact]
    public async Task ValidateOnly_RefusesAMissingTable_AndCreatesNothing()
    {
        string table = Database.NewTableName();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Open(table, AccountSchemaMode.ValidateOnly).FindAsync("discord:1"));

        Assert.Equal(new[] { "NULL" }, Database.Query($"SELECT OBJECT_ID(N'dbo.{table}');"));
    }

    [AccountsSqlServerFact]
    public async Task ValidateOnly_RefusesTheUnwidenedTable_NamingTheColumns_AndAltersNothing()
    {
        string table = OriginalTable();

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Open(table, AccountSchemaMode.ValidateOnly).ListAsync());

        Assert.Contains("'ban_reason' is missing", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'ban_until' is missing", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(EngineColumns.Take(4), Database.Columns(table));
    }

    [AccountsSqlServerFact]
    public async Task ValidateOnly_ServesATableAnAutoCreateStoreMade()
    {
        string table = Database.NewTableName();
        await Open(table).FindOrCreateAsync(SignIn("1", "Ferret"));

        SqlServerAccountStore validating = Open(table, AccountSchemaMode.ValidateOnly);
        await validating.BanAsync("discord:1", "griefing", null);

        Assert.Equal(new AccountRecord("discord:1", "Ferret", false, new AccountBan("griefing", null)),
            await validating.FindAsync("discord:1"));
    }

    [AccountsSqlServerFact]
    public async Task AColumnNarrowerThanTheLimits_OrAMissingSchema_IsRefusedOnTheFirstCall_BeforeAnyDdl()
    {
        string narrow = Database.NewTableName();
        Database.Execute($"CREATE TABLE dbo.[{narrow}] (subject NVARCHAR(128) NOT NULL PRIMARY KEY, " +
            "display_name NVARCHAR(64) NULL, whitelisted BIT NOT NULL, banned BIT NOT NULL);");

        InvalidOperationException refusal =
            await Assert.ThrowsAsync<InvalidOperationException>(() => Open(narrow).FindAsync("discord:1"));
        Assert.Contains("'display_name' holds fewer than 128 characters", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(EngineColumns.Take(4), Database.Columns(narrow));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Open(Database.NewTableName(), schema: "no_such_schema_" + Guid.NewGuid().ToString("N")).ListBannedAsync());
    }
}
