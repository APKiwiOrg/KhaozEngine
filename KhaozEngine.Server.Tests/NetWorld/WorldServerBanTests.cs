using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldServerBanTests
{
    private static float Flat(float x, float z) => 0f;

    [Fact]
    public async Task BannedAccount_IsRejectedAtConnect()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("evil", "cheating");

        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, banStore: bans);
        var client = new NetClient(ct, TestHandshake.Wire("evil"));   // AllowAllAuthenticator: subject = token

        for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Equal(0, server.PlayerCount);
    }

    // The ban rejection is a typed ServerNoticeKind.Banned with an empty message, never the old engine-authored
    // English "banned" literal: the client maps the kind to its own localized string (the server owns no catalog).
    [Fact]
    public async Task BannedJoin_SendsTypedBannedNotice_WithNoEnglishLiteral()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("evil", "cheating");

        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default, banStore: bans);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds }, token: Encoding.UTF8.GetBytes("evil"));

        ServerNotice? received = null;
        client.NoticeReceived += n => received = n;

        for (int i = 0; i < 120; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.Equal(0, server.PlayerCount);
        Assert.True(received.HasValue, "banned client never received a notice");
        Assert.Equal(ServerNoticeKind.Banned, received!.Value.Kind);
        Assert.Equal(string.Empty, received.Value.Message);   // no "banned" literal on the wire
    }
}
