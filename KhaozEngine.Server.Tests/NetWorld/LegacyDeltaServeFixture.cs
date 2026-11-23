using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

internal sealed class LegacyDeltaServeFixture : IDisposable
{
    internal const float Dt = 1f / 30f;
    internal const ushort PayloadType = MoveProtocol.FirstConsumerTypeId;
    internal const ushort SecretType = PayloadType + 1;

    internal readonly record struct Payload(int Value, int Words = 1) : IComponent;
    internal readonly record struct Secret(int Value) : IComponent;

    private readonly InMemoryTransportHub hub = new();
    private readonly WorldServer? flat;
    private readonly ShardedWorldServer? sharded;

    internal LegacyDeltaServeFixture(bool useShards, int sessions, Func<int, long, bool>? visible = null,
        bool legacy = true, bool allTicked = false, long firstNetId = 1, long previousTick = 0,
        Action? onPayloadCapture = null)
    {
        Transport = new LegacyDeltaServeTransport(hub.Server);
        Registry.Register<Secret>(SecretType, static (s, bw) => bw.Write(s.Value),
            static br => new Secret(br.ReadInt32()), channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        Action<Payload, System.IO.BinaryWriter> writePayload = onPayloadCapture is null ? WritePayload : (p, bw) =>
        {
            onPayloadCapture();
            WritePayload(p, bw);
        };
        Registry.Register(PayloadType, writePayload, static br =>
        {
            int words = br.ReadInt32();
            int value = 0;
            for (int i = 0; i < words; i++) value = br.ReadInt32();
            return new Payload(value, words);
        });

        if (useShards)
            sharded = new ShardedWorldServer(Transport,
                new ShardedWorldServerConfig
                {
                    MaxPlayers = sessions,
                    EntityVisibleToSlot = visible,
                    SpawnPosition = static _ => new Vector3(8f, 0f, 8f),
                },
                static (_, _) => 0f, MoveTuning.Default, registry: Registry);
        else
            flat = new WorldServer(Transport,
                new WorldServerConfig
                {
                    MaxPlayers = sessions,
                    EntityVisibleToSlot = visible,
                    SpawnPosition = static _ => new Vector3(8f, 0f, 8f),
                },
                static (_, _) => 0f, MoveTuning.Default, registry: Registry);

        if (firstNetId != 1 || previousTick != 0)
        {
            if (flat is not null) flat.SeedLegacyHeaderForTests(firstNetId, previousTick);
            else sharded!.SeedLegacyHeaderForTests(firstNetId, previousTick);
        }

        Clients = new Seat[sessions];
        for (int i = 0; i < sessions; i++)
        {
            INetTransport endpoint = hub.CreateClient();
            Clients[i] = new Seat(endpoint, new NetClient(endpoint, TestHandshake.Wire($"viewer{i}")),
                new NetConnectionId(i + 1), allTicked || (i & 1) != 0);
            Clients[i].Net.Poll();
        }

        Poll();
        foreach (Seat seat in Clients)
        {
            seat.Drain();
            Assert.True(seat.Net.Slot >= 0);
            if (legacy) seat.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable));
            if (seat.Ticked) seat.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.ServerTickCapable));
        }
        Poll();
    }

    private static void WritePayload(Payload payload, System.IO.BinaryWriter writer)
    {
        writer.Write(payload.Words);
        for (int i = 0; i < payload.Words; i++) writer.Write(payload.Value);
    }

    internal sealed record Seat(INetTransport Endpoint, NetClient Net, NetConnectionId Connection, bool Ticked)
    {
        internal void Send(byte[] payload) => Net.Send(payload, NetChannelReliability.ReliableOrdered);

        internal void Drain()
        {
            Net.Poll();
            while (Net.TryDequeueEvent(out _)) { }
        }
    }

    internal ReplicationRegistry Registry { get; } = new();
    internal LegacyDeltaServeTransport Transport { get; }
    internal Seat[] Clients { get; }
    internal long ServerTick => flat?.ServerTick ?? sharded!.ServerTick;
    internal AoiDeltaReplicator Writer => (flat is not null ? flat.DeltaReplicatorForTest : sharded!.DeltaReplicatorForTest)!;

    internal Action<int, World, IReadOnlySet<long>, long>? Observe
    {
        set
        {
            if (flat is not null) flat.ServeObservedForTest = value;
            else sharded!.ServeObservedForTest = value;
        }
    }

    internal void Poll()
    {
        if (flat is not null) flat.Poll();
        else sharded!.Poll();
    }

    internal void Tick()
    {
        if (flat is not null) flat.Tick(Dt);
        else sharded!.Tick(Dt);
    }

    internal event Action<float> BeforeTick
    {
        add
        {
            if (flat is not null) flat.OnBeforeTick += value;
            else sharded!.OnBeforeTick += value;
        }
        remove
        {
            if (flat is not null) flat.OnBeforeTick -= value;
            else sharded!.OnBeforeTick -= value;
        }
    }

    internal void AfterTick(Action action)
    {
        void Once(float _)
        {
            if (flat is not null) flat.OnAfterTick -= Once;
            else sharded!.OnAfterTick -= Once;
            action();
        }

        if (flat is not null) flat.OnAfterTick += Once;
        else sharded!.OnAfterTick += Once;
    }

    internal void OnGameMessage(ServerGameMessageHandler action)
    {
        if (flat is not null) flat.OnGameMessage += action;
        else sharded!.OnGameMessage += action;
    }

    internal void SendOrdinaryMessage(int slot)
    {
        if (flat is not null)
            flat.SendGameMessageTo(slot, 1, new byte[] { 53, 71 }, NetChannelReliability.ReliableOrdered);
        else
            sharded!.SendGameMessageTo(slot, 1, new byte[] { 53, 71 }, NetChannelReliability.ReliableOrdered);
    }

    internal void Disconnect(Seat seat) => hub.DisconnectClient(seat.Endpoint);

    internal long OwnerOf(Seat seat)
    {
        bool found = flat is not null ? flat.TryGetPlayerNetId(seat.Net.Slot, out long id)
            : sharded!.TryGetPlayerNetId(seat.Net.Slot, out id);
        Assert.True(found);
        return id;
    }

    internal (World World, Entity Entity) Spawn(Payload payload)
    {
        (World, Entity) placed = default;
        void Configure(World world, Entity entity)
        {
            world.Set(entity, payload);
            placed = (world, entity);
        }

        if (flat is not null) flat.SpawnEntity(10f, 10f, Configure);
        else sharded!.SpawnEntity(10f, 10f, Configure);
        return placed;
    }

    internal void SetPlayerSecret(Seat seat, int secret)
    {
        long id = OwnerOf(seat);
        if (flat is not null)
        {
            Entity entity = DeltaReliabilityRig.FindEntity(flat.World, id);
            flat.World.Set(entity, new Secret(secret));
        }
        else
        {
            Assert.True(sharded!.Host.TryGetOwner(id, out var cell, out Entity entity));
            cell.World.Set(entity, new Secret(secret));
        }
    }

    public void Dispose()
    {
        sharded?.Dispose();
        flat?.Dispose();
        hub.Dispose();
    }
}

