using System;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileNetcode;

[Collection("AllocSensitive")]
public sealed class TileSnapshotReceiveTests(ITestOutputHelper output)
{
    [Fact]
    public void BorrowedSnapshotStripsPaddingAndDoesNotAllocate()
    {
        byte[] paddedFrame = TileProtocol.EncodeSnapshotFrame(73, 12, 48, [41, 83]);
        byte[] envelope = new byte[paddedFrame.Length + 3];
        paddedFrame.CopyTo(envelope, 2);
        ReadOnlyMemory<byte> frame = envelope.AsMemory(2, paddedFrame.Length);
        ReadOnlyMemory<byte> body = default;

        bool matched = true;
        void Decode() =>
            matched &= TileProtocol.TryBorrowSnapshotFrame(frame, out long id, out int seq, out long tick, out body)
                && id == 73 && seq == 12 && tick == 48;
        Decode();
        AllocationMeasurement.NoPerCallAllocation("Borrowed tile snapshot header", () =>
        {
            for (int i = 0; i < 32; i++) Decode();
        });
        Assert.True(matched);
        Assert.Equal(new byte[] { 41, 83 }, body.ToArray());

        Assert.True(TileProtocol.TryDecodeSnapshotFrame(frame.Span, out _, out _, out _, out byte[] owned));
        Array.Clear(envelope);
        Assert.Equal(new byte[] { 41, 83 }, owned);
    }

    [Fact]
    public void WarmedJoinedPollBorrowsPaddedSnapshotBody()
    {
        const int measured = 32;
        const long remoteId = 73;
        var peer = new NetConnectionId(1);
        const NetChannelReliability channel = NetChannelReliability.ReliableOrdered;
        var registry = new ReplicationRegistry();
        registry.Register<Marker>(ReplicationRegistry.FirstExtensionTypeId, static (value, writer) => writer.Write(value.Value),
            static reader => new Marker(reader.ReadInt32()));
        var world = new World();
        Entity remote = world.Spawn();
        world.Set(remote, new NetId(remoteId));
        world.Set(remote, new Marker(83));

        var (source, receiver) = LoopbackTransport.CreatePair();
        using var sourceLease = source;
        using var receiverLease = receiver;
        using var client = new TileWorldClient(receiver, new TileWorldClientConfig
        {
            TickSeconds = 0.1f,
            StepTicks = new TileStepTicks(walk: 4, run: 2)
        },
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), registry: registry);
        source.Send(peer, SessionFrame.Write(SessionOpcode.Welcome, new byte[4]), channel);
        client.Poll();
        Assert.True(client.IsJoined);

        byte[] body = SnapshotWriter.Write(world, registry);
        byte[] envelope = TileProtocol.EncodeSnapshotFrame(-1, 12, 48, body);
        // The encoder pads two-byte bodies. A complete replication snapshot is longer, so set the
        // accepted pad flag and add the pad to a valid snapshot to exercise the full receive path.
        int flagsOffset = envelope.Length - body.Length - 1;
        envelope[flagsOffset] = 0x01;
        Array.Resize(ref envelope, envelope.Length + 1);
        byte[] frame = SessionFrame.Write(SessionOpcode.Data, envelope);
        for (int i = 0; i < 16; i++)
        {
            source.Send(peer, frame, channel);
            client.Poll();
        }

        long staged = 0;
        long polled = 0;
        using (AllocationRegion.Start("Warmed joined tile snapshot poll"))
        {
            for (int i = 0; i < measured; i++)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                source.Send(peer, frame, channel);
                long afterStage = GC.GetAllocatedBytesForCurrentThread();
                client.Poll();
                staged += afterStage - start;
                polled += GC.GetAllocatedBytesForCurrentThread() - afterStage;
            }
        }

        output.WriteLine($"B/frame: transport staging {staged / (double)measured}, poll {polled / (double)measured}");
        Assert.Equal(0, client.DroppedSnapshotCount);
        Assert.True(client.View.TryGetEntity(remoteId, out Entity entity));
        Assert.True(client.World.TryGet(entity, out Marker marker));
        Assert.Equal(83, marker.Value);
        Assert.Equal(0L, polled);
    }

    private readonly record struct Marker(int Value) : IComponent;
}
