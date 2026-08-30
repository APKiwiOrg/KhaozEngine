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

    [Fact]
    public void PlainDisconnect_LandsAfterEverythingAlreadySent()
    {
        // A kick loses nothing: the drop takes its place in send order rather than jumping the queue. This used to
        // hold only by accident, because the disconnect went straight into the peer's queue while the sent frame sat
        // in staging, and the staging drain happened not to check whether the link was still up (#129).
        var (server, client) = Pair();
        Drain(server); Drain(client);

        server.Send(new NetConnectionId(1), new byte[] { 1 }, NetChannelReliability.ReliableOrdered);
        server.Disconnect(new NetConnectionId(1));

        System.Collections.Generic.List<NetEvent> events = Drain(client);
        Assert.Collection(events,
            e => { Assert.Equal(NetEventType.Data, e.Type); Assert.Equal(new byte[] { 1 }, e.Data); },
            e => Assert.Equal(NetEventType.Disconnected, e.Type));
    }

    [Fact]
    public void DisconnectWithReason_IsOneEventCarryingIt_SupersedingUnpolledFrames()
    {
        // The refusal shape: NetServer stages the framed reject AND rides the same frame on the disconnect, because
        // over a real socket only one of the two survives the teardown. Lossless delivery of both would be the same
        // terminal rejection twice, so the reason-carrying disconnect supersedes what has not been polled.
        var (server, client) = Pair();
        Drain(server); Drain(client);

        byte[] frame = SessionFrame.Write(SessionOpcode.Reject, System.Text.Encoding.UTF8.GetBytes("nope"));
        server.Send(new NetConnectionId(1), frame, NetChannelReliability.ReliableOrdered);
        ((INetTransport)server).Disconnect(new NetConnectionId(1), frame);   // the seam NetServer calls through

        NetEvent ev = Assert.Single(Drain(client));
        Assert.Equal(NetEventType.Disconnected, ev.Type);
        Assert.Equal(frame, ev.Data);
    }

    [Fact]
    public void DisconnectWithAnEmptyReason_IsAPlainLosslessDrop()
    {
        // An empty reason is not a refusal, matching the UDP binding's own empty-reason branch: nothing is dropped.
        var (server, client) = Pair();
        Drain(server); Drain(client);

        server.Send(new NetConnectionId(1), new byte[] { 5 }, NetChannelReliability.ReliableOrdered);
        ((INetTransport)server).Disconnect(new NetConnectionId(1), System.ReadOnlySpan<byte>.Empty);

        System.Collections.Generic.List<NetEvent> events = Drain(client);
        Assert.Collection(events,
            e => { Assert.Equal(NetEventType.Data, e.Type); Assert.Equal(new byte[] { 5 }, e.Data); },
            e => { Assert.Equal(NetEventType.Disconnected, e.Type); Assert.Empty(e.Data); });
    }
}
