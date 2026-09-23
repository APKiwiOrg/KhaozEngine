using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>
/// The <see cref="IAccountStore"/> contract, as an abstract class with one concrete subclass per store: the
/// in-memory reference store here, and each durable engine backend beside it. It is the shape
/// <c>WalletStoreContract</c> and <c>ContentAuthoringStoreConformance</c> established.
/// <para>
/// <b>Every fact asserts OBSERVABLE behaviour through the seam and never a mechanism.</b> A fact that reached into a
/// table or a private field would pass on one backend and be unwritable on another, and the point of the suite is
/// that every implementation of one seam answers the same way.
/// </para>
/// <para>
/// <b>Every fact is virtual.</b> xUnit decides a skip at discovery from the attribute on the method, so a leg gated on
/// an environment variable (the SQL Server one, on <c>KE_ACCOUNTS_SQLSERVER</c>) overrides each fact with a one-line
/// body carrying its own skipping attribute. An always-on leg overrides nothing.
/// </para>
/// <para>
/// <b>The in-memory store is the REFERENCE.</b> A fact it fails is a defect in the reference or in the suite, never a
/// fact that does not apply.
/// </para>
/// </summary>
public abstract partial class AccountStoreConformance
{
    /// <summary>A fixed instant. The stores are clock-free, so expiry is asserted against instants around it.</summary>
    protected static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>No further claims. The engine stores keep none either way.</summary>
    protected static readonly IReadOnlyDictionary<string, string> NoClaims = new Dictionary<string, string>();

    /// <summary>
    /// A store over EMPTY backing data, a fresh one on every call, that whitelists the accounts it creates when
    /// <paramref name="whitelistOnCreate"/> is set. A leg owning a database gives each call its own table or file and
    /// cleans up when the test instance is disposed.
    /// </summary>
    protected abstract IAccountStore NewStore(bool whitelistOnCreate);

    /// <summary>A verified Discord sign-in, which the engine stores mint as <c>discord:{providerSubject}</c>.</summary>
    protected static AccountSignIn SignIn(string providerSubject, string? displayName = "Ferret", string provider = "discord") =>
        new(provider, providerSubject, displayName, NoClaims, Now);

