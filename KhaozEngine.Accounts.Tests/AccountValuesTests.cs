using System;
using KhaozEngine.Accounts;
using Xunit;

namespace KhaozEngine.Tests.Accounts;

/// <summary>The expiry rule every gate reads through: in force strictly before the expiry, lapsed at and after it.</summary>
public class AccountValuesTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void APermanentBan_IsAlwaysActive()
    {
        var ban = new AccountBan("cheating", null);

        Assert.True(ban.IsActive(DateTimeOffset.MinValue));
        Assert.True(ban.IsActive(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void ATimedBan_IsActiveStrictlyBeforeItsExpiry()
    {
        var ban = new AccountBan("cooldown", Now);

        Assert.True(ban.IsActive(Now.AddTicks(-1)));
        Assert.False(ban.IsActive(Now));
        Assert.False(ban.IsActive(Now.AddTicks(1)));
    }

    [Fact]
    public void ATimedBan_ComparesInstants_NotOffsets()
    {
        var ban = new AccountBan("cooldown", new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.FromHours(11)));

        // 23:00 at +11:00 is 12:00 UTC.
        Assert.True(ban.IsActive(Now.AddSeconds(-1)));
        Assert.False(ban.IsActive(Now));
    }

    [Fact]
    public void IsBanActive_IsFalseWithNoBanFiled_AndFollowsTheFiledBanOtherwise()
    {
        var clean = new AccountRecord("discord:1", "Ferret", true, null);
        var lapsing = clean with { Ban = new AccountBan("cooldown", Now) };

        Assert.False(clean.IsBanActive(Now));
        Assert.True(lapsing.IsBanActive(Now.AddSeconds(-1)));
        Assert.False(lapsing.IsBanActive(Now));
    }
}
