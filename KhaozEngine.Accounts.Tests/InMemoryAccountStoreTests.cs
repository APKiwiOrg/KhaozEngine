using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>What the reference store adds beyond the shared contract.</summary>
public class InMemoryAccountStoreTests
{
    private static readonly AccountSignIn Ferret =
        new("discord", "1", "Ferret", new Dictionary<string, string>(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void WhitelistOnCreate_ReportsWhatTheStoreWasBuiltWith()
    {
        Assert.True(new InMemoryAccountStore(whitelistOnCreate: true).WhitelistOnCreate);
        Assert.False(new InMemoryAccountStore(whitelistOnCreate: false).WhitelistOnCreate);
    }

    [Fact]
    public async Task ACancelledToken_Throws_AndWritesNothing()
    {
        var store = new InMemoryAccountStore(whitelistOnCreate: true);
        await store.FindOrCreateAsync(Ferret);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.FindOrCreateAsync(Ferret with { ProviderSubject = "2" }, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.BanAsync("discord:1", "r", null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SetWhitelistedAsync("discord:1", false, cancelled.Token));

        AccountRecord only = Assert.Single(await store.ListAsync());
        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, null), only);
    }

    [Fact]
    public async Task AListIsASnapshot_ThatLaterWritesDoNotChange()
    {
        var store = new InMemoryAccountStore(whitelistOnCreate: false);
        await store.FindOrCreateAsync(Ferret);

        IReadOnlyList<AccountRecord> before = await store.ListAsync();
        await store.SetWhitelistedAsync("discord:1", true);
        await store.FindOrCreateAsync(Ferret with { ProviderSubject = "2" });

        Assert.False(Assert.Single(before).Whitelisted);
    }
}
