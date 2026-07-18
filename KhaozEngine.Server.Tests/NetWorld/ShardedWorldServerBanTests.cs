using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldServerBanTests
{
    private static float Flat(float x, float z) => 0f;

    [Fact]
    public async Task BannedAccount_IsRejectedAtConnect()
    {
        var bans = new InMemoryBanStore();
        await bans.BanAsync("evil", "cheating");

        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new ShardedWorldServer(st, config, Flat, MoveTuning.Default, banStore: bans);
        var client = new NetClient(ct, TestHandshake.Wire("evil"));

        for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Equal(0, server.PlayerCount);
    }
}
