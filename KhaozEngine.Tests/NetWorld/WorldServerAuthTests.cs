using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldServerAuthTests
{
    private static float FlatGround(float x, float z) => 0f;
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("world-server-secret");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    // Drives a loopback client (presenting connectToken) into a WorldServer gated by auth.
    private static WorldServer Join(IConnectionAuthenticator auth, byte[] connectToken, out List<(int slot, string acct)> joins)
    {
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(serverTransport, config, FlatGround, MoveTuning.Default, authenticator: auth);
        var captured = new List<(int slot, string acct)>();
        server.PlayerJoined += (slot, acct) => captured.Add((slot, acct));

        var client = new NetClient(clientTransport, connectToken);
        for (int i = 0; i < 200 && captured.Count == 0; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }
        joins = captured;
        return server;
    }

    [Fact]
    public void WorldServer_UsesVerifiedSubject_AsAccountId()
    {
        string token = SignedToken.Mint("player-42", Now.AddHours(1), Secret);
        var auth = new HmacTokenAuthenticator(Secret, () => Now);

        WorldServer server = Join(auth, Encoding.UTF8.GetBytes(token), out var joins);

        Assert.Single(joins);
        Assert.Equal("player-42", joins[0].acct);
        Assert.True(server.TryGetAccountId(joins[0].slot, out string acct));
        Assert.Equal("player-42", acct);
    }

    [Fact]
    public void WorldServer_RejectsClient_PresentingTokenSignedWithWrongSecret()
    {
        // Token minted under a different secret: the server's authenticator must reject it (no join).
        string token = SignedToken.Mint("player-42", Now.AddHours(1), Encoding.UTF8.GetBytes("attacker-secret"));
        var auth = new HmacTokenAuthenticator(Secret, () => Now);

        WorldServer server = Join(auth, Encoding.UTF8.GetBytes(token), out var joins);

        Assert.Empty(joins);
        Assert.Equal(0, server.PlayerCount);
    }
}
