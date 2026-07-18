using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class LoopbackTransportTests
{
    [Fact]
    public void NetConnectionId_None_IsInvalid_AndPositiveIsValid()
    {
        Assert.False(NetConnectionId.None.IsValid);
        Assert.True(new NetConnectionId(1).IsValid);
        Assert.Equal(new NetConnectionId(1), new NetConnectionId(1)); // value equality
    }

    [Fact]
    public void NetEvent_FromData_CarriesPayloadAndReliability()
    {
        var ev = NetEvent.FromData(new NetConnectionId(1), new byte[] { 7, 8 }, NetChannelReliability.ReliableOrdered);
        Assert.Equal(NetEventType.Data, ev.Type);
        Assert.Equal(new NetConnectionId(1), ev.Connection);
        Assert.Equal(new byte[] { 7, 8 }, ev.Data);
        Assert.Equal(NetChannelReliability.ReliableOrdered, ev.Reliability);
    }

    private static (LoopbackTransport server, LoopbackTransport client) Pair() => LoopbackTransport.CreatePair();

    private static System.Collections.Generic.List<NetEvent> Drain(LoopbackTransport t)
    {
        var list = new System.Collections.Generic.List<NetEvent>();
        t.Poll();
        while (t.TryDequeueEvent(out NetEvent ev)) list.Add(ev);
        return list;
    }

    [Fact]
    public void FirstPoll_YieldsConnected_OnBothEnds()
    {
        var (server, client) = Pair();
        Assert.Contains(Drain(server), e => e.Type == NetEventType.Connected && e.Connection == new NetConnectionId(1));
        Assert.Contains(Drain(client), e => e.Type == NetEventType.Connected && e.Connection == new NetConnectionId(1));
    }

    [Fact]
    public void Send_IsDeliveredToPeer_AfterPeerPolls_WithReliabilityPreserved()
    {
        var (server, client) = Pair();
        Drain(server); Drain(client); // clear the connect events

        server.Send(new NetConnectionId(1), new byte[] { 1, 2, 3 }, NetChannelReliability.UnreliableSequenced);

        var clientEvents = Drain(client);
        var data = Assert.Single(clientEvents, e => e.Type == NetEventType.Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.Data);
        Assert.Equal(NetChannelReliability.UnreliableSequenced, data.Reliability);
    }

    [Fact]
    public void Send_IsNotVisible_BeforePeerPolls()
    {
        var (server, client) = Pair();
        Drain(server); Drain(client);
        server.Send(new NetConnectionId(1), new byte[] { 9 }, NetChannelReliability.ReliableOrdered);
        Assert.False(client.TryDequeueEvent(out _)); // nothing surfaces without a Poll
    }

    [Fact]
    public void Disconnect_YieldsDisconnected_OnPeer()
    {
        var (server, client) = Pair();
        Drain(server); Drain(client);
        server.Disconnect(new NetConnectionId(1));
        Assert.Contains(Drain(client), e => e.Type == NetEventType.Disconnected);
    }
}
