using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>The operator writes: the whitelist flag, filing a ban, lifting it, and what none of them may do.</summary>
public abstract partial class AccountStoreConformance
{
    [Fact]
    public virtual async Task Writes_OnAnUnknownSubject_ReturnNull_AndCreateNothing()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        // One real account, so "nothing created" is told apart from an empty store.
        await store.FindOrCreateAsync(SignIn("1"));

        Assert.Null(await store.SetWhitelistedAsync("discord:2", true));
        Assert.Null(await store.BanAsync("discord:2", "typo", null));
        Assert.Null(await store.UnbanAsync("discord:2"));

        Assert.Null(await store.FindAsync("discord:2"));
        Assert.Equal(new[] { "discord:1" }, (await store.ListAsync()).Select(a => a.Subject));
        Assert.Empty(await store.ListBannedAsync());
    }

    [Fact]
    public virtual async Task SetWhitelisted_ReturnsTheAccountAsItStandsAfterwards_AndTouchesNothingElse()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);
        await store.FindOrCreateAsync(SignIn("1", "Ferret"));
        await store.BanAsync("discord:1", "griefing", null);

        AccountRecord? on = await store.SetWhitelistedAsync("discord:1", true);
        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, new AccountBan("griefing", null)), on);
        Assert.Equal(on, await store.FindAsync("discord:1"));

        AccountRecord? off = await store.SetWhitelistedAsync("discord:1", false);
        Assert.Equal(on! with { Whitelisted = false }, off);
        Assert.Equal(off, await store.FindAsync("discord:1"));
    }

    [Fact]
    public virtual async Task Ban_FilesTheReasonAndExpiry_AndReturnsTheAccount()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        await store.FindOrCreateAsync(SignIn("1", "Ferret"));

        AccountRecord? banned = await store.BanAsync("discord:1", "griefing", Now.AddHours(1));

        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, new AccountBan("griefing", Now.AddHours(1))), banned);
        Assert.True(banned!.IsBanActive(Now));
        Assert.False(banned.IsBanActive(Now.AddHours(1)));
        Assert.Equal(banned, await store.FindAsync("discord:1"));
    }

    [Fact]
    public virtual async Task APermanentBan_HasNoExpiry()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        await store.FindOrCreateAsync(SignIn("1"));

        AccountRecord? banned = await store.BanAsync("discord:1", "cheating", null);

        Assert.Equal(new AccountBan("cheating", null), banned!.Ban);
        Assert.True(banned.IsBanActive(DateTimeOffset.MaxValue));
        Assert.Equal(banned, await store.FindAsync("discord:1"));
    }

    [Fact]
    public virtual async Task ALapsedTimedBan_StaysFiled_ThroughASignIn_AndInTheBannedList()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        await store.FindOrCreateAsync(SignIn("1"));
        await store.BanAsync("discord:1", "cooldown", Now.AddMinutes(5));
        DateTimeOffset later = Now.AddMinutes(10);

        AccountRecord signedIn = await store.FindOrCreateAsync(SignIn("1"));

        Assert.Equal(new AccountBan("cooldown", Now.AddMinutes(5)), signedIn.Ban);
        Assert.False(signedIn.IsBanActive(later));
        AccountRecord listed = Assert.Single(await store.ListBannedAsync());
        Assert.Equal(signedIn, listed);
    }

    [Fact]
    public virtual async Task Unban_ClearsTheReasonAndTheExpiry()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        await store.FindOrCreateAsync(SignIn("1", "Ferret"));
        await store.BanAsync("discord:1", "griefing", Now.AddDays(3));

        AccountRecord? lifted = await store.UnbanAsync("discord:1");

        Assert.Equal(new AccountRecord("discord:1", "Ferret", true, null), lifted);
        Assert.Equal(lifted, await store.FindAsync("discord:1"));
        Assert.Empty(await store.ListBannedAsync());

        // Nothing of the old filing survives into the next one.
        AccountRecord? again = await store.BanAsync("discord:1", "again", null);
        Assert.Equal(new AccountBan("again", null), again!.Ban);
    }

    [Fact]
    public virtual async Task Unban_OfAnAccountWithNoBan_ReturnsItUnchanged()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);
        AccountRecord created = await store.FindOrCreateAsync(SignIn("1"));

        Assert.Equal(created, await store.UnbanAsync("discord:1"));
        Assert.Equal(created, await store.FindAsync("discord:1"));
    }

    [Fact]
    public virtual async Task ABanOnABannedAccount_ReplacesTheReasonAndTheExpiry()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        await store.FindOrCreateAsync(SignIn("1"));
        await store.BanAsync("discord:1", "first", null);

        AccountRecord? replaced = await store.BanAsync("discord:1", "second", Now.AddDays(7));

        Assert.Equal(new AccountBan("second", Now.AddDays(7)), replaced!.Ban);
        Assert.Equal(replaced, await store.FindAsync("discord:1"));
        Assert.Single(await store.ListBannedAsync());
    }

    [Fact]
    public virtual async Task ABanExpiry_ReadsBackAsTheSameInstant_InUtc()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        await store.FindOrCreateAsync(SignIn("1"));
        var sydneyNoon = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(11));

        AccountRecord? banned = await store.BanAsync("discord:1", "r", sydneyNoon);
        AccountRecord? read = await store.FindAsync("discord:1");

        foreach (AccountRecord? account in new[] { banned, read })
        {
            DateTimeOffset until = account!.Ban!.Value.Until!.Value;
            Assert.Equal(sydneyNoon.UtcTicks, until.UtcTicks);
            Assert.Equal(TimeSpan.Zero, until.Offset);
        }
    }
}