    [Fact]
    public virtual async Task FirstSignIn_MintsProviderColonSubject_AndStoresTheName()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);

        AccountRecord account = await store.FindOrCreateAsync(SignIn("80351110224678912", "Ferret"));

        Assert.Equal("discord:80351110224678912", account.Subject);
        Assert.Equal("Ferret", account.DisplayName);
        Assert.Null(account.Ban);
        Assert.True(AccountStoreRules.IsAdmissibleSubject(account.Subject));
        Assert.Equal(account, await store.FindAsync("discord:80351110224678912"));
    }

    [Fact]
    public virtual async Task FirstSignIn_AppliesWhitelistOnCreate()
    {
        IAccountStore open = NewStore(whitelistOnCreate: true);
        IAccountStore closed = NewStore(whitelistOnCreate: false);

        Assert.True((await open.FindOrCreateAsync(SignIn("1"))).Whitelisted);
        Assert.False((await closed.FindOrCreateAsync(SignIn("2"))).Whitelisted);
    }

    [Fact]
    public virtual async Task WhitelistOnCreate_IsCreateOnly_SoARepeatKeepsTheStoredFlag()
    {
        IAccountStore open = NewStore(whitelistOnCreate: true);
        await open.FindOrCreateAsync(SignIn("1"));
        await open.SetWhitelistedAsync("discord:1", false);

        IAccountStore closed = NewStore(whitelistOnCreate: false);
        await closed.FindOrCreateAsync(SignIn("2"));
        await closed.SetWhitelistedAsync("discord:2", true);

        Assert.False((await open.FindOrCreateAsync(SignIn("1"))).Whitelisted);
        Assert.True((await closed.FindOrCreateAsync(SignIn("2"))).Whitelisted);
    }

    [Fact]
    public virtual async Task RepeatSignIn_RefreshesTheName_AndKeepsTheWhitelistAndTheBan()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);
        await store.FindOrCreateAsync(SignIn("1", "Ferret"));
        await store.SetWhitelistedAsync("discord:1", true);
        await store.BanAsync("discord:1", "griefing", Now.AddDays(1));

        AccountRecord again = await store.FindOrCreateAsync(SignIn("1", "Ferret the Second"));

        Assert.Equal("discord:1", again.Subject);
        Assert.Equal("Ferret the Second", again.DisplayName);
        Assert.True(again.Whitelisted);
        Assert.Equal(new AccountBan("griefing", Now.AddDays(1)), again.Ban);
        Assert.Equal(again, await store.FindAsync("discord:1"));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public virtual async Task ASignInWithNoName_StoresNone_AndARepeatWithNoName_KeepsTheStoredOne()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);

        AccountRecord first = await store.FindOrCreateAsync(SignIn("1", displayName: null));
        AccountRecord named = await store.FindOrCreateAsync(SignIn("1", "Ferret"));
        AccountRecord kept = await store.FindOrCreateAsync(SignIn("1", displayName: null));

        Assert.Null(first.DisplayName);
        Assert.Equal("Ferret", named.DisplayName);
        Assert.Equal("Ferret", kept.DisplayName);
        Assert.Equal("Ferret", (await store.FindAsync("discord:1"))!.DisplayName);
    }

    [Fact]
    public virtual async Task ConcurrentFirstSignIns_ProduceOneAccount()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);

        AccountRecord[] results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => store.FindOrCreateAsync(SignIn("1")))));

        Assert.All(results, account => Assert.Equal("discord:1", account.Subject));
        Assert.All(results, account => Assert.True(account.Whitelisted));
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public virtual async Task Find_OfAnUnknownSubject_IsNull_AndCreatesNothing()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);

        Assert.Null(await store.FindAsync("discord:1"));
        Assert.Empty(await store.ListAsync());
        Assert.Empty(await store.ListBannedAsync());
    }

    [Fact]
    public virtual async Task Subjects_CompareByCodePoint_SoCaseMakesTwoAccounts()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);

        AccountRecord lower = await store.FindOrCreateAsync(SignIn("abc", "Lower"));
        AccountRecord upper = await store.FindOrCreateAsync(SignIn("ABC", "Upper"));

        Assert.Equal("discord:abc", lower.Subject);
        Assert.Equal("discord:ABC", upper.Subject);
        Assert.Equal("Lower", (await store.FindAsync("discord:abc"))!.DisplayName);
        Assert.Equal("Upper", (await store.FindAsync("discord:ABC"))!.DisplayName);
        Assert.Null(await store.FindAsync("discord:Abc"));
        Assert.Equal(2, (await store.ListAsync()).Count);
    }

    [Fact]
    public virtual async Task ValuesAtTheLimits_RoundTripExactly()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);
        string providerSubject = new('7', AccountStoreRules.MaxSubjectChars - "discord:".Length);
        // Non-ASCII on purpose, a surrogate pair included, which counts two code units toward the limit.
        string name = "Férret \U0001F98A " + new string('n', AccountStoreRules.MaxDisplayNameChars - 10);
        string reason = "é" + new string('r', AccountStoreRules.MaxBanReasonChars - 1);
        Assert.Equal(AccountStoreRules.MaxDisplayNameChars, name.Length);

        AccountRecord created = await store.FindOrCreateAsync(SignIn(providerSubject, name));
        AccountRecord? banned = await store.BanAsync(created.Subject, reason, null);

        Assert.Equal(AccountStoreRules.MaxSubjectChars, created.Subject.Length);
        Assert.Equal(name, created.DisplayName);
        Assert.Equal(new AccountBan(reason, null), banned!.Ban);
        Assert.Equal(banned, await store.FindAsync(created.Subject));
    }
}
