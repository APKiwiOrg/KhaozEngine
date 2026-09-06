using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The per-tick server send path frames ONCE into a buffer it keeps, and a broadcast hands the same bytes to every
/// peer (#131). It used to call <c>SessionFrame.Write</c>, which allocates a fresh <c>byte[]</c>, on every
/// <c>SendTo</c> and every <c>Broadcast</c>, and the UDP binding then called <c>payload.ToArray()</c> per peer on top,
/// so a broadcast to N players cost one frame plus N copies of identical bytes, every broadcast of every tick, on the
/// one host that can least afford the garbage.
/// <para>The transport half of that is the LiteNetLib binding taking the span overload of <c>NetPeer.Send</c>, which
/// only a real socket exercises (the live-socket round trip in <c>LiteNetLibTransportTests</c> does). What is
/// measurable headlessly is the session half, which is where the per-call frame lived. Joins <c>AllocSensitive</c>
/// because it reads <see cref="GC.GetAllocatedBytesForCurrentThread"/>.</para>
/// </summary>
[Collection("AllocSensitive")]
public class NetServerSendAllocationTests
{
    private const int Players = 16;

    private static (HandshakingTransport Transport, NetServer Server) JoinedServer(int players)
    {
        var transport = new HandshakingTransport();
        var server = new NetServer(transport, maxPlayers: players, new AllowAllAuthenticator());
        transport.StageHellos(players);
        server.Poll();
        Assert.Equal(players, transport.Sends);   // one Welcome each, so every slot really is joined
        return (transport, server);
    }

    [Fact]
    public void Broadcast_frames_once_and_allocates_nothing_once_the_buffer_is_grown()
    {
        (HandshakingTransport transport, NetServer server) = JoinedServer(Players);

        var payload = new byte[256];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        // Warm up: JIT, and grow the send buffer to this payload's frame length so the measured window is steady state.
        for (int i = 0; i < 8; i++) server.Broadcast(payload, NetChannelReliability.UnreliableSequenced);

        const int measured = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measured; i++) server.Broadcast(payload, NetChannelReliability.UnreliableSequenced);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.Equal(Players + (8 + measured) * Players, transport.Sends);   // every peer really was sent to
    }

    [Fact]
    public void SendTo_allocates_nothing_once_the_buffer_is_grown()
    {
        (HandshakingTransport transport, NetServer server) = JoinedServer(Players);

        var payload = new byte[128];
        for (int i = 0; i < 8; i++) server.SendTo(0, payload, NetChannelReliability.ReliableOrdered);

        const int measured = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measured; i++) server.SendTo(0, payload, NetChannelReliability.ReliableOrdered);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.Equal(Players + 8 + measured, transport.Sends);
    }

    [Fact]
    public void Broadcast_still_delivers_the_whole_framed_payload_to_every_peer()
    {
        // The buffer is reused and only ever grows, so the thing worth pinning is that a shorter send after a longer
        // one hands out its own length and not a window with the previous tail still in it.
        (HandshakingTransport transport, NetServer server) = JoinedServer(3);
        List<byte[]> sent = transport.StartRecording();

        server.Broadcast(new byte[] { 1, 2, 3, 4, 5, 6 }, NetChannelReliability.ReliableOrdered);
        server.Broadcast(new byte[] { 9, 9 }, NetChannelReliability.ReliableOrdered);

        Assert.Equal(6, sent.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(SessionOpcode.Data, SessionFrame.ReadOpcode(sent[i]));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, SessionFrame.ReadBody(sent[i]));
        }
        for (int i = 3; i < 6; i++)
        {
            Assert.Equal(SessionOpcode.Data, SessionFrame.ReadOpcode(sent[i]));
            Assert.Equal(new byte[] { 9, 9 }, SessionFrame.ReadBody(sent[i]));
        }
    }

    [Fact]
    public void SessionFrame_writes_into_a_caller_buffer_and_refuses_a_short_one()
    {
        var destination = new byte[8];
        int written = SessionFrame.Write(SessionOpcode.Data, new byte[] { 7, 8 }, destination);

        Assert.Equal(3, written);
        Assert.Equal(SessionFrame.FrameLength(2), written);
        Assert.Equal(SessionFrame.Write(SessionOpcode.Data, new byte[] { 7, 8 }), destination.AsSpan(0, written).ToArray());
        Assert.Throws<ArgumentException>(() => SessionFrame.Write(SessionOpcode.Data, new byte[] { 7, 8 }, new byte[2]));
    }

    // A transport that answers the drain with staged Hellos from distinct connections and counts what the server
    // sends. Recording the payloads is opt-in, so the allocation tests never have the double's own copies land inside
    // the measured window.
    private sealed class HandshakingTransport : INetTransport
    {
        private readonly Queue<NetEvent> inbound = new();
        private readonly List<byte[]> recorded = new();
        private bool recording;

        public int Sends;

        public void StageHellos(int count)
        {
            byte[] hello = SessionFrame.Write(SessionOpcode.Hello, ReadOnlySpan<byte>.Empty);
            for (int i = 1; i <= count; i++)
            {
                var connection = new NetConnectionId(i);
                inbound.Enqueue(NetEvent.Connected(connection));
                inbound.Enqueue(NetEvent.FromData(connection, hello, NetChannelReliability.ReliableOrdered));
            }
        }

        /// <summary>Starts keeping a copy of every payload sent from here on, and hands back the list they land in.</summary>
        public List<byte[]> StartRecording()
        {
            recording = true;
            return recorded;
        }

        public void Poll() { }

        public bool TryDequeueEvent(out NetEvent ev)
        {
            if (inbound.Count > 0) { ev = inbound.Dequeue(); return true; }
            ev = default;
            return false;
        }

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
        {
            Sends++;
            if (recording) recorded.Add(payload.ToArray());
        }

        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }
    }
}
