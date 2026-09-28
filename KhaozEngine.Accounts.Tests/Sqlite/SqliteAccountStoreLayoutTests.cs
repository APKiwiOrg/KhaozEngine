using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Accounts.Sqlite;

/// <summary>
/// What the SQLite store does to a table it did not create, and to the file it opens: Grimhollow's layouts with
/// rows already in them, the subject key's collation, the configured table name, the identifier rule, the times
/// either store writes, and the release on dispose. These
/// read the table raw, which the conformance suite never does, because the claims are about the table.
/// </summary>
public sealed class SqliteAccountStoreLayoutTests : IDisposable
{
    // Grimhollow's three columns after the subject, for layouts that vary only the subject key.
    private const string LegacyColumns =
        "display_name TEXT NOT NULL, whitelisted INTEGER NOT NULL, banned INTEGER NOT NULL";

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

    private const string TimesByRow = "SELECT subject, debug, created_at_utc, updated_at_utc FROM accounts ORDER BY subject;";

    private readonly SqliteScratch scratch = new();
    private readonly ManualClock clock = new(Now);

    public void Dispose() => scratch.Dispose();

    private SqliteAccountStore Open(string path, bool whitelistOnCreate = false, AccountTableOptions? table = null) =>
        scratch.Own(new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate,
            (table ?? new AccountTableOptions()) with { TimeProvider = clock }));

    private static AccountSignIn SignIn(string providerSubject, string? displayName) =>
        new("discord", providerSubject, displayName, new System.Collections.Generic.Dictionary<string, string>(), Now);

    [Fact]
    public async Task TheOriginalFourColumnTable_IsRead_WidenedWithTheBanPairAndTheTimes_AndBannable()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Original +
            "INSERT INTO accounts VALUES ('discord:1', 'Ferret', 1, 0), ('discord:2', 'Weasel', 0, 1);");

        using (var store = new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate: false,
            new AccountTableOptions { TimeProvider = clock }))
        {
            Assert.Equal(new AccountRecord("discord:1", "Ferret", true, null), await store.FindAsync("discord:1"));
            // A legacy filing with no reason reads as an empty reason, since a ban's reason is never null.
            Assert.Equal(new AccountRecord("discord:2", "Weasel", false, new AccountBan("", null)),
                await store.FindAsync("discord:2"));
            Assert.Equal(EngineColumns, SqliteScratch.Columns(path, "accounts"));

            await store.BanAsync("discord:1", "griefing", Now.AddDays(1));
        }

        // The ban is a change, so it stamps the update time. The creation time stays NULL: nothing proves it.
        using SqliteAccountStore reopened = Open(path);
        Assert.Equal(
            new AccountRecord("discord:1", "Ferret", true, new AccountBan("griefing", Now.AddDays(1))) { UpdatedAtUtc = Now },
            await reopened.FindAsync("discord:1"));
        Assert.Equal(EngineColumns, SqliteScratch.Columns(path, "accounts"));
    }

    [Fact]
    public async Task TheWidenedTable_IsAdopted_AndEveryWriteLeavesDebugAlone()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Widened +
            "INSERT INTO accounts VALUES ('discord:1', 'Ferret', 1, 0, NULL, NULL, 1), " +
            "('discord:2', 'Weasel', 1, 1, 'cheating', '2026-03-01T12:00:00.0000000+11:00', 0);");
        SqliteAccountStore store = Open(path);

        AccountRecord? weasel = await store.FindAsync("discord:2");
        DateTimeOffset until = weasel!.Ban!.Value.Until!.Value;
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 1, 0, 0, TimeSpan.Zero), until);
        Assert.Equal(TimeSpan.Zero, until.Offset);

        await store.FindOrCreateAsync(SignIn("1", "Ferret the Second"));
        await store.SetWhitelistedAsync("discord:1", false);
        await store.BanAsync("discord:1", "griefing", null);
        await store.UnbanAsync("discord:1");
        await store.BanAsync("discord:2", "again", Now);
        await store.FindOrCreateAsync(SignIn("3", displayName: null));

        Assert.Equal(
            new[] { "discord:1|1", "discord:2|0", "discord:3|0" },
            SqliteScratch.Query(path, "SELECT subject, debug FROM accounts ORDER BY subject;"));
        Assert.Equal(WidenedColumns, SqliteScratch.Columns(path, "accounts"));
    }

    [Fact]
    public async Task Grimhollow_layout_table_gains_the_two_columns_and_keeps_debug()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Widened +
            "INSERT INTO accounts VALUES ('discord:1', 'Ferret', 1, 0, NULL, NULL, 1), " +
            "('discord:2', 'Weasel', 0, 1, 'cheating', NULL, 0);");
        SqliteAccountStore store = Open(path);

        // A row older than the times has no provable creation or update time, so both read NULL rather than a guess.
        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, null), await store.FindAsync("discord:1"));
        Assert.Equal(new AccountRecord("discord:2", "Weasel", false, new AccountBan("cheating", null)),
            await store.FindAsync("discord:2"));
        Assert.Equal(WidenedColumns, SqliteScratch.Columns(path, "accounts"));
        Assert.Equal(new[] { "created_at_utc|TEXT|0|NULL", "updated_at_utc|TEXT|0|NULL" },
            SqliteScratch.Query(path, "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('accounts') " +
                "WHERE name IN ('created_at_utc', 'updated_at_utc') ORDER BY cid;"));
        Assert.Equal(new[] { "discord:1|1|NULL|NULL", "discord:2|0|NULL|NULL" }, SqliteScratch.Query(path, TimesByRow));
    }

    [Fact]
    public async Task ATableGrimhollowAlreadyTimestamped_ReadsItsTimes_AndIsWrittenInItsEncoding()
    {
        // Grimhollow's own store writes round-trip UTC text, which is what this store writes, so each reads the
        // other's rows. A time written at another offset reads back as the same instant with offset zero.
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Timestamped +
            "INSERT INTO accounts VALUES " +
            "('discord:1', 'Ferret', 1, 0, NULL, NULL, 1, '2025-12-31T09:30:00.1234567+00:00', '2025-12-31T10:00:00.7654321+00:00'), " +
            "('discord:3', 'Stoat', 0, 0, NULL, NULL, 0, '2026-01-01T09:00:00.0000000+11:00', '2026-01-01T09:00:00.0000000+11:00');");
        SqliteAccountStore store = Open(path);

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

        Assert.Equal(WidenedColumns, SqliteScratch.Columns(path, "accounts"));
        Assert.Equal(
            new[]
            {
                "discord:1|1|2025-12-31T09:30:00.1234567+00:00|2026-01-01T12:00:00.1234567+00:00",
                "discord:2|0|2026-01-01T12:00:00.1234567+00:00|2026-01-01T12:00:00.1234567+00:00",
                "discord:3|0|2026-01-01T09:00:00.0000000+11:00|2026-01-01T09:00:00.0000000+11:00",
            },
            SqliteScratch.Query(path, TimesByRow));
    }

    [Fact]
    public async Task ATimeThatIsNotADate_IsRefused_NamingTheColumn_WithoutEchoingIt()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Timestamped +
            "INSERT INTO accounts VALUES ('discord:1', 'Ferret', 1, 0, NULL, NULL, 0, 'last tuesday', NULL);");
        SqliteAccountStore store = Open(path);

        InvalidOperationException refusal =
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync("discord:1"));

        Assert.Contains("created_at_utc", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tuesday", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentOpenersOfAnOlderFile_WidenItOnce()
    {
        // The ensure reads the columns and adds the missing ones in one immediate transaction, so a second opener
        // waits for the first one's write lock, then reads the widened table and adds nothing. Two check-then-adds
        // outside one would both read the narrow table, and the slower ALTER would fail on a duplicate column.
        const int openers = 8;
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Original);
        using var start = new Barrier(openers);

        Task<SqliteAccountStore>[] opening = Enumerable.Range(0, openers).Select(_ => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait();
                return new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate: false);
            },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            await Task.WhenAll(opening);
        }
        finally
        {
            foreach (Task<SqliteAccountStore> opened in opening)
            {
                if (opened.IsCompletedSuccessfully) scratch.Own(await opened);
            }
        }

        Assert.Equal(EngineColumns, SqliteScratch.Columns(path, "accounts"));
    }

    [Fact]
    public void ANullClock_IsRefused_BeforeTheFileIsOpened()
    {
        string path = scratch.NewPath();

        Assert.Throws<ArgumentNullException>(() => new SqliteAccountStore(SqliteScratch.ConnectionString(path), false,
            new AccountTableOptions { TimeProvider = null! }));

        Assert.False(File.Exists(path));
        Assert.Same(TimeProvider.System, new AccountTableOptions().TimeProvider);
    }

    [Fact]
    public async Task OnALegacyNotNullName_ANameless_SignIn_StoresEmpty_AndReadsBackAsNull()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Widened);
        SqliteAccountStore store = Open(path);

        AccountRecord created = await store.FindOrCreateAsync(SignIn("1", displayName: null));

        Assert.Null(created.DisplayName);
        Assert.Null((await store.FindAsync("discord:1"))!.DisplayName);
        Assert.Equal(new[] { "" }, SqliteScratch.Query(path, "SELECT display_name FROM accounts;"));
    }

    [Fact]
    public async Task AFreshTable_KeepsTheNameNullable_AndStoresNoneAsNull()
    {
        string path = scratch.NewPath();
        SqliteAccountStore store = Open(path);

        await store.FindOrCreateAsync(SignIn("1", displayName: null));

        Assert.Equal(new[] { "NULL" }, SqliteScratch.Query(path, "SELECT display_name FROM accounts;"));
        Assert.Equal(
            new[] { "subject|1", "display_name|0" },
            SqliteScratch.Query(path,
                "SELECT name, \"notnull\" FROM pragma_table_info('accounts') WHERE name IN ('subject', 'display_name') ORDER BY cid;"));
    }

    [Fact]
    public void ReopeningAnAdoptedTable_RunsTheEnsureAgain_WithoutAddingAnything()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Original);

        new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate: false).Dispose();
        new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate: false).Dispose();

        Assert.Equal(EngineColumns, SqliteScratch.Columns(path, "accounts"));
    }

    [Fact]
    public async Task AConfiguredTableName_IsTheOnlyTableTheStoreTouches()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Original +
            "INSERT INTO accounts VALUES ('discord:1', 'Ferret', 1, 0);");
        SqliteAccountStore store = Open(path, table: new AccountTableOptions("grim_accounts"));

        await store.FindOrCreateAsync(SignIn("2", "Weasel"));

        Assert.Equal("grim_accounts", store.TableName);
        Assert.Null(await store.FindAsync("discord:1"));
        Assert.Equal(new[] { "discord:1" }, SqliteScratch.Query(path, "SELECT subject FROM accounts;"));
        Assert.Equal(new[] { "discord:2" }, SqliteScratch.Query(path, "SELECT subject FROM grim_accounts;"));
        Assert.Equal(new[] { "subject", "display_name", "whitelisted", "banned" }, SqliteScratch.Columns(path, "accounts"));
    }

    [Fact]
    public void ATableNameThatIsNotAPlainIdentifier_IsRefused_BeforeTheFileIsOpened()
    {
        string[] refused =
        {
            "", " ", "accounts; DROP TABLE accounts", "acc\"ounts", "[accounts]", "1accounts", "accounts x",
            "acc\u00F6unts", "acc-ounts", "main.accounts", new string('a', 129), null!,
        };

        foreach (string name in refused)
        {
            string path = scratch.NewPath();
            ArgumentException refusal = Assert.ThrowsAny<ArgumentException>(
                () => new SqliteAccountStore(SqliteScratch.ConnectionString(path), false, new AccountTableOptions(name)));
            Assert.DoesNotContain("DROP", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
        }

        string edge = scratch.NewPath();
        Open(edge, table: new AccountTableOptions("_" + new string('a', 127)));
        Assert.True(File.Exists(edge));
    }

    [Fact]
    public void ATableWithoutAnOriginalColumn_IsRefused_AndLeftAsItWas()
    {
        string path = scratch.NewDatabase(
            "CREATE TABLE accounts (subject TEXT PRIMARY KEY, display_name TEXT NOT NULL, banned INTEGER NOT NULL);");

        Assert.Throws<InvalidOperationException>(
            () => new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate: false));

        Assert.Equal(new[] { "subject", "display_name", "banned" }, SqliteScratch.Columns(path, "accounts"));
    }

    [Theory]
    [InlineData("subject TEXT COLLATE NOCASE PRIMARY KEY, " + LegacyColumns + ");")]
    [InlineData("subject TEXT, " + LegacyColumns + ", PRIMARY KEY (subject COLLATE NOCASE));")]
    [InlineData("subject TEXT PRIMARY KEY COLLATE RTRIM, " + LegacyColumns + ");")]
    [InlineData("subject TEXT COLLATE NOCASE PRIMARY KEY, " + LegacyColumns + ") WITHOUT ROWID;")]
    [InlineData("subject TEXT PRIMARY KEY, " + LegacyColumns + "); CREATE UNIQUE INDEX subject_ci ON accounts (subject COLLATE NOCASE);")]
    public void ATableWhoseSubjectKeyIsNotBinary_IsRefusedAtEnsure_NamingNoSubject_AndLeftAsItWas(string layout)
    {
        // The upsert's ON CONFLICT(subject) matches under the key's collation, so over any of these a verified sign-in
        // as discord:alice would rename Alice's account and hand back discord:Alice to be signed into a token.
        string path = scratch.NewDatabase("CREATE TABLE accounts (" + layout +
            "INSERT INTO accounts VALUES ('discord:Alice', 'Alice', 1, 0);");

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate: true));

        Assert.Contains("BINARY", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "subject", "display_name", "whitelisted", "banned" }, SqliteScratch.Columns(path, "accounts"));
        Assert.Equal(new[] { "discord:Alice|Alice" }, SqliteScratch.Query(path, "SELECT subject, display_name FROM accounts;"));
    }

    [Theory]
    [InlineData("subject TEXT NOT NULL COLLATE BINARY PRIMARY KEY, " + LegacyColumns + ");")]
    [InlineData("subject TEXT COLLATE NOCASE, " + LegacyColumns + ", PRIMARY KEY (subject COLLATE BINARY));")]
    [InlineData("subject TEXT PRIMARY KEY, " + LegacyColumns + "); CREATE INDEX subject_lookup ON accounts (subject COLLATE NOCASE);")]
    public async Task ATableWhoseSubjectKeyIsBinary_IsAdopted_AndKeepsCaseDistinctAccountsApart(string layout)
    {
        // Only a key the upsert can match on decides it. A non-unique index is never that key, and a binary key over a
        // case-insensitive column still matches exactly.
        string path = scratch.NewDatabase("CREATE TABLE accounts (" + layout +
            "INSERT INTO accounts VALUES ('discord:Alice', 'Alice', 1, 0);");
        SqliteAccountStore store = Open(path, whitelistOnCreate: true);

        AccountRecord other = await store.FindOrCreateAsync(SignIn("alice", "Mallory"));

        Assert.Equal("discord:alice", other.Subject);
        Assert.Equal(new AccountRecord("discord:Alice", "Alice", true, null), await store.FindAsync("discord:Alice"));
    }

    [Fact]
    public async Task AnUpsertThatReturnsAnotherAccount_IsRefused_AndRolledBack_NamingNeitherSubject()
    {
        string path = scratch.NewPath();
        SqliteAccountStore store = Open(path, whitelistOnCreate: true);
        // Rebuilt behind the open store: the ensure cannot see a change made after it ran, so what stands between this
        // sign-in and Alice's account is the check on the subject the upsert returned.
        SqliteScratch.Execute(path,
            "DROP TABLE accounts;" +
            "CREATE TABLE accounts (subject TEXT NOT NULL COLLATE NOCASE PRIMARY KEY, display_name TEXT NULL, " +
            "whitelisted INTEGER NOT NULL, banned INTEGER NOT NULL, ban_reason TEXT NULL, ban_until TEXT NULL, " +
            "created_at_utc TEXT NULL, updated_at_utc TEXT NULL);" +
            "INSERT INTO accounts VALUES ('discord:Alice', 'Alice', 1, 0, NULL, NULL, NULL, NULL);");

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.FindOrCreateAsync(SignIn("alice", "Mallory")));

        Assert.DoesNotContain("alice", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mallory", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "discord:Alice|Alice" }, SqliteScratch.Query(path, "SELECT subject, display_name FROM accounts;"));
    }

    [Fact]
    public async Task ABanExpiryThatIsNotADate_IsRefused_WithoutEchoingIt()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Widened +
            "INSERT INTO accounts VALUES ('discord:1', 'Ferret', 1, 1, 'r', 'next tuesday', 0);");
        SqliteAccountStore store = Open(path);

        InvalidOperationException refusal =
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync("discord:1"));

        Assert.DoesNotContain("tuesday", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisposedStore_ReleasesItsFile()
    {
        string path = scratch.NewPath();
        using (var store = new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate: true))
            await store.FindOrCreateAsync(SignIn("1", "Ferret"));

        // Windows refuses an exclusive open while any handle is live. POSIX lets the delete through, so the
        // reopen is what would catch a parked handle serving the unlinked file.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(path);
        SqliteAccountStore reopened = Open(path);
        Assert.Null(await reopened.FindAsync("discord:1"));
    }

    [Fact]
    public async Task WhitelistOnCreate_IsWhatTheStoreWasBuiltWith_AndAnOverlongSubjectIsUnknown()
    {
        SqliteAccountStore open = Open(scratch.NewPath(), whitelistOnCreate: true);
        SqliteAccountStore closed = Open(scratch.NewPath(), whitelistOnCreate: false);
        string overlong = "discord:" + new string('7', AccountStoreRules.MaxSubjectChars);

        Assert.True(open.WhitelistOnCreate);
        Assert.False(closed.WhitelistOnCreate);
        Assert.Equal("accounts", open.TableName);
        Assert.Null(await open.FindAsync(overlong));
        Assert.Null(await open.SetWhitelistedAsync(overlong, true));
    }
}
