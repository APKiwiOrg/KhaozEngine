using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class LegacyDeltaServeReentrancyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AOneShotPostTickSubscriberCanServeTheNextTick(bool sharded, bool legacy)
    {
        using var server = new LegacyDeltaServeFixture(sharded, 1, legacy: legacy, allTicked: true);
        var frames = new List<byte[]>();
        server.Transport.ObserveAll = (_, frame, _) => frames.Add(frame.ToArray());
        server.AfterTick(server.Tick);

        server.Tick();

        Assert.Equal(2L, server.ServerTick);
        Assert.Collection(frames, frame => AssertFrame(frame, 1), frame => AssertFrame(frame, 2));

        void AssertFrame(byte[] frame, int tick)
        {
            Assert.Equal(legacy ? (byte)8 : (byte)7, frame[1]);
            Assert.Equal((long)tick, BitConverter.ToInt64(frame.AsSpan(2, 8)));
            if (legacy) Assert.Equal(tick, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(26)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwningWriteDuringSendKeepsBothFramesIntact(bool sharded)
    {
        using var server = new LegacyDeltaServeFixture(sharded, 1);
        var seat = server.Clients[0];
        long owner = server.OwnerOf(seat);
        server.SetPlayerSecret(seat, 123);
        World? served = null;
        var interest = new HashSet<long> { owner };
        server.Observe = (_, world, _, _) => served = world;
        server.Transport.Observe = (_, frame, _) =>
        {
            byte[] original = frame.ToArray();
            byte[] nested = server.Writer.WriteFor(7, Assert.IsType<World>(served), interest, owner);

            Assert.Equal(original[14..], nested);
            Assert.True(frame.SequenceEqual(original));
        };

        server.Tick();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedServeBuildTakesIndependentStorageDuringTheBorrow(bool sharded)
    {
        using var server = new LegacyDeltaServeFixture(sharded, 1);
        long owner = server.OwnerOf(server.Clients[0]);
        server.SetPlayerSecret(server.Clients[0], 123);
        World? served = null;
        var interest = new HashSet<long> { owner };
        server.Observe = (_, world, _, _) => served = world;
        server.Transport.Observe = (_, frame, _) =>
        {
            byte[] original = frame.ToArray();
            var nestedPass = new LegacyDeltaFrameSender(server.ServerTick);
            using (AoiDeltaReplicator.ServeFrame nested = nestedPass.Build(
                server.Writer, 7, Assert.IsType<World>(served), interest, owner))
            {
                Assert.False(frame.Overlaps(nested.Bytes));
                Assert.Equal(original[14..], nested.Bytes[22..].ToArray());
            }
            nestedPass.Complete(server.Writer);
            Assert.True(frame.SequenceEqual(original));
        };

        server.Tick();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiringTheSlotDuringTheBorrowLeavesTheLentFrameIntact(bool reset)
    {
        using var server = new LegacyDeltaServeFixture(useShards: true, 1);
        server.Tick();
        server.Tick(); // Warm both projections before retiring the slot.
        server.Observe = (slot, world, set, owner) =>
        {
            server.Transport.Observe = (_, frame, _) =>
            {
                byte[] original = frame.ToArray();
                if (reset)
                {
                    server.Writer.SeedLegacySequenceForTest(int.MaxValue);
                    server.Writer.ResetAfterLegacySequenceExhaustion();
                    server.Writer.BeginTick();
                }
                else server.Writer.Forget(slot);

                byte[] fresh = server.Writer.WriteFor(slot, world, set, owner);
                Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(fresh));
                Assert.Equal(reset ? 1 : 3, BinaryPrimitives.ReadInt32LittleEndian(fresh.AsSpan(4)));
                Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(fresh.AsSpan(12)));
                Assert.True(frame.SequenceEqual(original));
            };
        };

        server.Tick();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResetDuringHostCaptureRefusesTheServeAndLeavesAFreshFirstBody(bool sharded, bool openNextTick)
    {
        Action? duringCapture = null;
        using var server = new LegacyDeltaServeFixture(sharded, 1, onPayloadCapture: () => duringCapture?.Invoke());
        (World world, Entity actor) = server.Spawn(new LegacyDeltaServeFixture.Payload(45));
        long actorId = world.Get<NetId>(actor).Value;
        var seat = server.Clients[0];
        int slot = seat.Net.Slot;
        server.Tick();
        server.Writer.SeedLegacySequenceForTest(int.MaxValue - 1);
        duringCapture = () =>
        {
            duringCapture = null;
            server.Disconnect(seat);
            server.Writer.Forget(slot);
            server.Writer.ResetAfterLegacySequenceExhaustion();
            if (openNextTick) server.Writer.BeginTick();
        };
        bool sent = false;
        server.Transport.Observe = (_, _, _) => sent = true;

        Assert.Throws<InvalidOperationException>(server.Tick);

        Assert.False(sent);
        Assert.Equal(0, server.Writer.LegacySlotCount);
        Assert.Equal(0, server.Writer.LegacyHistoryCount);
        if (!openNextTick) server.Writer.BeginTick();
        byte[] restarted = server.Writer.WriteFor(slot, world, new HashSet<long> { actorId });
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(restarted));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(restarted.AsSpan(4)));

        var receiver = new World();
        var clientView = new ClientReplicationView(server.Registry);
        clientView.ApplyDelta(receiver, restarted);
        Assert.True(clientView.TryGetEntity(actorId, out Entity received));
        Assert.Equal(new LegacyDeltaServeFixture.Payload(45), receiver.Get<LegacyDeltaServeFixture.Payload>(received));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinarySessionSendDuringTheBorrowKeepsBothFramesIntact(bool sharded)
    {
        using var server = new LegacyDeltaServeFixture(sharded, 1);
        var seat = server.Clients[0];
        server.Transport.Observe = (_, frame, _) =>
        {
            byte[] original = frame.ToArray();
            server.SendOrdinaryMessage(seat.Net.Slot);
            Assert.True(frame.SequenceEqual(original));
        };

        server.Tick();
        seat.Net.Poll();
        Assert.True(seat.Net.TryDequeueEvent(out ClientSessionEvent ordinary));
        Assert.True(MoveProtocol.TryDecodeServerFrame(ordinary.Data, out var kind, out byte[] payload));
        Assert.Equal(MoveProtocol.ServerFrameKind.GameMessage, kind);
        Assert.Equal(MoveProtocol.EncodeGameMessageBody(1, new byte[] { 53, 71 }), payload);
        Assert.True(seat.Net.TryDequeueEvent(out ClientSessionEvent legacy));
        Assert.Equal((byte)MoveProtocol.ServerFrameKind.Delta, legacy.Data[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeparateAuthoritiesCanServeDuringTheBorrowAndTicksCanChangeThreads(bool sharded)
    {
        using var first = new LegacyDeltaServeFixture(sharded, 1);
        using var second = new LegacyDeltaServeFixture(sharded, 2);
        Exception? independent = null;
        first.Transport.Observe = (_, frame, _) =>
        {
            first.Transport.Observe = null;
            byte[] original = frame.ToArray();
            var thread = new Thread(() => independent = Record.Exception(second.Tick));
            thread.Start();
            thread.Join();
            Assert.True(frame.SequenceEqual(original));
        };

        first.Tick();

        Assert.Null(independent);
        Exception? migrated = null;
        var continuation = new Thread(() => migrated = Record.Exception(first.Tick));
        continuation.Start();
        continuation.Join();
        Assert.Null(migrated);
        Assert.Equal(2L, first.ServerTick);
        Assert.Equal(1L, second.ServerTick);
    }
}
