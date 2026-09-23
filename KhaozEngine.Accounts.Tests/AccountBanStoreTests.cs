using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>
/// The one <see cref="IBanStore"/> over the account store: expiry through the clock, the store written before the
/// cache, the unknown subject refused, and the reload that picks up what was written out of band.
/// </summary>
public class AccountBanStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAccountStore accounts = new(whitelistOnCreate: true);
    private readonly ManualClock clock = new(Now);

    private async Task SignInAsync(params string[] providerSubjects)
    {
        foreach (string providerSubject in providerSubjects)
        {
            await accounts.FindOrCreateAsync(
                new AccountSignIn("discord", providerSubject, "Ferret", new Dictionary<string, string>(), Now));
        }
    }

    [Fact]
    public void TheConstructor_RefusesANullStoreOrClock()
    {
        Assert.Throws<ArgumentNullException>(() => new AccountBanStore(null!, clock));
        Assert.Throws<ArgumentNullException>(() => new AccountBanStore(accounts, null!));
    }

    [Fact]
    public async Task IsBanned_HonoursTheExpiry_ThroughTheClock()
    {
        await SignInAsync("1");
        var bans = new AccountBanStore(accounts, clock);

        await bans.BanAsync("discord:1", "cooldown", Now.AddMinutes(5));
        Assert.True(bans.IsBanned("discord:1"));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(bans.IsBanned("discord:1"));
        Assert.Empty(bans.ListBans());
        // The lapse is the clock's doing: the filing stays in the store for the operator.
        Assert.Equal(new AccountBan("cooldown", Now.AddMinutes(5)), (await accounts.FindAsync("discord:1"))!.Ban);
    }

    [Fact]
    public async Task IsBanned_OfAnEmptyUnknownOrUnbannedSubject_IsFalse()
    {
        await SignInAsync("1");
        var bans = new AccountBanStore(accounts, clock);

        Assert.False(bans.IsBanned(string.Empty));
        Assert.False(bans.IsBanned(null!));
        Assert.False(bans.IsBanned("discord:2"));
        Assert.False(bans.IsBanned("discord:1"));
    }

    [Fact]
    public async Task Ban_And_Unban_WriteTheAccountRow_SoEveryReaderAgrees()
    {
        await SignInAsync("1");
        var bans = new AccountBanStore(accounts, clock);

        await bans.BanAsync("discord:1", "griefing", null);
        Assert.Equal(new AccountBan("griefing", null), (await accounts.FindAsync("discord:1"))!.Ban);
        Assert.True(bans.IsBanned("discord:1"));

        await bans.UnbanAsync("discord:1");
        Assert.Null((await accounts.FindAsync("discord:1"))!.Ban);
        Assert.False(bans.IsBanned("discord:1"));
    }

    [Fact]
    public async Task Ban_ANullReason_FilesAnEmptyOne()
    {
        await SignInAsync("1");
        var bans = new AccountBanStore(accounts, clock);

        await bans.BanAsync("discord:1", null!);

        Assert.Equal(new AccountBan(string.Empty, null), (await accounts.FindAsync("discord:1"))!.Ban);
        Assert.Equal(new BanRecord("discord:1", string.Empty, null), Assert.Single(bans.ListBans()));
    }

    [Fact]
    public async Task AStoreThatThrowsOnBan_LeavesTheCacheUnchanged()
    {
        await SignInAsync("1");
        var store = new ScriptedAccountStore(accounts) { FailWrites = true };
        var bans = new AccountBanStore(store, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bans.BanAsync("discord:1", "griefing").AsTask());

        Assert.False(bans.IsBanned("discord:1"));
        Assert.Empty(bans.ListBans());
    }

    [Fact]
    public async Task AStoreThatThrowsOnUnban_LeavesTheBanInForce()
    {
        await SignInAsync("1");
        var store = new ScriptedAccountStore(accounts);
        var bans = new AccountBanStore(store, clock);
        await bans.BanAsync("discord:1", "griefing");
        store.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => bans.UnbanAsync("discord:1").AsTask());

        Assert.True(bans.IsBanned("discord:1"));
    }

    [Fact]
    public async Task AnUnknownSubject_ThrowsArgumentException_NotEchoingIt_AndCachesNothing()
    {
        await SignInAsync("1");
        var bans = new AccountBanStore(accounts, clock);

        ArgumentException onBan = await Assert.ThrowsAsync<ArgumentException>(
            () => bans.BanAsync("discord:4815162342", "typo").AsTask());
        ArgumentException onUnban = await Assert.ThrowsAsync<ArgumentException>(
            () => bans.UnbanAsync("discord:4815162342").AsTask());

        foreach (ArgumentException refusal in new[] { onBan, onUnban })
        {
            Assert.Equal("accountId", refusal.ParamName);
            Assert.DoesNotContain("4815162342", refusal.Message, StringComparison.Ordinal);
        }
        Assert.False(bans.IsBanned("discord:4815162342"));
        Assert.Null(await accounts.FindAsync("discord:4815162342"));
    }

    [Fact]
    public async Task AnEmptySubject_IsRefused()
    {
        var bans = new AccountBanStore(accounts, clock);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => bans.BanAsync(string.Empty, "r").AsTask());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => bans.UnbanAsync(null!).AsTask());
    }

    [Fact]
    public async Task AnOverlongReason_IsTheStoresRefusal_AndCachesNothing()
    {
        await SignInAsync("1");
        var bans = new AccountBanStore(accounts, clock);

        await Assert.ThrowsAsync<ArgumentException>(
            () => bans.BanAsync("discord:1", new string('r', AccountStoreRules.MaxBanReasonChars + 1)).AsTask());

        Assert.False(bans.IsBanned("discord:1"));
    }

    [Fact]
    public async Task LoadAsync_PicksUpBansFiledOutOfBand_LapsedFilingsInert()
    {
        await SignInAsync("1", "2", "3");
        await accounts.BanAsync("discord:1", "permanent", null);
        await accounts.BanAsync("discord:2", "lapsed", Now.AddHours(-1));
        var bans = new AccountBanStore(accounts, clock);
        Assert.False(bans.IsBanned("discord:1"));

        await bans.LoadAsync();

        Assert.True(bans.IsBanned("discord:1"));
        Assert.False(bans.IsBanned("discord:2"));
        Assert.False(bans.IsBanned("discord:3"));
    }

    [Fact]
    public async Task LoadAsync_DropsABanLiftedOutOfBand()
    {
        await SignInAsync("1");
        var bans = new AccountBanStore(accounts, clock);
        await bans.BanAsync("discord:1", "griefing");
        await accounts.UnbanAsync("discord:1");
        Assert.True(bans.IsBanned("discord:1"));

        await bans.LoadAsync();

        Assert.False(bans.IsBanned("discord:1"));
    }

    [Fact]
    public async Task AFailedReload_KeepsThePreviousMap()
    {
        await SignInAsync("1");
        var store = new ScriptedAccountStore(accounts);
        var bans = new AccountBanStore(store, clock);
        await bans.BanAsync("discord:1", "griefing");
        store.FailListBanned = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => bans.LoadAsync());

        Assert.True(bans.IsBanned("discord:1"));
    }

    [Fact]
    public async Task ABanWrittenWhileAReloadIsReading_IsNotLostToTheReload()
    {
        await SignInAsync("1");
        var store = new ScriptedAccountStore(accounts);
        var bans = new AccountBanStore(store, clock);
        store.StallNextList();

        Task reload = bans.LoadAsync();
        await store.ListEntered;                           // the reload holds a snapshot with no ban in it
        Task ban = bans.BanAsync("discord:1", "griefing").AsTask();
        Assert.Null((await accounts.FindAsync("discord:1"))!.Ban);   // the ban waits for the reload
        store.ReleaseList();
        await Task.WhenAll(reload, ban);

        Assert.True(bans.IsBanned("discord:1"));
    }

    [Fact]
    public async Task ListBans_IsTheBansInForce_InOrdinalOrder()
    {
        await SignInAsync("b", "A", "a", "c");
        var bans = new AccountBanStore(accounts, clock);
        await bans.BanAsync("discord:b", "timed", Now.AddHours(1));
        await bans.BanAsync("discord:A", "permanent");
        await bans.BanAsync("discord:a", "lapsed", Now.AddHours(-1));

        IReadOnlyCollection<BanRecord> live = bans.ListBans();

        Assert.Equal(
            new[]
            {
                new BanRecord("discord:A", "permanent", null),
                new BanRecord("discord:b", "timed", Now.AddHours(1)),
            },
            live.ToArray());
    }
}
