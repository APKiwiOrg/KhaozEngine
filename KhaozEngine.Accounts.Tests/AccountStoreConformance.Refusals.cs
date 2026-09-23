using System;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>
/// What every engine store refuses, before anything is written: the subject rule, the length limits and the
/// argument checks. The refusal is an <see cref="ArgumentException"/> (or a subclass) on every backend.
/// <para>
/// Facts rather than theories, with the cases looped inside, so a gated leg overrides each with one attribute and
/// never restates a data row.
/// </para>
/// </summary>
public abstract partial class AccountStoreConformance
{
    [Fact]
    public virtual async Task SignIn_RefusesAProviderSubjectWithADot_AndCreatesNothing()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);

        foreach ((string provider, string providerSubject) in new[] { ("discord", "12.34"), ("oidc", "user@example.com") })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => store.FindOrCreateAsync(SignIn(providerSubject, provider: provider)));
        }

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public virtual async Task SignIn_RefusesTheReservedGuestPrefix_AndCreatesNothing()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.FindOrCreateAsync(SignIn("1", provider: "guest")));

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public virtual async Task SignIn_RefusesAMalformedProviderIdOrSubject_AndCreatesNothing()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        (string Provider, string ProviderSubject)[] malformed =
        {
            ("dis:cord", "1"), ("disc.ord", "1"), ("", "1"), (" ", "1"), (null!, "1"),
            ("discord", ""), ("discord", " "), ("discord", null!),
        };

        foreach ((string provider, string providerSubject) in malformed)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => store.FindOrCreateAsync(SignIn(providerSubject, provider: provider)));
        }

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public virtual async Task SignIn_RefusesAnOverlongSubjectOrName_AndCreatesNothing()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        string overlongSubject = new('7', AccountStoreRules.MaxSubjectChars - "discord:".Length + 1);
        string overlongName = new('n', AccountStoreRules.MaxDisplayNameChars + 1);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.FindOrCreateAsync(SignIn(overlongSubject)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.FindOrCreateAsync(SignIn("1", overlongName)));

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public virtual async Task ARepeatSignIn_WithAnOverlongName_ChangesNothing()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        AccountRecord created = await store.FindOrCreateAsync(SignIn("1", "Ferret"));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.FindOrCreateAsync(SignIn("1", new string('n', AccountStoreRules.MaxDisplayNameChars + 1))));

        Assert.Equal(created, await store.FindAsync("discord:1"));
    }

    [Fact]
    public virtual async Task Ban_RefusesAnOverlongOrNullReason_AndChangesNothing()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        AccountRecord created = await store.FindOrCreateAsync(SignIn("1"));

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => store.BanAsync("discord:1", new string('r', AccountStoreRules.MaxBanReasonChars + 1), null));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.BanAsync("discord:1", null!, null));

        Assert.Equal(created, await store.FindAsync("discord:1"));
        Assert.Empty(await store.ListBannedAsync());
    }

    [Fact]
    public virtual async Task ReadsAndWrites_RefuseANullOrEmptySubject()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);

        foreach (string? subject in new[] { null, string.Empty })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.FindAsync(subject!));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetWhitelistedAsync(subject!, true));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.BanAsync(subject!, "r", null));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.UnbanAsync(subject!));
        }
    }

    [Fact]
    public virtual async Task List_RefusesANonPositiveLimit()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListAsync(limit: 0));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListAsync(limit: -1));
    }
}
