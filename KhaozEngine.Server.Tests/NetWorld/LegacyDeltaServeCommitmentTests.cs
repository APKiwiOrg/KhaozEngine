using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;
using static KhaozEngine.Tests.NetWorld.LegacyDeltaServeFixture;

namespace KhaozEngine.Tests.NetWorld;

public class LegacyDeltaServeCommitmentTests
{
    public enum Failure { Body, MissingSlot, SilentDrop, ThrowingTransport }

    [Theory]
    [InlineData(false, Failure.Body)]
    [InlineData(false, Failure.MissingSlot)]
    [InlineData(false, Failure.SilentDrop)]
    [InlineData(false, Failure.ThrowingTransport)]
    [InlineData(true, Failure.Body)]
    [InlineData(true, Failure.MissingSlot)]
    [InlineData(true, Failure.SilentDrop)]
    [InlineData(true, Failure.ThrowingTransport)]
    public void OnlyAFailedBodyBuildLeavesThePreviousCommitment(bool sharded, Failure failure)
    {
        using var server = new LegacyDeltaServeFixture(sharded, 1);
        (World world, Entity actor) = server.Spawn(new Payload(1));
        long actorId = world.Get<NetId>(actor).Value;
        byte[]? observed = null;
        server.Transport.Observe = (_, frame, _) => observed = frame.ToArray();
        server.Tick();
        Assert.Equal(1, Sequence(Assert.IsType<byte[]>(observed)));
        server.Clients[0].Drain();

        int slot = server.Clients[0].Net.Slot;
        long owner = 0;
        World? servedWorld = null;
        var interest = new HashSet<long>();
        server.Observe = (_, served, set, ownerId) =>
        {
            servedWorld = served;
            owner = ownerId;
            interest.UnionWith(set);
        };

        void AssertNextBaseline()
        {
            // This writer-only probe observes the immediate commitment. An undelivered frame needs a fresh receiver.
            server.Writer.BeginTick();
            byte[] next = server.Writer.WriteFor(slot, Assert.IsType<World>(servedWorld), interest, owner);
            Assert.Equal(failure == Failure.Body ? 1 : 2, BinaryPrimitives.ReadInt32LittleEndian(next));
            Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(next.AsSpan(4)));
            Assert.Contains(actorId, interest);
            Assert.Equal(failure == Failure.Body ? 1 : 0, BinaryPrimitives.ReadInt32LittleEndian(next.AsSpan(12)));
        }

        observed = null;
        world.Set(actor, new Payload(2));
        if (failure == Failure.MissingSlot)
        {
            bool servedFromMessage = false;
            server.OnGameMessage((_, _, _) =>
            {
                // NetServer already detached the connection while polling, but the host has yet to drain Left.
                server.Tick();
                Assert.Null(observed);
                AssertNextBaseline();
                servedFromMessage = true;
            });
            Seat seat = server.Clients[0];
            seat.Send(MoveProtocol.EncodeGameMessage(1, new byte[] { 91 }));
            server.Disconnect(seat);
            server.Poll();
            Assert.True(servedFromMessage);
            return;
        }

        var fault = new IOException();
        switch (failure)
        {
            case Failure.Body:
                server.Writer.BeforeLegacyBodyCompletedForTests = () => throw fault;
                break;
            case Failure.SilentDrop:
                server.Disconnect(server.Clients[0]);
                break;
            case Failure.ThrowingTransport:
                server.Transport.Observe = (_, _, _) => throw fault;
                break;
        }

        Exception? raised = Record.Exception(server.Tick);
        if (failure is Failure.Body or Failure.ThrowingTransport) Assert.Same(fault, raised);
        else Assert.Null(raised);
        if (failure == Failure.SilentDrop)
        {
            Assert.Equal(2, Sequence(Assert.IsType<byte[]>(observed)));
            NetClient receiver = server.Clients[0].Net;
            receiver.Poll();
            Assert.True(receiver.TryDequeueEvent(out ClientSessionEvent ended));
            Assert.Equal(ClientSessionEventKind.Disconnected, ended.Kind);
        }
        else Assert.Null(observed);

        server.Writer.BeforeLegacyBodyCompletedForTests = null;
        AssertNextBaseline();
    }

    private static int Sequence(byte[] frame) => BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(18));
}
