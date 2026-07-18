using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class InMemoryBanStoreTests
{
    [Fact]
    public async Task Ban_ThenIsBanned_ThenUnban()
    {
        var bans = new InMemoryBanStore();
        Assert.False(bans.IsBanned("evil"));
        await bans.BanAsync("evil", "cheating");
        Assert.True(bans.IsBanned("evil"));
        Assert.Equal("cheating", bans.ListBans().Single().Reason);
        await bans.UnbanAsync("evil");
        Assert.False(bans.IsBanned("evil"));
        Assert.Empty(bans.ListBans());
    }

    [Fact]
    public async Task ExpiredBan_IsNotBanned()
    {
        var now = new DateTimeOffset(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);
        var clock = now;
        var bans = new InMemoryBanStore(() => clock);
        await bans.BanAsync("temp", "timeout", now.AddMinutes(10));
        Assert.True(bans.IsBanned("temp"));
        clock = now.AddMinutes(11);
        Assert.False(bans.IsBanned("temp"));
        Assert.Empty(bans.ListBans());
    }
}
