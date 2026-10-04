using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The server tick on the wire: the ticked frame codec, <c>ServerTick</c> counting one per <c>Tick</c> call on both
/// servers, and the <see cref="MoveProtocol.ClientControlKind.ServerTickCapable"/> hello that turns a slot's frames
/// into <see cref="MoveProtocol.ServerFrameKind.TickedSnapshot"/> or <see cref="MoveProtocol.ServerFrameKind.TickedDelta"/>.
/// </summary>
public class ServerTickWireTests
{
    private const float Dt = 1f / 30f;

    [Fact]
    public void TickedFramesRoundTrip()
    {
        byte[] snapshot = { 1, 2, 3, 250 };
        foreach (long tick in new[] { 0L, 1L, long.MaxValue })
        {
            foreach (int ack in new[] { -1, 7 })
            {
                byte[] frame = MoveProtocol.EncodeTickedSnapshotFrame(tick, 1L << 40, ack, snapshot);

                Assert.Equal(20 + snapshot.Length, frame.Length);
                Assert.True(MoveProtocol.TryDecodeTickedSnapshotFrame(frame, out long decodedTick, out long netId,
                    out int decodedAck, out byte[] body));
                Assert.Equal(tick, decodedTick);
                Assert.Equal(1L << 40, netId);
                Assert.Equal(ack, decodedAck);
                Assert.Equal(snapshot, body);
            }
        }
    }

