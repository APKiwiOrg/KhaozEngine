using System;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// <see cref="NetClient.Send"/> aims at the connection id the transport actually reported for the server, not at a
/// literal 1 (#132). Both shipped transports happen to number the server peer 1, so the literal and the real id
/// coincided by construction and nothing exercised the difference. The failure it hid is asymmetric and silent:
/// receive rides the peer object straight off the listener callback and keeps working, while Send looks up an id no
/// transport knows and no-ops, so a client goes on looking connected and never sends anything again.
/// </summary>
public class NetClientServerConnectionTests
{
    [Fact]
    public void Send_targets_the_connection_the_transport_reported()
    {
        var transport = new RecordingTransport();
        var server = new NetConnectionId(7);   // deliberately not 1
        transport.Deliver(NetEvent.Connected(server));

        var client = new NetClient(transport);
        client.Poll();                          // Hello, on the reported connection
        client.Send(new byte[] { 4, 2 }, NetChannelReliability.ReliableOrdered);

        Assert.Equal(2, transport.Sent.Count);
        Assert.All(transport.Sent, s => Assert.Equal(server, s.Target));
        Assert.Equal(SessionOpcode.Hello, SessionFrame.ReadOpcode(transport.Sent[0].Payload));
        Assert.Equal(SessionOpcode.Data, SessionFrame.ReadOpcode(transport.Sent[1].Payload));
        Assert.Equal(new byte[] { 4, 2 }, SessionFrame.ReadBody(transport.Sent[1].Payload));
    }

    [Fact]
    public void Send_before_the_transport_reports_a_connection_is_a_noop()
    {
        var transport = new RecordingTransport();
        var client = new NetClient(transport);

        client.Send(new byte[] { 1 }, NetChannelReliability.ReliableOrdered);

        Assert.Empty(transport.Sent);   // there is no server connection to name yet
    }

    [Fact]
    public void Send_after_the_session_ends_is_a_noop()
    {
        var transport = new RecordingTransport();
        var server = new NetConnectionId(7);
        transport.Deliver(NetEvent.Connected(server));
        transport.Deliver(NetEvent.Disconnected(server));

        var client = new NetClient(transport);
        client.Poll();
        transport.Sent.Clear();   // drop the Hello, only what happens after the drop matters here

        client.Send(new byte[] { 1 }, NetChannelReliability.ReliableOrdered);

        Assert.Empty(transport.Sent);
    }
}