/// <summary>Observes the authority's actual borrowed span and meters the in-memory adapter independently.</summary>
internal sealed class LegacyDeltaServeTransport(INetTransport inner) : INetTransport
{
    internal delegate void ObserveSend(NetConnectionId target, ReadOnlySpan<byte> frame, NetChannelReliability reliability);

    internal ObserveSend? Observe { get; set; }
    internal ObserveSend? ObserveAll { get; set; }
    internal bool Meter { get; set; }
    internal long TransportBytes { get; set; }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        ObserveAll?.Invoke(target, payload, reliability);
        bool legacy = payload.Length > 1 && payload[0] == (byte)SessionOpcode.Data
            && payload[1] is (byte)MoveProtocol.ServerFrameKind.Delta or (byte)MoveProtocol.ServerFrameKind.TickedDelta;
        if (legacy) Observe?.Invoke(target, payload, reliability);

        if (Meter && legacy)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            inner.Send(target, payload, reliability);
            TransportBytes += GC.GetAllocatedBytesForCurrentThread() - start;
        }
        else inner.Send(target, payload, reliability);
    }

    public void Poll() => inner.Poll();
    public bool TryDequeueEvent(out NetEvent ev) => inner.TryDequeueEvent(out ev);
    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Dispose() => inner.Dispose();
}
