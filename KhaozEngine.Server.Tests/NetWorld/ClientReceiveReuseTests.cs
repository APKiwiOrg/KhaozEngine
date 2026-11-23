using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

[Collection("AllocSensitive")]
public sealed class ClientReceiveReuseTests(ITestOutputHelper output)
{
    private static readonly NetConnectionId Peer = new(1);
    private const NetChannelReliability Channel = NetChannelReliability.ReliableOrdered;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmedSnapshotReceiveAllocatesNothingForUnsampledComponents(bool ticked)
    {
        ReplicationRegistry registry = Registry();
        (LoopbackTransport source, LoopbackTransport receiver) = LoopbackTransport.CreatePair();
        using var sourceLease = source;
        using var receiverLease = receiver;
        using var client = NewClient(source, receiver, registry);
        byte[] frame = Frame(registry, ticked, (47, 95), (11, 17));

        for (int i = 0; i < 16; i++)
        {
            source.Send(Peer, frame, Channel);
            client.Poll();
        }

        long bytes = 0;
        using (AllocationRegion.Start("Warmed snapshot receive"))
        {
            for (int i = 0; i < 32; i++)
            {
                source.Send(Peer, frame, Channel); // exclude the transport's independently owned frame
                long start = GC.GetAllocatedBytesForCurrentThread();
                client.Poll();
                bytes += GC.GetAllocatedBytesForCurrentThread() - start;
            }
        }

        Assert.Equal(0L, bytes);
        Assert.True(client.TryGetComponent(47, out Marker marker) && marker.Value == 95);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmedDeltaPollAllocatesOnlyTheAcknowledgement(bool ticked)
    {
        const int warmup = 16;
        const int measured = 32;
        ReplicationRegistry registry = Registry();
        var world = new World();
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(47));
        var writer = new ServerReplicator(registry);
        var frames = new byte[warmup + measured][];
        for (int i = 0; i < frames.Length; i++)
        {
            world.AdvanceTick();
            world.Set(entity, new Marker(i));
            writer.Capture(world);
            frames[i] = Envelope(ticked, delta: true, writer.WriteFor(0));
        }

        (LoopbackTransport source, LoopbackTransport receiver) = LoopbackTransport.CreatePair();
        using var sourceLease = source;
        using var receiverLease = receiver;
        using var client = NewClient(source, receiver, registry);
        _ = ReadAcknowledgement(source);
        bool allAcknowledged = true;
        for (int i = 0; i < warmup; i++)
        {
            source.Send(Peer, frames[i], Channel);
            client.Poll();
            allAcknowledged &= ReadAcknowledgement(source) == i + 1;
        }

        // The send-only control follows the same ACK encoder, session framing and loopback transport path.
        long acknowledgements = MeasureAcknowledgements(warmup, measured);
        long staged = 0;
        long polled = 0;
        using (AllocationRegion.Start("Warmed joined delta poll"))
        {
            for (int i = warmup; i < frames.Length; i++)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                source.Send(Peer, frames[i], Channel);
                long afterStage = GC.GetAllocatedBytesForCurrentThread();
                client.Poll();
                staged += afterStage - start;
                polled += GC.GetAllocatedBytesForCurrentThread() - afterStage;
                allAcknowledged &= ReadAcknowledgement(source) == i + 1;
            }
        }

