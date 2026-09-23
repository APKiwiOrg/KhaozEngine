using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using Joined = KhaozEngine.Tests.TileNetcode.TileWorldServerAdminTests.Joined;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The ban after the door (#1104). <see cref="TileWorldServerConfig.BanStore"/> and
/// <see cref="TileWorldServerConfig.IsBanned"/> are read at the join and once per tick over every live session, so a
/// ban that reaches an admitted player ends the session with <see cref="TileServerReason.Banned"/>, the same string
/// the door refuses with, whoever recorded it.
/// </summary>
public class TileWorldServerPostJoinBanTests
{
    static byte[] Token(string account) => Encoding.UTF8.GetBytes(account);

    // A ban that lands in the window between the door and the join: the door's check for the one account it watches
    // answers false, every later check answers true.
    sealed class BannedAfterTheDoor : IBanStore
    {
        readonly string account;
        int checks;

        public BannedAfterTheDoor(string account) => this.account = account;

        public bool IsBanned(string accountId) => accountId == account && checks++ > 0;

        public ValueTask BanAsync(string accountId, string reason, DateTimeOffset? until = null,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask UnbanAsync(string accountId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public IReadOnlyCollection<BanRecord> ListBans() => Array.Empty<BanRecord>();
    }

    [Fact]
    public async Task A_ban_recorded_in_the_store_while_connected_ends_the_session_with_the_ban_token()
    {
        var bans = new InMemoryBanStore();
        using var j = new Joined("acct-live", configure: c => c with { BanStore = bans });
        var leaving = new List<string>();
        j.Server.PlayerLeaving += (_, account, _) => leaving.Add(account);

        // Straight into the store: no admin surface, no Kick for the game to pair with it.
        await bans.BanAsync("acct-live", "cheating");
        j.Frames(8);

        Assert.Equal(0, j.Server.PlayerCount);
        Assert.Equal(new[] { TileServerReason.Banned }, j.Notices);
        Assert.Equal(HandshakeToken.BannedReason, TileServerReason.Banned);
        Assert.Equal(new[] { "acct-live" }, leaving);
        Assert.False(j.Client.IsJoined);
        Assert.Empty(j.Server.ListOnline());

        // The door still reads the same store, so the next connect never reaches a join.
        TileWorldClient again = j.Connect(Token("acct-live"));
        j.Frames(8);
        Assert.Equal(HandshakeToken.BannedReason, again.RefusedReason);
        Assert.Equal(0, j.Server.PlayerCount);
    }

    [Fact]
    public void The_predicate_ends_a_live_session_the_same_way()
    {
        bool banned = false;
        using var j = new Joined("acct-p", configure: c => c with { IsBanned = account => banned && account == "acct-p" });

        j.Frames(8);
        Assert.Equal(1, j.Server.PlayerCount);

        banned = true;
        j.Frames(8);
        Assert.Equal(0, j.Server.PlayerCount);
        Assert.Equal(new[] { TileServerReason.Banned }, j.Notices);
    }

    [Fact]
    public void A_ban_that_lands_between_the_door_and_the_join_is_refused_at_the_join()
    {
        using var j = new Joined("acct-ok", configure: c => c with { BanStore = new BannedAfterTheDoor("acct-window") });
        var joined = new List<string>();
        j.Server.PlayerJoined += (_, account) => joined.Add(account);

        TileWorldClient late = j.Connect(Token("acct-window"));
        var lateNotices = new List<string>();
        late.NoticeReceived += lateNotices.Add;
        j.Frames(16);

        // Admitted at the door, told and dropped at the join, never seated.
        Assert.Null(late.RefusedReason);
        Assert.Equal(new[] { TileServerReason.Banned }, lateNotices);
        Assert.False(late.IsJoined);
        Assert.Empty(joined);
        Assert.Equal(1, j.Server.PlayerCount);
        Assert.Empty(j.Notices);
    }

    // ServerAdmin.BanAsync stores the ban and then kicks, and the admin commands run ahead of the sweep. Without the
    // ban check in the kick, the player would leave on the kick's own reason and the sweep would find nobody.
    [Fact]
    public async Task An_admin_kick_of_a_banned_account_carries_the_ban_token_whatever_its_reason()
    {
        var bans = new InMemoryBanStore();
        using var j = new Joined("acct-k", configure: c => c with { BanStore = bans });

        await bans.BanAsync("acct-k", "x");
        j.Server.Kick(PlayerRef.Account("acct-k"), "game:bye");
        j.Frames(8);

        Assert.Equal(new[] { TileServerReason.Banned }, j.Notices);
        Assert.Equal(0, j.Server.PlayerCount);
    }

    [Fact]
    public async Task A_tokenless_seat_is_never_ban_checked()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("guest:0", "x");
        await bans.BanAsync("guest:1", "x");
        using var j = new Joined("acct-main", configure: c => c with { BanStore = bans });

        TileWorldClient guest = j.Connect(null);
        var guestNotices = new List<string>();
        guest.NoticeReceived += guestNotices.Add;
        j.Frames(24);

        Assert.True(guest.IsJoined);
        Assert.Equal(2, j.Server.PlayerCount);
        Assert.Empty(guestNotices);
    }
}
