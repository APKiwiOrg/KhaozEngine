using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Accounts.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Accounts.SqlServer;

/// <summary>
/// What the SQL Server store settles before it ever reaches a server, so these run everywhere with no instance: the
/// constructor opens nothing, a configured name is refused unless it is a plain identifier, and every argument
/// refusal, every overlong subject and every pre-cancelled call is answered without a connection attempt. The
/// connection string names a port nothing listens on, so a call that did try to connect would fail differently.
/// </summary>
public sealed class SqlServerAccountStoreConstructionTests
{
    private const string Unreachable = "Server=tcp:127.0.0.1,9;Database=nowhere;Connect Timeout=1;Encrypt=False";

    private static AccountSignIn SignIn(string provider, string providerSubject, string? displayName = "Ferret") =>
        new(provider, providerSubject, displayName, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void TheConstructor_OpensNothing_AndReportsWhatItWasBuiltWith()
    {
        var store = new SqlServerAccountStore(Unreachable, whitelistOnCreate: true,
            new SqlServerAccountStoreOptions("game", "grim_accounts", AccountSchemaMode.ValidateOnly));

        Assert.True(store.WhitelistOnCreate);
        Assert.Equal(new SqlServerAccountStoreOptions("game", "grim_accounts", AccountSchemaMode.ValidateOnly), store.Options);
        Assert.Equal(new SqlServerAccountStoreOptions("dbo", "accounts", AccountSchemaMode.AutoCreate),
            new SqlServerAccountStore(Unreachable, whitelistOnCreate: false).Options);
    }

    [Fact]
    public void ANameThatIsNotAPlainIdentifier_IsRefused_WithoutEchoingIt()
    {
        string[] refused =
        {
            "", " ", "accounts; DROP TABLE accounts", "acc]ounts", "[accounts]", "1accounts", "accounts x",
            "acc\u00F6unts", "dbo.accounts", new string('a', 129), null!,
        };

        foreach (string name in refused)
        {
            ArgumentException asTable = Assert.ThrowsAny<ArgumentException>(
                () => new SqlServerAccountStore(Unreachable, false, new SqlServerAccountStoreOptions(Table: name)));
            ArgumentException asSchema = Assert.ThrowsAny<ArgumentException>(
                () => new SqlServerAccountStore(Unreachable, false, new SqlServerAccountStoreOptions(Schema: name)));
            Assert.DoesNotContain("DROP", asTable.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP", asSchema.Message, StringComparison.Ordinal);
        }

        _ = new SqlServerAccountStore(Unreachable, false,
            new SqlServerAccountStoreOptions("_" + new string('s', 127), "_" + new string('t', 127)));
    }

    [Fact]
    public void ABlankConnectionString_OrAnUndefinedMode_IsRefused()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqlServerAccountStore(" ", false));
        Assert.ThrowsAny<ArgumentException>(() => new SqlServerAccountStore(null!, false));
        Assert.ThrowsAny<ArgumentException>(() => new SqlServerAccountStore(Unreachable, false,
            new SqlServerAccountStoreOptions(SchemaMode: (AccountSchemaMode)7)));
    }

    [Fact]
    public async Task EveryRefusal_IsAnsweredBeforeAConnectionIsAttempted()
    {
        var store = new SqlServerAccountStore(Unreachable, whitelistOnCreate: false);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.FindOrCreateAsync(SignIn("discord", "1.2")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.FindOrCreateAsync(SignIn("guest", "1")));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.FindOrCreateAsync(SignIn("discord", "1", new string('n', AccountStoreRules.MaxDisplayNameChars + 1))));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.FindAsync(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetWhitelistedAsync(null!, true));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.BanAsync("discord:1", null!, null));
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.BanAsync("discord:1", new string('r', AccountStoreRules.MaxBanReasonChars + 1), null));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.UnbanAsync(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListAsync(limit: 0));
    }

    [Fact]
    public async Task AnOverlongSubject_IsUnknown_AndACancelledCall_IsCancelled_WithoutAConnection()
    {
        var store = new SqlServerAccountStore(Unreachable, whitelistOnCreate: false);
        string overlong = "discord:" + new string('7', AccountStoreRules.MaxSubjectChars);

        Assert.Null(await store.FindAsync(overlong));
        Assert.Null(await store.SetWhitelistedAsync(overlong, true));
        Assert.Null(await store.BanAsync(overlong, "r", null));
        Assert.Null(await store.UnbanAsync(overlong));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListBannedAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FindAsync("discord:1", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.FindOrCreateAsync(SignIn("discord", "1"), cancelled.Token));
    }
}
