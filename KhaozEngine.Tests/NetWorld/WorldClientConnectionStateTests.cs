using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldClientConnectionStateTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Connects_through_Connecting_to_Connected()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        Assert.Equal(WorldConnectionState.Connecting, client.ConnectionState);
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.True(client.Joined);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }

    [Fact]
    public void Transport_drop_while_connected_is_Disconnected_Unreachable()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        INetTransport ct = hub.CreateClient();
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        hub.DisconnectClient(ct);
        for (int i = 0; i < 3; i++) { client.Poll(); }
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.Unreachable, client.DisconnectReason);
    }

    [Fact]
    public void Rejected_token_is_surfaced_as_RejectedToken_with_detail()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        // An authenticator that rejects every token with a known reason.
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default,
            authenticator: new RejectingAuthenticator("bad token"));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds }, token: new byte[] { 1 });

        var states = new List<WorldConnectionState>();
        client.ConnectionStateChanged += s => states.Add(s);

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.RejectedToken, client.DisconnectReason);
        Assert.Equal("bad token", client.DisconnectReasonDetail);
        Assert.Contains(WorldConnectionState.Disconnected, states);
    }

    private sealed class RejectingAuthenticator : IConnectionAuthenticator
    {
        private readonly string reason;
        public RejectingAuthenticator(string reason) => this.reason = reason;
        public bool TryAuthenticate(ReadOnlySpan<byte> token, out string subject, out string rejectReason)
        {
            subject = string.Empty;
            rejectReason = reason;
            return false;
        }
    }
}
