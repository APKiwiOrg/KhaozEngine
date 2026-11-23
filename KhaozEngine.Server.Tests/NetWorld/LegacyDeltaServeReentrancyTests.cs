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
