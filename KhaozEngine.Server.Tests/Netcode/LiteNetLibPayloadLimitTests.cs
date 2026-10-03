using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The LiteNetLib binding answers the packet-limit query from the connected peer's own unfragmented packet size for
/// the channel's delivery method, and answers zero for a connection it does not know. Binds a real UDP socket, so it
/// is <c>LiveSocket</c> and outside the default suite.
/// </summary>
public class LiteNetLibPayloadLimitTests
{
    private readonly ITestOutputHelper output;
    public LiteNetLibPayloadLimitTests(ITestOutputHelper output) => this.output = output;

    [Trait("Category", "LiveSocket")]
    [Fact]
    public void ConnectedPeerReportsAPositiveLimitAndAnUnknownConnectionReportsZero()
    {
        using LiteNetLibServerTransport? server = LiveSocketSupport.TryBindServer(out int port);
        if (server is null) { output.WriteLine(LiveSocketSupport.NoFreePortReason); return; }
        using var client = new LiteNetLibClientTransport("127.0.0.1", port);

        NetConnectionId clientOnServer = NetConnectionId.None;
        NetConnectionId serverOnClient = NetConnectionId.None;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000 && (!clientOnServer.IsValid || !serverOnClient.IsValid))
        {
            server.Poll();
            client.Poll();
            while (server.TryDequeueEvent(out NetEvent ev))
                if (ev.Type == NetEventType.Connected) clientOnServer = ev.Connection;
            while (client.TryDequeueEvent(out NetEvent ev))
                if (ev.Type == NetEventType.Connected) serverOnClient = ev.Connection;
            if (!clientOnServer.IsValid || !serverOnClient.IsValid) Thread.Sleep(15);
        }
        Assert.True(clientOnServer.IsValid, "server never saw the client connect");
        Assert.True(serverOnClient.IsValid, "client never saw the server connect");

        var unknown = new NetConnectionId(999);
        foreach (NetChannelReliability reliability in new[]
                 { NetChannelReliability.UnreliableSequenced, NetChannelReliability.ReliableOrdered })
        {
            int serverLimit = server.MaxUnfragmentedPayloadBytes(clientOnServer, reliability);
            int clientLimit = client.MaxUnfragmentedPayloadBytes(serverOnClient, reliability);
            output.WriteLine($"{reliability}: server {serverLimit}, client {clientLimit}");
            Assert.True(serverLimit > 0);
            Assert.True(clientLimit > 0);
            Assert.Equal(0, server.MaxUnfragmentedPayloadBytes(unknown, reliability));
            Assert.Equal(0, client.MaxUnfragmentedPayloadBytes(unknown, reliability));
            Assert.Equal(0, server.MaxUnfragmentedPayloadBytes(NetConnectionId.None, reliability));
        }
    }
}
