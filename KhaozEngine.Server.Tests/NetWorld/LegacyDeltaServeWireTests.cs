using System;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;
using static KhaozEngine.Tests.NetWorld.LegacyDeltaServeFixture;

namespace KhaozEngine.Tests.NetWorld;

public class LegacyDeltaServeWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothViewerHeadersAndBodiesMatchTheOwningEncodersAcrossSizeAndVisibilityChanges(bool sharded)
    {
        long hidden = -1;
        using var server = new LegacyDeltaServeFixture(sharded, 2, (slot, id) => slot != 1 || id != hidden);
        var reference = new AoiDeltaReplicator(server.Registry);
        (World world, Entity actor) = server.Spawn(new Payload(1));
        long actorId = world.Get<NetId>(actor).Value;
        server.SetPlayerSecret(server.Clients[0], 111);
        server.SetPlayerSecret(server.Clients[1], 222);
        var expected = new Dictionary<long, byte[]>();
        var received = new Dictionary<long, byte[]>();
        int move = 0;

        server.Observe = (slot, served, interest, owner) =>
        {
            byte[] body = reference.WriteFor(slot, served, interest, owner);
            bool ticked = server.Clients[slot].Ticked;
            byte[] framed = ticked
                ? MoveProtocol.EncodeTickedSnapshotFrame(server.ServerTick, owner, move, body)
                : MoveProtocol.EncodeSnapshotFrame(owner, move, body);
            expected.Add(owner, SessionFrame.Write(SessionOpcode.Data, MoveProtocol.EncodeServerFrame(
                ticked ? MoveProtocol.ServerFrameKind.TickedDelta : MoveProtocol.ServerFrameKind.Delta, framed)));
        };

        server.Transport.Observe = (_, frame, reliability) =>
        {
            Assert.Equal(NetChannelReliability.ReliableOrdered, reliability);
            received.Add(ReadOwner(frame), frame.ToArray());
        };

        void RunTick()
        {
            expected.Clear();
            received.Clear();
            move++;
            foreach (Seat seat in server.Clients) seat.Send(MoveProtocol.EncodeMove(move, MoveCommand.Idle));
            server.Poll();
            reference.BeginTick();
            server.Tick();
            Assert.Equal(expected.Keys, received.Keys);
            Assert.Equal(2, received.Count);
            foreach ((long owner, byte[] frame) in expected) Assert.Equal(frame, received[owner]);
        }

        RunTick(); // both first/full, each with its own secret
        int full = FirstPlainFrame(received).Length;

        world.Set(actor, new Payload(2));
        RunTick(); // changed component
        int changed = FirstPlainFrame(received).Length;
        Assert.True(changed < full);

        // Only the second viewer acknowledges, so both last-sent baselines still advance in order.
        server.Clients[1].Send(MoveProtocol.EncodeReplicationAck(2));
        reference.Acknowledge(1, 2);
        world.Set(actor, new Payload(3));
        RunTick();
        Assert.Equal(changed, FirstPlainFrame(received).Length);

        hidden = actorId;
        world.Remove<Payload>(actor);
        RunTick(); // component removal for the plain viewer and entity exit for the ticked viewer
        Assert.True(FirstPlainFrame(received).Length < changed);

        hidden = -1;
        world.Set(actor, new Payload(4, Words: 1024));
        RunTick(); // larger frame and re-enter
        Assert.True(FirstPlainFrame(received).Length > full);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedTickFromTransportIsRefusedBeforeItCanChangeTheBorrowedFrame(bool sharded)
    {
        using var server = new LegacyDeltaServeFixture(sharded, 1);
        Exception? nested = null;
        byte[]? original = null;
        byte[]? after = null;
        server.Transport.Observe = (_, frame, _) =>
        {
            server.Transport.Observe = null;
            original = frame.ToArray();
            nested = Record.Exception(server.Tick);
            after = frame.ToArray();
        };

        Exception? outer = Record.Exception(server.Tick);
        Assert.IsType<InvalidOperationException>(nested);
        Assert.Null(outer);
        Assert.Equal(original, after);
        Assert.Equal(1L, server.ServerTick);
        server.Tick(); // the borrow ends with Send
        Assert.Equal(2L, server.ServerTick);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherAuthorityCanServeDuringTheBorrowAndTheSameAuthorityCanMigrateBetweenTicks(bool sharded)
    {
        using var first = new LegacyDeltaServeFixture(sharded, 1);
        using var second = new LegacyDeltaServeFixture(sharded, 2);
        Exception? competing = null;
        Exception? independent = null;
        bool bytesPreserved = false;
        first.Transport.Observe = (_, frame, _) =>
        {
            first.Transport.Observe = null;
            byte[] original = frame.ToArray();
            var thread = new Thread(() =>
            {
                competing = Record.Exception(first.Tick);
                independent = Record.Exception(second.Tick);
            });
            thread.Start();
            thread.Join();
            bytesPreserved = frame.SequenceEqual(original);
        };

        Exception? outer = Record.Exception(first.Tick);
        Assert.IsType<InvalidOperationException>(competing);
        Assert.Null(outer);
        Assert.Null(independent);
        Assert.True(bytesPreserved);
        Exception? migrated = null;
        var continuation = new Thread(() => migrated = Record.Exception(first.Tick));
        continuation.Start();
        continuation.Join();
        Assert.Null(migrated);
        Assert.Equal(2L, first.ServerTick);
        Assert.Equal(1L, second.ServerTick);
    }

    private static byte[] FirstPlainFrame(Dictionary<long, byte[]> frames)
    {
        foreach (byte[] frame in frames.Values)
            if (frame[1] == (byte)MoveProtocol.ServerFrameKind.Delta) return frame;
        throw new InvalidOperationException();
    }

    private static long ReadOwner(ReadOnlySpan<byte> frame) =>
        BitConverter.ToInt64(frame.Slice(frame[1] == (byte)MoveProtocol.ServerFrameKind.TickedDelta ? 10 : 2, 8));
}