        output.WriteLine($"B/frame: transport staging {staged / (double)measured}, ACK control {acknowledgements / (double)measured}, poll {polled / (double)measured}");
        Assert.True(allAcknowledged);
        Assert.True(client.TryGetComponent(47, out Marker marker) && marker.Value == frames.Length - 1);
        Assert.Equal(acknowledgements, polled);
    }

    private static long MeasureAcknowledgements(int warmup, int measured)
    {
        var (sender, receiver) = LoopbackTransport.CreatePair();
        using var senderLease = sender;
        using var receiverLease = receiver;
        var session = new NetClient(sender);
        session.Poll();
        _ = ReadAcknowledgement(receiver);
        for (int i = 0; i < warmup; i++)
        {
            session.Send(MoveProtocol.EncodeReplicationAck(i + 1), Channel);
            _ = ReadAcknowledgement(receiver);
        }

        long bytes = 0;
        bool valid = true;
        using (AllocationRegion.Start("Delta acknowledgement send control"))
        {
            for (int i = warmup; i < warmup + measured; i++)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                session.Send(MoveProtocol.EncodeReplicationAck(i + 1), Channel);
                bytes += GC.GetAllocatedBytesForCurrentThread() - start;
                valid &= ReadAcknowledgement(receiver) == i + 1;
            }
        }

        Assert.True(valid);
        return bytes;
    }

    private static int ReadAcknowledgement(LoopbackTransport transport)
    {
        int sequence = -1;
        transport.Poll();
        while (transport.TryDequeueEvent(out NetEvent ev))
            if (ev.Type == NetEventType.Data && SessionFrame.ReadOpcode(ev.Data) == SessionOpcode.Data
                && MoveProtocol.TryDecodeReplicationAck(ev.Data.AsSpan(1), out int ack))
                sequence = ack;
        return sequence;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorldDispatchDoesNotReadBytesBehindTheCurrentFrame(bool ticked)
    {
        byte[] body = [2, 3, 5, 7];
        var kind = ticked ? MoveProtocol.ServerFrameKind.TickedDelta : MoveProtocol.ServerFrameKind.Delta;
        byte[] payload = ticked
            ? MoveProtocol.EncodeTickedSnapshotFrame(23, 71, 17, body)
            : MoveProtocol.EncodeSnapshotFrame(71, 17, body);
        byte[] prior = MoveProtocol.EncodeServerFrame(kind, payload);

        ReadOnlyMemory<byte> logicalFrame = prior.AsMemory(0, prior.Length - 1);
        Assert.True(MoveProtocol.TryBorrowServerFrame(logicalFrame, out var received, out var served));
        Assert.Equal(kind, received);
        Assert.True(MoveProtocol.TryBorrowServedFrame(received, served, out long serverTick, out long netId, out int ack,
            out ReadOnlyMemory<byte> slice));
        Assert.Equal(ticked ? 23L : -1L, serverTick);
        Assert.Equal(71L, netId);
        Assert.Equal(17, ack);
        Assert.Equal(new byte[] { 2, 3, 5 }, slice.ToArray());

        Assert.True(MoveProtocol.TryDecodeServerFrame(logicalFrame.Span, out var owningKind, out byte[] owned));
        Assert.Equal(received, owningKind);
        Assert.Equal(served.ToArray(), owned);
        long owningTick = -1;
        long owningNetId;
        int owningAck;
        byte[] owningBody;
        bool decoded = ticked
            ? MoveProtocol.TryDecodeTickedSnapshotFrame(owned, out owningTick, out owningNetId, out owningAck, out owningBody)
            : MoveProtocol.TryDecodeSnapshotFrame(owned, out owningNetId, out owningAck, out owningBody);
        Assert.True(decoded);
        Assert.Equal((serverTick, netId, ack), (owningTick, owningNetId, owningAck));
        Assert.Equal(slice.ToArray(), owningBody);
        Array.Clear(prior);
        Assert.Equal(payload[..^1], owned);
        Assert.Equal(new byte[] { 2, 3, 5 }, owningBody);
    }

    [Fact]
    public void ShorterNestedGameFrameKeepsOuterBorrowAndDeliveryOrder()
    {
        ReplicationRegistry registry = Registry();
        (LoopbackTransport source, LoopbackTransport receiver) = LoopbackTransport.CreatePair();
        using var sourceLease = source;
        using var receiverLease = receiver;
        using var client = NewClient(source, receiver, registry);
        var received = new List<(ushort, byte[])>();
        client.GameMessageReceived += (kind, payload) =>
        {
            if (kind == 7) client.Poll();
            received.Add((kind, payload.ToArray()));
        };
        source.Send(Peer, GameFrame(7, [2, 3, 5, 7, 11]), Channel);
        source.Send(Peer, GameFrame(8, [13]), Channel);
        client.Poll();

        Assert.Collection(received,
            nested => { Assert.Equal((ushort)8, nested.Item1); Assert.Equal(new byte[] { 13 }, nested.Item2); },
            outer => { Assert.Equal((ushort)7, outer.Item1); Assert.Equal(new byte[] { 2, 3, 5, 7, 11 }, outer.Item2); });
    }

    [Fact]
    public void TransportThatReusesAnEventBufferStillDeliversBothBodies()
    {
        byte[][] packets = [SessionFrame.Write(SessionOpcode.Welcome, new byte[4]),
            GameFrame(7, [2, 3, 5]), GameFrame(8, [11, 13, 17])];
        using var transport = new RecyclingTransport(packets);
        using var client = new WorldClient(transport, static (_, _) => 0f, MoveTuning.Default);
        var received = new List<(ushort, byte[])>();
        client.GameMessageReceived += (kind, payload) => received.Add((kind, payload.ToArray()));

        client.Poll();

        Assert.True(client.Joined);
        Assert.Collection(received,
            first => { Assert.Equal((ushort)7, first.Item1); Assert.Equal(new byte[] { 2, 3, 5 }, first.Item2); },
            second => { Assert.Equal((ushort)8, second.Item1); Assert.Equal(new byte[] { 11, 13, 17 }, second.Item2); });
    }

    private sealed class RecyclingTransport(byte[][] packets) : INetTransport
    {
        private readonly byte[] data = new byte[packets[1].Length];
        private int next;

        public void Poll() { }
        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) { }
        public void Disconnect(NetConnectionId connection) { }
        public void Dispose() { }

        public bool TryDequeueEvent(out NetEvent ev)
        {
            if (next == packets.Length)
            {
                ev = default;
                return false;
            }

            packets[next++].CopyTo(data, 0);
            ev = NetEvent.FromData(Peer, data, Channel);
            return true;
        }
    }

    private static WorldClient NewClient(LoopbackTransport source, INetTransport receiver, ReplicationRegistry registry)
    {
        var client = new WorldClient(receiver, static (_, _) => 0f, MoveTuning.Default,
            new WorldClientConfig { InterpolateRemotes = false, ReceiveServerTick = true }, registry: registry);
        source.Send(Peer, SessionFrame.Write(SessionOpcode.Welcome, new byte[4]), Channel);
        client.Poll();
        Assert.True(client.Joined);
        return client;
    }

    private static ReplicationRegistry Registry()
    {
        var registry = new ReplicationRegistry();
        registry.Register<Marker>(ReplicationRegistry.FirstExtensionTypeId,
            static (m, w) => w.Write(m.Value), static r => new Marker(r.ReadInt32()));
        return registry;
    }

    private static byte[] Frame(ReplicationRegistry registry, bool ticked, params (long Id, int Value)[] entities)
    {
        var world = new World();
        foreach ((long id, int value) in entities)
        {
            Entity entity = world.Spawn();
            world.Set(entity, new NetId(id));
            world.Set(entity, new Marker(value));
        }
        return Envelope(ticked, delta: false, SnapshotWriter.Write(world, registry));
    }

    private static byte[] Envelope(bool ticked, bool delta, byte[] body)
    {
        var kind = (ticked, delta) switch
        {
            (true, true) => MoveProtocol.ServerFrameKind.TickedDelta,
            (false, true) => MoveProtocol.ServerFrameKind.Delta,
            (true, false) => MoveProtocol.ServerFrameKind.TickedSnapshot,
            _ => MoveProtocol.ServerFrameKind.Snapshot
        };
        byte[] served = ticked ? MoveProtocol.EncodeTickedSnapshotFrame(23, -1, -1, body)
            : MoveProtocol.EncodeSnapshotFrame(-1, -1, body);
        return SessionFrame.Write(SessionOpcode.Data, MoveProtocol.EncodeServerFrame(kind, served));
    }

    private static byte[] GameFrame(ushort kind, byte[] payload) =>
        SessionFrame.Write(SessionOpcode.Data, MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.GameMessage,
            MoveProtocol.EncodeGameMessageBody(kind, payload)));

    private readonly record struct Marker(int Value) : IComponent;
}