    [Fact]
    public void AShortTickedFrameIsRefused()
    {
        Assert.False(MoveProtocol.TryDecodeTickedSnapshotFrame(new byte[19], out long tick, out long netId,
            out int ack, out byte[] body));
        Assert.Equal(-1L, tick);
        Assert.Equal(-1L, netId);
        Assert.Equal(-1, ack);
        Assert.Empty(body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServerTickCountsTickCalls(bool sharded)
    {
        using var hub = new InMemoryTransportHub();
        ServerUnderTest server = ServerUnderTest.Create(sharded, hub);
        var client = new RawTickClient(hub.CreateClient(), "player", deltaHello: false, tickHello: false);
        client.Join(server);
        var before = new List<long>();
        var after = new List<long>();
        server.OnBeforeTick(() => before.Add(server.ServerTick));
        server.OnAfterTick(() => after.Add(server.ServerTick));

        Assert.Equal(0L, server.ServerTick);
        server.Tick(Dt);
        server.Tick(Dt);
        Assert.Equal(new[] { 1L, 2L }, before);
        Assert.Equal(new[] { 1L, 2L }, after);

        // A third of a step fills no accumulator. It is still a Tick call, so it still counts.
        server.Tick(Dt / 3f);
        Assert.Equal(3L, server.ServerTick);
        Assert.Equal(new[] { 1L, 2L, 3L }, before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FramesStayPlainWithoutTheHello(bool sharded, bool deltas)
    {
        using var hub = new InMemoryTransportHub();
        ServerUnderTest server = ServerUnderTest.Create(sharded, hub);
        var client = new RawTickClient(hub.CreateClient(), "player", deltaHello: deltas, tickHello: false);
        client.Join(server);
        server.Poll();   // takes the hellos

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }

        MoveProtocol.ServerFrameKind expected = deltas ? MoveProtocol.ServerFrameKind.Delta : MoveProtocol.ServerFrameKind.Snapshot;
        AssertPlainFrames(client, server, expected, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnknownControlKindIsIgnored(bool sharded)
    {
        using var hub = new InMemoryTransportHub();
        ServerUnderTest server = ServerUnderTest.Create(sharded, hub);
        var suspicious = new List<SuspiciousActivity>();
        server.OnSuspiciousActivity(suspicious.Add);
        var client = new RawTickClient(hub.CreateClient(), "player", deltaHello: false, tickHello: false);
        client.Join(server);
        server.Poll();   // takes the hellos
        client.SendControl((MoveProtocol.ClientControlKind)0x7F);
        server.Poll();   // takes the unknown control

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }

        AssertPlainFrames(client, server, MoveProtocol.ServerFrameKind.Snapshot, 6);
        Assert.Empty(suspicious);
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
    }

    // Every frame is plain and its header names the receiver with no move acknowledged (the client sent none).
    private static void AssertPlainFrames(RawTickClient client, ServerUnderTest server,
        MoveProtocol.ServerFrameKind expected, int count)
    {
        long ownNetId = client.NetIdOn(server);
        Assert.Equal(count, client.Frames.Count);
        foreach (ServedFrame frame in client.Frames)
        {
            Assert.Equal(expected, frame.Kind);
            Assert.True(MoveProtocol.TryDecodeSnapshotFrame(frame.Payload, out long netId, out int ack, out _));
            Assert.Equal(ownNetId, netId);
            Assert.Equal(-1, ack);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheHelloTurnsFramesTicked(bool sharded, bool deltas)
    {
        using var hub = new InMemoryTransportHub();
        ServerUnderTest server = ServerUnderTest.Create(sharded, hub);
        var client = new RawTickClient(hub.CreateClient(), "player", deltaHello: deltas, tickHello: true);
        client.Join(server);
        server.Poll();   // takes the hellos
        long servingTick = -1;
        server.OnAfterTick(() => servingTick = server.ServerTick);

        MoveProtocol.ServerFrameKind expected = deltas
            ? MoveProtocol.ServerFrameKind.TickedDelta
            : MoveProtocol.ServerFrameKind.TickedSnapshot;
        for (int i = 0; i < 6; i++)
        {
            servingTick = -1;
            server.Poll();
            server.Tick(Dt);
            client.Frames.Clear();
            client.Poll();

            Assert.Equal(i + 1, servingTick);
            ServedFrame frame = Assert.Single(client.Frames);
            Assert.Equal(expected, frame.Kind);
            Assert.True(MoveProtocol.TryDecodeTickedSnapshotFrame(frame.Payload, out long tick, out long netId,
                out _, out _));
            Assert.Equal(servingTick, tick);
            Assert.Equal(client.NetIdOn(server), netId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASlotThatLeavesForgetsTheHello(bool sharded)
    {
        using var hub = new InMemoryTransportHub();
        ServerUnderTest server = ServerUnderTest.Create(sharded, hub);
        var ticked = new RawTickClient(hub.CreateClient(), "ticked", deltaHello: true, tickHello: true);
        ticked.Join(server);
        server.Poll();
        for (int i = 0; i < 3; i++) { server.Poll(); server.Tick(Dt); ticked.Poll(); }
        Assert.All(ticked.Frames, f => Assert.Equal(MoveProtocol.ServerFrameKind.TickedDelta, f.Kind));
        int slot = ticked.Slot;

        hub.DisconnectClient(ticked.Transport);
        server.Poll();
        var plain = new RawTickClient(hub.CreateClient(), "plain", deltaHello: true, tickHello: false);
        plain.Join(server);
        server.Poll();
        Assert.Equal(slot, plain.Slot);

        for (int i = 0; i < 3; i++) { server.Poll(); server.Tick(Dt); plain.Poll(); }
        Assert.Equal(3, plain.Frames.Count);
        Assert.All(plain.Frames, f => Assert.Equal(MoveProtocol.ServerFrameKind.Delta, f.Kind));
    }

    private readonly record struct ServedFrame(MoveProtocol.ServerFrameKind Kind, byte[] Payload);

    /// <summary>One of the two servers behind the members these facts drive.</summary>
    private sealed class ServerUnderTest
    {
        private readonly WorldServer? flat;
        private readonly ShardedWorldServer? sharded;

        private ServerUnderTest(WorldServer? flat, ShardedWorldServer? sharded)
        {
            this.flat = flat;
            this.sharded = sharded;
        }

        public static ServerUnderTest Create(bool sharded, InMemoryTransportHub hub) => sharded
            ? new(null, new ShardedWorldServer(hub.Server,
                new ShardedWorldServerConfig { TickSeconds = Dt, MaxPlayers = 4 }, (_, _) => 0f, MoveTuning.Default))
            : new(new WorldServer(hub.Server,
                new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 4 }, (_, _) => 0f, MoveTuning.Default), null);

        public long ServerTick => flat?.ServerTick ?? sharded!.ServerTick;

        public void Poll()
        {
            if (flat is not null) flat.Poll();
            else sharded!.Poll();
        }

        public void Tick(float dt)
        {
            if (flat is not null) flat.Tick(dt);
            else sharded!.Tick(dt);
        }

        public void OnBeforeTick(Action action)
        {
            if (flat is not null) flat.OnBeforeTick += _ => action();
            else sharded!.OnBeforeTick += _ => action();
        }

        public void OnAfterTick(Action action)
        {
            if (flat is not null) flat.OnAfterTick += _ => action();
            else sharded!.OnAfterTick += _ => action();
        }

        public void OnSuspiciousActivity(Action<SuspiciousActivity> action)
        {
            if (flat is not null) flat.OnSuspiciousActivity += action;
            else sharded!.OnSuspiciousActivity += action;
        }

        public bool TryGetPlayerNetId(int slot, out long netId) => flat is not null
            ? flat.TryGetPlayerNetId(slot, out netId)
            : sharded!.TryGetPlayerNetId(slot, out netId);
    }

    /// <summary>A raw client that sends the chosen hellos on join and records every replication frame it is served.</summary>
    private sealed class RawTickClient
    {
        private readonly NetClient net;
        private readonly bool deltaHello;
        private readonly bool tickHello;

        public RawTickClient(INetTransport transport, string account, bool deltaHello, bool tickHello)
        {
            Transport = transport;
            net = new NetClient(transport, TestHandshake.Wire(account));
            this.deltaHello = deltaHello;
            this.tickHello = tickHello;
        }

        public INetTransport Transport { get; }
        public int Slot => net.Slot;
        public List<ServedFrame> Frames { get; } = new();

        public void Join(ServerUnderTest server)
        {
            Poll();
            server.Poll();
            Poll();
            Assert.True(net.Slot >= 0);
            Assert.True(server.TryGetPlayerNetId(net.Slot, out _));
        }

        public long NetIdOn(ServerUnderTest server)
        {
            Assert.True(server.TryGetPlayerNetId(net.Slot, out long netId));
            return netId;
        }

        public void Poll()
        {
            net.Poll();
            while (net.TryDequeueEvent(out ClientSessionEvent ev))
            {
                if (ev.Kind == ClientSessionEventKind.Joined)
                {
                    if (deltaHello) SendControl(MoveProtocol.ClientControlKind.DeltaCapable);
                    if (tickHello) SendControl(MoveProtocol.ClientControlKind.ServerTickCapable);
                }
                else if (ev.Kind == ClientSessionEventKind.Data
                    && MoveProtocol.TryDecodeServerFrame(ev.Data, out MoveProtocol.ServerFrameKind kind, out byte[] payload)
                    && kind is not MoveProtocol.ServerFrameKind.Notice and not MoveProtocol.ServerFrameKind.GameMessage)
                {
                    Frames.Add(new ServedFrame(kind, payload));
                }
            }
        }

        public void SendControl(MoveProtocol.ClientControlKind kind) =>
            net.Send(MoveProtocol.EncodeClientControl(kind), NetChannelReliability.ReliableOrdered);
    }
}
