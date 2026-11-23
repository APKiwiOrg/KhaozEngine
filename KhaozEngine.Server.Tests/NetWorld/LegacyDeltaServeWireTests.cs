using System;
using System.Collections.Generic;
using System.IO;
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
    private const long FirstOwner = 0x0000_1234_5678_9ABC;
    private const long PreviousTick = 0x0102_0304_0506_0708;
    private const int FirstAck = 0x1234_5678;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothHeaderKindsMatchIndependentBytesAtTheirActualDestinations(bool sharded)
    {
        long actorId = 0;
        bool actorVisible = false;
        // The players see one another on the first tick. The observer's copy has no private component.
        using var server = new LegacyDeltaServeFixture(sharded, 2, (_, id) => id != actorId || actorVisible,
            firstNetId: FirstOwner, previousTick: PreviousTick);
        Seat plain = server.Clients[0], ticked = server.Clients[1];
        long otherOwner = FirstOwner + 1;
        Assert.Equal(FirstOwner, server.OwnerOf(plain));
        Assert.Equal(otherOwner, server.OwnerOf(ticked));
        server.SetPlayerSecret(plain, 111);
        server.SetPlayerSecret(ticked, 222);
        (World world, Entity actor) = server.Spawn(new Payload(1));
        actorId = world.Get<NetId>(actor).Value;
        var received = new Dictionary<NetConnectionId, List<byte[]>>
        {
            [plain.Connection] = new(),
            [ticked.Connection] = new(),
        };

        server.Transport.ObserveAll = (connection, frame, reliability) =>
        {
            Assert.Equal(NetChannelReliability.ReliableOrdered, reliability);
            received[connection].Add(frame.ToArray());
        };

        void RunTick(int seq, long[] removed, params Change[] changes)
        {
            int plainAck = FirstAck + seq;
            int tickedAck = FirstAck + 0x0100_0000 + seq;
            plain.Send(MoveProtocol.EncodeMove(plainAck, MoveCommand.Idle));
            ticked.Send(MoveProtocol.EncodeMove(tickedAck, MoveCommand.Idle));
            server.Poll();
            server.Tick();
            long tick = PreviousTick + seq;
            Assert.Equal(tick, server.ServerTick);
            Assert.Equal(seq, received[plain.Connection].Count);
            Assert.Equal(seq, received[ticked.Connection].Count);
            if (seq == 1)
            {
                Assert.Equal(ExpectedFrame(null, FirstOwner, plainAck, -1, seq, removed,
                    new(FirstOwner, true, SecretValue: 111), new(otherOwner, true)), received[plain.Connection][^1]);
                Assert.Equal(ExpectedFrame(tick, otherOwner, tickedAck, -1, seq, removed,
                    new(FirstOwner, true), new(otherOwner, true, SecretValue: 222)), received[ticked.Connection][^1]);
            }
            else
            {
                Assert.Equal(ExpectedFrame(null, FirstOwner, plainAck, seq - 1, seq, removed, changes),
                    received[plain.Connection][^1]);
                Assert.Equal(ExpectedFrame(tick, otherOwner, tickedAck, seq - 1, seq, removed, changes),
                    received[ticked.Connection][^1]);
            }
        }

        RunTick(1, []);
        actorVisible = true;
        RunTick(2, [], new Change(actorId, true, PayloadValue: new Payload(1)));
        world.Set(actor, new Payload(2));
        RunTick(3, [], new Change(actorId, false, PayloadValue: new Payload(2)));

        ticked.Send(MoveProtocol.EncodeReplicationAck(2));
        world.Set(actor, new Payload(3));
        RunTick(4, [], new Change(actorId, false, PayloadValue: new Payload(3)));
        Assert.Equal(received[plain.Connection][2].Length, received[plain.Connection][3].Length);
        Assert.Equal(received[ticked.Connection][2].Length, received[ticked.Connection][3].Length);

        world.Remove<Payload>(actor);
        RunTick(5, [], new Change(actorId, false, RemovePayload: true));
        actorVisible = false;
        RunTick(6, [actorId]);

        actorVisible = true;
        var large = new Payload(4, Words: 1024);
        world.Set(actor, large);
        RunTick(7, [], new Change(actorId, true, PayloadValue: large));
        Assert.True(received[plain.Connection][6].Length > received[plain.Connection][0].Length);
        Assert.True(received[ticked.Connection][6].Length > received[ticked.Connection][0].Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATickHelloProcessedDuringCaptureSelectsTheHeaderForThatFrame(bool sharded)
    {
        Action? duringCapture = null;
        using var server = new LegacyDeltaServeFixture(sharded, 1, firstNetId: FirstOwner, previousTick: PreviousTick,
            onPayloadCapture: () => duringCapture?.Invoke());
        Seat seat = server.Clients[0];
        (World world, Entity actor) = server.Spawn(new Payload(1));
        long actorId = world.Get<NetId>(actor).Value;
        server.Tick();
        world.Set(actor, new Payload(3));
        duringCapture = () =>
        {
            duringCapture = null;
            seat.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.ServerTickCapable));
            server.Poll();
        };
        byte[]? frame = null;
        server.Transport.ObserveAll = (_, bytes, _) => frame = bytes.ToArray();

        server.Tick();

        Assert.Equal(ExpectedFrame(PreviousTick + 2, FirstOwner, -1, 1, 2, [],
            new Change(actorId, false, PayloadValue: new Payload(3))), frame);
    }

    private readonly record struct Change(long Id, bool IsNew, int? SecretValue = null, Payload? PayloadValue = null,
        bool RemovePayload = false);

    // Encode the public wire layout directly, without calling a server, session, or body encoder.
    private static byte[] ExpectedFrame(long? tick, long owner, int ack, int baseline, int sequence,
        long[] removed, params Change[] changed)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0x10);
        writer.Write(tick.HasValue ? (byte)8 : (byte)2);
        if (tick is long number) writer.Write(BitConverter.GetBytes(number));
        writer.Write(BitConverter.GetBytes(owner));
        writer.Write(BitConverter.GetBytes(ack));

        writer.Write(baseline);
        writer.Write(sequence);
        writer.Write(removed.Length);
        foreach (long id in removed) writer.Write(id);
        writer.Write(changed.Length);
        foreach (Change entry in changed)
        {
            writer.Write(entry.Id);
            writer.Write(entry.IsNew);
            writer.Write(entry.RemovePayload ? 1 : 0);
            if (entry.RemovePayload) writer.Write((ushort)16);
            if (entry.SecretValue is int secret)
            {
                writer.Write((ushort)17);
                writer.Write7BitEncodedInt(4);
                writer.Write(secret);
            }
            if (entry.PayloadValue is Payload payload)
            {
                writer.Write((ushort)16);
                writer.Write7BitEncodedInt(4 + 4 * payload.Words);
                writer.Write(payload.Words);
                for (int i = 0; i < payload.Words; i++) writer.Write(payload.Value);
            }
            writer.Write((ushort)0);
        }
        return stream.ToArray();
    }
}
