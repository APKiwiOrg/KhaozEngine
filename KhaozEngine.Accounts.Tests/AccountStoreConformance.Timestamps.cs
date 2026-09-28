using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>
/// The account's creation and update times, stamped from the store's clock. The creation time is written once. The
/// update time equals it on a new account and moves only when a write changes a stored value, so a repeat sign-in or
/// an operator write that leaves every value as it was moves neither.
/// </summary>
public abstract partial class AccountStoreConformance
{
    // An odd step, so a backend that dropped or rounded sub-second digits of a stored time goes red.
    private static readonly TimeSpan ClockStep = TimeSpan.FromTicks(12_345_678);

    private DateTimeOffset AdvanceClock()
    {
        clock.Advance(ClockStep);
        return clock.GetUtcNow();
    }

    private static void AssertTimes(AccountRecord? account, DateTimeOffset created, DateTimeOffset updated, string after)
    {
        Assert.NotNull(account);
        Assert.True(account.CreatedAtUtc == created && account.UpdatedAtUtc == updated,
            $"After {after}: created {account.CreatedAtUtc:o} and updated {account.UpdatedAtUtc:o}, where " +
            $"{created:o} and {updated:o} were expected.");
        Assert.Equal(TimeSpan.Zero, account.CreatedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, account.UpdatedAtUtc!.Value.Offset);
    }

    [Fact]
    public virtual async Task Created_account_reports_its_creation_time()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);
        DateTimeOffset t0 = AdvanceClock();

        AccountRecord created = await store.FindOrCreateAsync(SignIn("1", "Ferret"));
        DateTimeOffset t1 = AdvanceClock();
        await store.FindOrCreateAsync(SignIn("2", displayName: null));

        AssertTimes(created, t0, t0, "the first sign-in");
        AssertTimes(await store.FindAsync("discord:1"), t0, t0, "a find");
        AssertTimes(await store.FindAsync("discord:2"), t1, t1, "a later account's find");
        IReadOnlyList<AccountRecord> listed = await store.ListAsync();
        Assert.Equal(2, listed.Count);
        AssertTimes(listed[0], t0, t0, "a listing");
        AssertTimes(listed[1], t1, t1, "a listing");
    }

    [Fact]
    public virtual async Task Changing_an_account_moves_only_its_update_time()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);
        DateTimeOffset t0 = AdvanceClock();
        await store.FindOrCreateAsync(SignIn("1", "Ferret"));

        DateTimeOffset t1 = AdvanceClock();
        AssertTimes(await store.BanAsync("discord:1", "griefing", null), t0, t1, "a ban");
        DateTimeOffset t2 = AdvanceClock();
        AssertTimes(await store.FindOrCreateAsync(SignIn("1", "Ferret the Second")), t0, t2, "a rename");

        AssertTimes(await store.FindAsync("discord:1"), t0, t2, "a find");
        AssertTimes(Assert.Single(await store.ListBannedAsync()), t0, t2, "the banned list");
    }

    [Fact]
    public virtual async Task Every_write_that_changes_a_stored_value_moves_the_update_time()
    {
        IAccountStore store = NewStore(whitelistOnCreate: false);
        DateTimeOffset t0 = AdvanceClock();
        await store.FindOrCreateAsync(SignIn("1", "Ferret"));

        // Each write changes as few stored values as the seam allows, so every comparison a backend makes to decide
        // whether a value changed is exercised on its own. A case or a trailing space is a change of a stored text.
        (string Change, Func<Task<AccountRecord?>> Write)[] changes =
        {
            ("whitelisting", () => store.SetWhitelistedAsync("discord:1", true)),
            ("a ban", () => store.BanAsync("discord:1", "griefing", null)),
            ("a reason changing case", () => store.BanAsync("discord:1", "Griefing", null)),
            ("a reason gaining a trailing space", () => store.BanAsync("discord:1", "Griefing ", null)),
            ("an expiry on a permanent ban", () => store.BanAsync("discord:1", "Griefing ", Now.AddDays(1))),
            ("a different expiry", () => store.BanAsync("discord:1", "Griefing ", Now.AddDays(2))),
            ("a timed ban made permanent", () => store.BanAsync("discord:1", "Griefing ", null)),
            ("an unban", () => store.UnbanAsync("discord:1")),
            ("unwhitelisting", () => store.SetWhitelistedAsync("discord:1", false)),
            ("a rename changing case", async () => await store.FindOrCreateAsync(SignIn("1", "ferret"))),
            ("a rename gaining a trailing space", async () => await store.FindOrCreateAsync(SignIn("1", "ferret "))),
        };

        foreach ((string change, Func<Task<AccountRecord?>> write) in changes)
        {
            DateTimeOffset at = AdvanceClock();
            AssertTimes(await write(), t0, at, change);
            AssertTimes(await store.FindAsync("discord:1"), t0, at, change + " and a find");
        }
    }

    [Fact]
    public virtual async Task A_write_that_changes_nothing_moves_neither_time()
    {
        IAccountStore store = NewStore(whitelistOnCreate: true);
        DateTimeOffset t0 = AdvanceClock();
        await store.FindOrCreateAsync(SignIn("1", "Ferret"));
        DateTimeOffset t1 = AdvanceClock();
        await store.BanAsync("discord:1", "griefing", Now.AddDays(1));

        (string Write, Func<Task<AccountRecord?>> Run)[] noChanges =
        {
            ("a repeat sign-in", async () => await store.FindOrCreateAsync(SignIn("1", "Ferret"))),
            ("a sign-in with no name", async () => await store.FindOrCreateAsync(SignIn("1", displayName: null))),
            ("whitelisting a whitelisted account", () => store.SetWhitelistedAsync("discord:1", true)),
            ("the same ban again", () => store.BanAsync("discord:1", "griefing", Now.AddDays(1))),
            ("the same ban with its expiry at another offset",
                () => store.BanAsync("discord:1", "griefing", Now.AddDays(1).ToOffset(TimeSpan.FromHours(11)))),
        };
        foreach ((string write, Func<Task<AccountRecord?>> run) in noChanges)
        {
            AdvanceClock();
            AssertTimes(await run(), t0, t1, write);
            AssertTimes(await store.FindAsync("discord:1"), t0, t1, write + " and a find");
        }

        DateTimeOffset t2 = AdvanceClock();
        await store.UnbanAsync("discord:1");
        AdvanceClock();
        AssertTimes(await store.UnbanAsync("discord:1"), t0, t2, "an unban with no ban filed");
        AssertTimes(await store.FindAsync("discord:1"), t0, t2, "an unban with no ban filed and a find");
    }
}
