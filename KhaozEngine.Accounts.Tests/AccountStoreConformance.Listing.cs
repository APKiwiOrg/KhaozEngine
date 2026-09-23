using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>Keyset paging in ordinal order, and the banned list the ban adapter reloads from.</summary>
public abstract partial class AccountStoreConformance
{
    // Ordinal order is 0, A, B, _, a, b, c. A culture-aware sort would put _ first and interleave the cases, so a
    // store that sorts by its database's default collation goes red here.
    private static readonly string[] MixedSubjects = { "b", "B", "a", "A", "c", "_", "0" };

    private static readonly string[] MixedSubjectsInOrdinalOrder =
        MixedSubjects.Select(s => "discord:" + s).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    private async Task<IAccountStore> StoreWithMixedSubjectsAsync()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);
        foreach (string providerSubject in MixedSubjects)
            await store.FindOrCreateAsync(SignIn(providerSubject));
        return store;
    }

    [Fact]
    public virtual async Task List_RunsInOrdinalOrder()
    {
        IAccountStore store = await StoreWithMixedSubjectsAsync();

        IReadOnlyList<AccountRecord> all = await store.ListAsync();

        Assert.Equal(new[] { "discord:0", "discord:A", "discord:B", "discord:_", "discord:a", "discord:b", "discord:c" },
            all.Select(a => a.Subject));
        Assert.Equal(MixedSubjectsInOrdinalOrder, all.Select(a => a.Subject));
    }

    [Fact]
    public virtual async Task List_PagesByKeyset_UntilAnEmptyPage()
    {
        IAccountStore store = await StoreWithMixedSubjectsAsync();
        var paged = new List<string>();
        var pageSizes = new List<int>();
        string? after = null;

        while (true)
        {
            IReadOnlyList<AccountRecord> page = await store.ListAsync(after, limit: 3);
            pageSizes.Add(page.Count);
            if (page.Count == 0) break;
            paged.AddRange(page.Select(a => a.Subject));
            after = page[^1].Subject;
        }

        Assert.Equal(new[] { 3, 3, 1, 0 }, pageSizes);
        Assert.Equal(MixedSubjectsInOrdinalOrder, paged);
    }

    [Fact]
    public virtual async Task List_AfterASubjectNoAccountHas_StartsAtItsPosition()
    {
        IAccountStore store = await StoreWithMixedSubjectsAsync();

        IReadOnlyList<AccountRecord> page = await store.ListAsync("discord:AZ", limit: 2);

        Assert.Equal(new[] { "discord:B", "discord:_" }, page.Select(a => a.Subject));
    }

    [Fact]
    public virtual async Task List_ReturnsWholeRecords()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        await store.FindOrCreateAsync(SignIn("1", "Ferret"));
        await store.FindOrCreateAsync(SignIn("2", displayName: null));
        await store.BanAsync("discord:2", "griefing", Now.AddDays(1));

        IReadOnlyList<AccountRecord> all = await store.ListAsync();

        Assert.Equal(
            new[]
            {
                new AccountRecord("discord:1", "Ferret", true, null),
                new AccountRecord("discord:2", null, true, new AccountBan("griefing", Now.AddDays(1))),
            },
            all);
    }

    [Fact]
    public virtual async Task ListBanned_HoldsEveryFiledBan_LapsedIncluded_InOrdinalOrder()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        foreach (string providerSubject in new[] { "c", "a", "d", "b" })
            await store.FindOrCreateAsync(SignIn(providerSubject));
        await store.BanAsync("discord:c", "permanent", null);
        await store.BanAsync("discord:a", "timed", Now.AddHours(1));
        await store.BanAsync("discord:b", "lapsed", Now.AddHours(-1));
        await store.BanAsync("discord:d", "lifted", null);
        await store.UnbanAsync("discord:d");

        IReadOnlyList<AccountRecord> banned = await store.ListBannedAsync();

        Assert.Equal(new[] { "discord:a", "discord:b", "discord:c" }, banned.Select(a => a.Subject));
        Assert.Equal(new AccountBan("timed", Now.AddHours(1)), banned[0].Ban);
        Assert.Equal(new AccountBan("lapsed", Now.AddHours(-1)), banned[1].Ban);
        Assert.Equal(new AccountBan("permanent", null), banned[2].Ban);
        foreach (AccountRecord account in banned)
            Assert.Equal(account, await store.FindAsync(account.Subject));
    }
}
