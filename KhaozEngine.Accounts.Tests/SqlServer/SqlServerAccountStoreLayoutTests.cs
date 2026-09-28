using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Accounts.SqlServer;

/// <summary>
/// What the SQL Server store does to a table it did not create: Grimhollow's layouts with rows already in them, the
/// times either store writes, a legacy case-insensitive collation, both schema modes, and the checks that refuse a
/// table the store cannot read.
/// Gated on <c>KE_ACCOUNTS_SQLSERVER</c> like the conformance leg, and reading the table raw, because the claims are
/// about the table.
/// </summary>
public sealed class SqlServerAccountStoreLayoutTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] EngineColumns =
    {
        "subject", "display_name", "whitelisted", "banned", "ban_reason", "ban_until", "created_at_utc", "updated_at_utc",
    };

    // Grimhollow's widened table, which added debug before the times existed, so the times follow it.
    private static readonly string[] WidenedColumns =
    {
        "subject", "display_name", "whitelisted", "banned", "ban_reason", "ban_until", "debug", "created_at_utc",
        "updated_at_utc",
    };

    private readonly ManualClock clock = new(Now);
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
        new(Database.ConnectionString, whitelistOnCreate: false,
            new SqlServerAccountStoreOptions(schema, table, mode) { TimeProvider = clock });

    private static AccountSignIn SignIn(string providerSubject, string? displayName) =>
        new("discord", providerSubject, displayName, new Dictionary<string, string>(), Now);

    [AccountsSqlServerFact]
    public async Task TheOriginalFourColumnTable_IsRead_WidenedWithTheBanPairAndTheTimes_AndBannable()
    {
        string table = OriginalTable();
        Database.Execute($"INSERT INTO dbo.[{table}] VALUES (N'discord:1', N'Ferret', 1, 0), (N'discord:2', N'Weasel', 0, 1);");
        SqlServerAccountStore store = Open(table);

        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, null), await store.FindAsync("discord:1"));
        Assert.Equal(new AccountRecord("discord:2", "Weasel", false, new AccountBan("", null)),
            await store.FindAsync("discord:2"));
        Assert.Equal(EngineColumns, Database.Columns(table));

        await store.BanAsync("discord:1", "griefing", Now.AddDays(1));

        // The ban is a change, so it stamps the update time. The creation time stays NULL: nothing proves it.
        Assert.Equal(
            new AccountRecord("discord:1", "Ferret", true, new AccountBan("griefing", Now.AddDays(1))) { UpdatedAtUtc = Now },
            await Open(table).FindAsync("discord:1"));
    }

    [AccountsSqlServerFact]
    public async Task TheWidenedTable_IsAdopted_AndEveryWriteLeavesDebugAlone()
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
        Assert.Equal(WidenedColumns, Database.Columns(table));
    }

    [AccountsSqlServerFact]
    public async Task Grimhollow_layout_table_gains_the_two_columns_and_keeps_debug()
    {
        string table = WidenedTable();
        Database.Execute(
            $"INSERT INTO dbo.[{table}] (subject, display_name, whitelisted, banned, ban_reason, ban_until, debug) VALUES " +
            "(N'discord:1', N'Ferret', 1, 0, NULL, NULL, 1), (N'discord:2', N'Weasel', 0, 1, N'cheating', NULL, 0);");
        SqlServerAccountStore store = Open(table);

        // A row older than the times has no provable creation or update time, so both read NULL rather than a guess.
        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, null), await store.FindAsync("discord:1"));
        Assert.Equal(new AccountRecord("discord:2", "Weasel", false, new AccountBan("cheating", null)),
            await store.FindAsync("discord:2"));
        Assert.Equal(WidenedColumns, Database.Columns(table));
        Assert.Equal(new[] { "created_at_utc|datetimeoffset|7|True", "updated_at_utc|datetimeoffset|7|True" },
            Database.Query("SELECT name, TYPE_NAME(system_type_id), scale, is_nullable FROM sys.columns " +
                $"WHERE object_id = OBJECT_ID(N'dbo.{table}') AND name IN (N'created_at_utc', N'updated_at_utc') " +
                "ORDER BY column_id;"));
        Assert.Equal(new[] { "discord:1|1|NULL|NULL", "discord:2|0|NULL|NULL" },
            Database.Query("SELECT subject, CAST(debug AS int), created_at_utc, updated_at_utc " +
                $"FROM dbo.[{table}] ORDER BY subject;"));
    }

    [AccountsSqlServerFact]
    public async Task ATableGrimhollowAlreadyTimestamped_ReadsItsTimes_AndIsWrittenAtOffsetZero()
    {
        // The columns as Grimhollow's own store adds them, so each store reads the other's rows. A time written at
        // another offset reads back as the same instant with offset zero.
        string table = WidenedTable();
        Database.Execute(
            $"ALTER TABLE dbo.[{table}] ADD created_at_utc DATETIMEOFFSET(7) NULL; " +
            $"ALTER TABLE dbo.[{table}] ADD updated_at_utc DATETIMEOFFSET(7) NULL;");
        Database.Execute(
            $"INSERT INTO dbo.[{table}] (subject, display_name, whitelisted, banned, debug, created_at_utc, updated_at_utc) " +
            "VALUES (N'discord:1', N'Ferret', 1, 0, 1, '2025-12-31T09:30:00.1234567+00:00', '2025-12-31T10:00:00.7654321+00:00'), " +
            "(N'discord:3', N'Stoat', 0, 0, 0, '2026-01-01T09:00:00.0000000+11:00', '2026-01-01T09:00:00.0000000+11:00');");
        SqlServerAccountStore store = Open(table);

        AccountRecord? ferret = await store.FindAsync("discord:1");
        AccountRecord? stoat = await store.FindAsync("discord:3");
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 9, 30, 0, TimeSpan.Zero).AddTicks(1_234_567), ferret!.CreatedAtUtc);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 10, 0, 0, TimeSpan.Zero).AddTicks(7_654_321), ferret.UpdatedAtUtc);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero), stoat!.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, stoat.CreatedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, stoat.UpdatedAtUtc!.Value.Offset);

        clock.Advance(TimeSpan.FromTicks(1_234_567));
        await store.BanAsync("discord:1", "griefing", null);
        await store.FindOrCreateAsync(SignIn("2", "Weasel"));

        // What this store writes is offset zero and exact to the tick: 1,234,567 ticks after noon is 123,456,700 ns.
        Assert.Equal(WidenedColumns, Database.Columns(table));
        Assert.Equal(new[] { "discord:1|0|0|123456700", "discord:2|0|0|123456700" },
            Database.Query("SELECT subject, DATEPART(TZOFFSET, created_at_utc), DATEPART(TZOFFSET, updated_at_utc), " +
                "DATEDIFF_BIG(NANOSECOND, CAST('2026-01-01T12:00:00+00:00' AS datetimeoffset(7)), updated_at_utc) " +
                $"FROM dbo.[{table}] WHERE subject IN (N'discord:1', N'discord:2') ORDER BY subject;"));
        Assert.Equal(new[] { "1" }, Database.Query($"SELECT CAST(debug AS int) FROM dbo.[{table}] WHERE subject = N'discord:1';"));
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
        Assert.Contains("'created_at_utc' is missing", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'updated_at_utc' is missing", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(EngineColumns.Take(4), Database.Columns(table));
    }

    [AccountsSqlServerFact]
    public async Task ValidateOnly_ServesATableAnAutoCreateStoreMade()
    {
        string table = Database.NewTableName();
        await Open(table).FindOrCreateAsync(SignIn("1", "Ferret"));

        SqlServerAccountStore validating = Open(table, AccountSchemaMode.ValidateOnly);
        await validating.BanAsync("discord:1", "griefing", null);

        Assert.Equal(
            new AccountRecord("discord:1", "Ferret", false, new AccountBan("griefing", null))
            {
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now,
            },
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

    [AccountsSqlServerFact]
    public async Task ATimeColumnOfAnotherType_IsRefusedOnTheFirstCall_BeforeAnyDdl()
    {
        string table = Database.NewTableName();
        Database.Execute($"CREATE TABLE dbo.[{table}] (subject NVARCHAR(128) NOT NULL PRIMARY KEY, " +
            "display_name NVARCHAR(128) NULL, whitelisted BIT NOT NULL, banned BIT NOT NULL, created_at_utc DATETIME2 NULL);");

        InvalidOperationException refusal =
            await Assert.ThrowsAsync<InvalidOperationException>(() => Open(table).FindAsync("discord:1"));

        Assert.Contains("'created_at_utc' is datetime2 where datetimeoffset is expected", refusal.Message,
            StringComparison.Ordinal);
        Assert.Equal(EngineColumns.Take(4).Append("created_at_utc"), Database.Columns(table));
    }
}
