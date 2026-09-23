using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Accounts.Sqlite;

/// <summary>
/// What the SQLite store does to a table it did not create, and to the file it opens: Grimhollow's two layouts
/// with rows already in them, the subject key's collation, the configured table name, the identifier rule, and the
/// release on dispose. These
/// read the table raw, which the conformance suite never does, because the claims are about the table.
/// </summary>
public sealed class SqliteAccountStoreLayoutTests : IDisposable
{
    // Grimhollow's three columns after the subject, for layouts that vary only the subject key.
    private const string LegacyColumns =
        "display_name TEXT NOT NULL, whitelisted INTEGER NOT NULL, banned INTEGER NOT NULL";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] EngineColumns =
        { "subject", "display_name", "whitelisted", "banned", "ban_reason", "ban_until" };

    private readonly SqliteScratch scratch = new();

    public void Dispose() => scratch.Dispose();

    private SqliteAccountStore Open(string path, bool whitelistOnCreate = false, AccountTableOptions? table = null) =>
        scratch.Own(new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate, table));

    private static AccountSignIn SignIn(string providerSubject, string? displayName) =>
        new("discord", providerSubject, displayName, new System.Collections.Generic.Dictionary<string, string>(), Now);

    [Fact]
    public async Task TheOriginalFourColumnTable_IsRead_WidenedWithTheBanPairOnly_AndBannable()
    {
        string path = scratch.NewDatabase(GrimhollowSqliteLayout.Original +
            "INSERT INTO accounts VALUES ('discord:1', 'Ferret', 1, 0), ('discord:2', 'Weasel', 0, 1);");

        using (var store = new SqliteAccountStore(SqliteScratch.ConnectionString(path), whitelistOnCreate: false))
        {
            Assert.Equal(new AccountRecord("discord:1", "Ferret", true, null), await store.FindAsync("discord:1"));
            // A legacy filing with no reason reads as an empty reason, since a ban's reason is never null.
            Assert.Equal(new AccountRecord("discord:2", "Weasel", false, new AccountBan("", null)),
                await store.FindAsync("discord:2"));
            Assert.Equal(EngineColumns, SqliteScratch.Columns(path, "accounts"));

            await store.BanAsync("discord:1", "griefing", Now.AddDays(1));
        }

        using SqliteAccountStore reopened = Open(path);
        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, new AccountBan("griefing", Now.AddDays(1))),
            await reopened.FindAsync("discord:1"));
        Assert.Equal(EngineColumns, SqliteScratch.Columns(path, "accounts"));
    }

    [Fact]
    public async Task TheWidenedTable_IsAdoptedAsItIs_AndEveryWriteLeavesDebugAlone()
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
        Assert.Equal(EngineColumns.Append("debug"), SqliteScratch.Columns(path, "accounts"));
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
            "whitelisted INTEGER NOT NULL, banned INTEGER NOT NULL, ban_reason TEXT NULL, ban_until TEXT NULL);" +
            "INSERT INTO accounts VALUES ('discord:Alice', 'Alice', 1, 0, NULL, NULL);");

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
