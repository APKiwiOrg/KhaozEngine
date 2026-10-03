using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>Which authoritative host a format 2 negotiation case runs against.</summary>
public enum RebuildHostKind
{
    World,
    Sharded,
}

/// <summary>
/// The spec's four-way capability matrix on each host, driven by raw client controls: server opt-in by client
/// request, at the current builtin generation. Also the transport-limit fallback, accepting and refusing clients, the
/// end of legacy serving at the mode 1 offer and generation skew. Real <see cref="WorldClient"/> peers repeat the
/// matrix later.
/// </summary>
public class ReplicationCapabilityMatrixTests
{
    public static IEnumerable<object[]> Hosts() => new[]
    {
        new object[] { RebuildHostKind.World },
        new object[] { RebuildHostKind.Sharded },
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ServerDefaultClientDefaultStaysLegacyWithNoOffer(RebuildHostKind kind)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: false);

        host.Pump(12, client);

        AssertLegacyUnnegotiated(host, client);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ServerOptInClientDefaultStaysLegacyWithNoOffer(RebuildHostKind kind)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: false);

        host.Pump(12, client);

        AssertLegacyUnnegotiated(host, client);
        Assert.Equal(0UL, host.EpochHighWater);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ServerDefaultClientOptInGetsModeZeroForServerPolicy(RebuildHostKind kind)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);

        host.Pump(12, client);

        ReplicationModeOffer offer = Assert.Single(client.Offers);
        Assert.Equal(RebuildProtocol.FallbackOffer(ReplicationSelectionReason.DisabledServerPolicy), offer);
        Assert.Equal(RebuildProtocol.WireReasonServerPolicy, offer.Reason);
        AssertSelection(host, client,
            new ReplicationSelection(ReplicationDeliveryMode.LegacyReliable, ReplicationSelectionReason.DisabledServerPolicy, 0));
        Assert.True(client.LegacyFramesAfterFirstOffer >= 5, "a mode 0 offer keeps legacy serving");
        Assert.Equal(0UL, host.EpochHighWater);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void BothOptInGetModeOneSelectedAndLegacyStops(RebuildHostKind kind)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);

        host.Pump(30, client);

        ReplicationModeOffer offer = Assert.Single(client.Offers);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, offer.Mode);
        Assert.Equal(RebuildProtocol.WireReasonSelected, offer.Reason);
        Assert.Equal(1UL, offer.Epoch);
        Assert.Equal(host.EpochHighWater, offer.Epoch);
        var defaults = new DeltaRebuildOptions();
        Assert.Equal(new ReplicationModeOffer(RebuildProtocol.Format, ReplicationDeliveryMode.AcknowledgedUnreliable,
            RebuildProtocol.WireReasonSelected, (ushort)defaults.MaxRetainedProjections,
            (uint)defaults.MaxRetainedPayloadBytes, (uint)defaults.MaxKeyframeBytes, (uint)defaults.MaxEntities,
            (uint)defaults.MaxComponents, 512, 4, 1), offer);
        AssertSelection(host, client,
            new ReplicationSelection(ReplicationDeliveryMode.AcknowledgedUnreliable, ReplicationSelectionReason.Selected, 1));
        Assert.True(client.LegacyFramesBeforeFirstOffer >= 1, "legacy state queued before the offer precedes it");
        Assert.Equal(0, client.LegacyFramesAfterFirstOffer);
        Assert.Equal(0, client.V2StateFrames);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ModeOfferIsOneReliable37BytePayload(RebuildHostKind kind)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);

        host.Pump(6, client);

        var offers = transport.Sends
            .Where(s => s.Payload.Length > 1 && s.Payload[1] == (byte)MoveProtocol.ServerFrameKind.ReplicationMode)
            .ToList();
        (NetConnectionId _, byte[] payload, NetChannelReliability reliability) = Assert.Single(offers);
        Assert.Equal(RebuildProtocol.ModeOfferBytes + 1, payload.Length);
        Assert.Equal(NetChannelReliability.ReliableOrdered, reliability);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void UnknownTransportLimitGetsModeZeroForTransportLimit(RebuildHostKind kind)
    {
        var transport = new LimitTransport(unreliableLimit: 0, reliableLimit: 0);
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);

        host.Pump(12, client);

        ReplicationModeOffer offer = Assert.Single(client.Offers);
        Assert.Equal(RebuildProtocol.FallbackOffer(ReplicationSelectionReason.UnavailableTransportLimit), offer);
        AssertSelection(host, client, new ReplicationSelection(ReplicationDeliveryMode.LegacyReliable,
            ReplicationSelectionReason.UnavailableTransportLimit, 0));
        Assert.True(client.LegacyFramesAfterFirstOffer >= 5, "a mode 0 offer keeps legacy serving");
        Assert.Equal(0UL, host.EpochHighWater);
    }

    [Theory]
    [InlineData(RebuildHostKind.World, 0, 1400)]
    [InlineData(RebuildHostKind.World, 1400, 0)]
    [InlineData(RebuildHostKind.World, 280, 1400)]
    [InlineData(RebuildHostKind.World, 1400, 280)]
    [InlineData(RebuildHostKind.Sharded, 0, 1400)]
    [InlineData(RebuildHostKind.Sharded, 1400, 280)]
    public void UnknownOrInfeasibleLimitOnEitherChannelFallsBack(RebuildHostKind kind, int unreliable, int reliable)
    {
        var transport = new LimitTransport(unreliable, reliable);
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);

        host.Pump(8, client);

        Assert.Equal(RebuildProtocol.FallbackOffer(ReplicationSelectionReason.UnavailableTransportLimit),
            Assert.Single(client.Offers));
        Assert.Equal(0UL, host.EpochHighWater);
    }

    [Theory]
    [InlineData(RebuildHostKind.World, 400, 1400, 400)]
    [InlineData(RebuildHostKind.World, 1400, 281, 281)]
    [InlineData(RebuildHostKind.World, 1400, 1400, 512)]
    [InlineData(RebuildHostKind.Sharded, 400, 1400, 400)]
    [InlineData(RebuildHostKind.Sharded, 1400, 1400, 512)]
    public void SelectedCapIsTheSmallestOfConfiguredAndActualLimits(RebuildHostKind kind, int unreliable,
        int reliable, int expectedCap)
    {
        var transport = new LimitTransport(unreliable, reliable);
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);

        host.Pump(6, client);

        ReplicationModeOffer offer = Assert.Single(client.Offers);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, offer.Mode);
        Assert.Equal((uint)expectedCap, offer.PacketCap);
        Assert.True(host.TryGetRebuildStream(client.Slot, out RebuildServerStream stream));
        Assert.Equal(expectedCap, stream.PacketCap);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void AcceptingClientKeepsLegacyStoppedAndGetsNoV2State(RebuildHostKind kind)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);
        host.Pump(4, client);
        ulong epoch = Assert.Single(client.Offers).Epoch;

        client.Send(RebuildProtocol.EncodeAcceptance(epoch));
        host.Pump(30, client);

        Assert.True(host.TryGetRebuildStream(client.Slot, out RebuildServerStream stream));
        Assert.True(stream.Accepted);
        AssertSelection(host, client, new ReplicationSelection(ReplicationDeliveryMode.AcknowledgedUnreliable,
            ReplicationSelectionReason.Selected, epoch));
        Assert.Equal(0, client.LegacyFramesAfterFirstOffer);
        Assert.Equal(0, client.V2StateFrames);
        Assert.Single(client.Offers);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void RefusingClientLeavesAndLegacyNeverResumes(RebuildHostKind kind)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);
        host.Pump(4, client);
        Assert.Single(client.Offers);
        int slot = client.Slot;

        client.Disconnect();
        host.Pump(6, client);

        Assert.Equal(0, host.PlayerCount);
        Assert.False(host.TryGetReplicationSelection(slot, out _));
        Assert.False(host.TryGetRebuildStream(slot, out _));
        Assert.Equal(0, client.LegacyFramesAfterFirstOffer);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void BuiltinGenerationSkewIsStillRejectedByTheAuthenticator(RebuildHostKind kind)
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(kind, transport, allowUnreliable: true);
        byte[] skewed = ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion - 1, consumerVersion: null,
            innerToken: null);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true, token: skewed);

        host.Pump(8, client);

        Assert.False(client.Joined);
        Assert.False(string.IsNullOrEmpty(client.RejectReason));
        Assert.Equal(0, host.PlayerCount);
        Assert.Empty(client.Offers);
        Assert.Equal(0UL, host.EpochHighWater);
    }

    private static void AssertLegacyUnnegotiated(RebuildHost host, RawRebuildClient client)
    {
        Assert.True(client.Joined);
        Assert.Empty(client.Offers);
        AssertSelection(host, client, default);
        Assert.False(host.TryGetRebuildStream(client.Slot, out _));
        Assert.True(client.LegacyFrames >= 10, $"legacy serving continues: {client.LegacyFrames} frames");
    }

    private static void AssertSelection(RebuildHost host, RawRebuildClient client, ReplicationSelection expected)
    {
        Assert.True(host.TryGetReplicationSelection(client.Slot, out ReplicationSelection selection));
        Assert.Equal(expected, selection);
    }
}

/// <summary>
/// An in-memory server transport that answers a configured unfragmented payload limit per channel and records every
/// payload handed to it. A send the session layer refuses for an unknown slot never reaches it, so the record is the
/// evidence of which offers and frames <see cref="NetServer.TrySendTo"/> actually handed over.
/// </summary>
internal sealed class LimitTransport : INetTransport
{
    private readonly INetTransport inner;

    public LimitTransport(int unreliableLimit, int reliableLimit)
    {
        Hub = new InMemoryTransportHub();
        inner = Hub.Server;
        UnreliableLimit = unreliableLimit;
        ReliableLimit = reliableLimit;
    }

    /// <summary>A transport whose limits sit above the default 512-byte cap on both channels.</summary>
    public static LimitTransport Known() => new(1400, 1400);

    public InMemoryTransportHub Hub { get; }
    public int UnreliableLimit { get; set; }
    public int ReliableLimit { get; set; }
    public List<(NetConnectionId Target, byte[] Payload, NetChannelReliability Reliability)> Sends { get; } = new();

    public int MaxUnfragmentedPayloadBytes(NetConnectionId connection, NetChannelReliability reliability) =>
        reliability == NetChannelReliability.ReliableOrdered ? ReliableLimit : UnreliableLimit;

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        Sends.Add((target, payload.ToArray(), reliability));
        inner.Send(target, payload, reliability);
    }

    public void Poll() => inner.Poll();
    public bool TryDequeueEvent(out NetEvent ev) => inner.TryDequeueEvent(out ev);
    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => inner.Disconnect(connection, reason);
    public void Dispose() => inner.Dispose();
}

/// <summary>
/// A raw format 2 negotiating client: joins, advertises <see cref="MoveProtocol.ClientControlKind.DeltaCapable"/> and,
/// when asked, <see cref="MoveProtocol.ClientControlKind.RebuildDeltaCapable"/>, both reliably, and records every
/// server frame in arrival order. It never acknowledges legacy state, so every frame the server serves is visible.
/// </summary>
internal sealed class RawRebuildClient
{
    private readonly NetClient net;
    private readonly bool requestRebuild;

    public RawRebuildClient(INetTransport transport, bool requestRebuild, byte[]? token = null)
    {
        net = new NetClient(transport, token ?? TestHandshake.Wire());
        this.requestRebuild = requestRebuild;
    }

    public bool Joined { get; private set; }
    public int Slot => net.Slot;
    public string? RejectReason { get; private set; }
    public long LocalNetId { get; private set; } = -1;
    public int LastMovementAck { get; private set; } = int.MinValue;
    public List<(MoveProtocol.ServerFrameKind Kind, byte[] Payload)> Frames { get; } = new();
    public List<ReplicationModeOffer> Offers { get; } = new();

    public int LegacyFrames => Frames.Count(f => IsLegacy(f.Kind));

    public int LegacyFramesBeforeFirstOffer
    {
        get
        {
            int offer = FirstOfferIndex;
            return Frames.Take(offer < 0 ? Frames.Count : offer).Count(f => IsLegacy(f.Kind));
        }
    }

    public int LegacyFramesAfterFirstOffer
    {
        get
        {
            int offer = FirstOfferIndex;
            return offer < 0 ? 0 : Frames.Skip(offer + 1).Count(f => IsLegacy(f.Kind));
        }
    }

    public int V2StateFrames => Frames.Count(f => f.Kind is MoveProtocol.ServerFrameKind.RebuildDelta
        or MoveProtocol.ServerFrameKind.RebuildKeyframeChunk);

    private int FirstOfferIndex => Frames.FindIndex(f => f.Kind == MoveProtocol.ServerFrameKind.ReplicationMode);

    public void Send(byte[] frame, NetChannelReliability reliability = NetChannelReliability.ReliableOrdered) =>
        net.Send(frame, reliability);

    public void SendMove(int seq, in MoveCommand cmd) => Send(MoveProtocol.EncodeMove(seq, cmd));

    public void Disconnect() => net.Disconnect();

    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    Joined = true;
                    Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable));
                    if (requestRebuild)
                        Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.RebuildDeltaCapable));
                    break;
                case ClientSessionEventKind.Rejected:
                    RejectReason = ev.RejectReason;
                    break;
                case ClientSessionEventKind.Data:
                    OnFrame(ev.Data);
                    break;
            }
        }
    }

    private void OnFrame(byte[] data)
    {
        if (!MoveProtocol.TryDecodeServerFrame(data, out MoveProtocol.ServerFrameKind kind, out byte[] payload)) return;
        Frames.Add((kind, payload));
        if (IsLegacy(kind) && MoveProtocol.TryDecodeSnapshotFrame(payload, out long localNetId, out int ack, out _))
        {
            LocalNetId = localNetId;
            LastMovementAck = ack;
        }
        else if (kind == MoveProtocol.ServerFrameKind.ReplicationMode)
        {
            Assert.True(RebuildProtocol.TryDecodeModeOffer(payload, out ReplicationModeOffer offer), "mode offer decodes");
            Offers.Add(offer);
        }
    }

    private static bool IsLegacy(MoveProtocol.ServerFrameKind kind) =>
        kind is MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta;
}

/// <summary>One adapter over <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/> for host-agnostic
/// negotiation cases.</summary>
internal sealed class RebuildHost
{
    public const float Dt = 1f / 30f;
    public static readonly Vector3 Spawn = new(5f, 0f, 5f);

    private readonly WorldServer? flat;
    private readonly ShardedWorldServer? sharded;

    private RebuildHost(WorldServer? flat, ShardedWorldServer? sharded)
    {
        this.flat = flat;
        this.sharded = sharded;
        if (flat is not null) flat.OnSuspiciousActivity += Suspicious.Add;
        if (sharded is not null) sharded.OnSuspiciousActivity += Suspicious.Add;
    }

    public List<SuspiciousActivity> Suspicious { get; } = new();

    public static RebuildHost Create(RebuildHostKind kind, INetTransport transport, bool allowUnreliable = false,
        bool deltaReplication = true, AntiCheatConfig? antiCheat = null, Func<int, long, bool>? visible = null,
        ReplicationStreamOptions? stream = null)
    {
        antiCheat ??= new AntiCheatConfig();
        stream ??= new ReplicationStreamOptions();
        float Flat(float x, float z) => 0f;
        if (kind == RebuildHostKind.World)
        {
            var config = new WorldServerConfig
            {
                TickSeconds = Dt,
                InterestRadius = 500f,
                MaxPlayers = 8,
                SpawnPosition = _ => Spawn,
                DeltaReplication = deltaReplication,
                AllowUnreliableDeltaReplication = allowUnreliable,
                ReplicationStream = stream,
                AntiCheat = antiCheat,
                EntityVisibleToSlot = visible,
            };
            return new RebuildHost(new WorldServer(transport, config, Flat, MoveTuning.Default), null);
        }
        var shardedConfig = new ShardedWorldServerConfig
        {
            TickSeconds = Dt,
            MaxPlayers = 8,
            SpawnPosition = _ => Spawn,
            DeltaReplication = deltaReplication,
            AllowUnreliableDeltaReplication = allowUnreliable,
            ReplicationStream = stream,
            AntiCheat = antiCheat,
            EntityVisibleToSlot = visible,
        };
        return new RebuildHost(null, new ShardedWorldServer(transport, shardedConfig, Flat, MoveTuning.Default));
    }

    public int PlayerCount => flat?.PlayerCount ?? sharded!.PlayerCount;
    public ulong EpochHighWater => flat?.ReplicationEpochHighWaterForTest ?? sharded!.ReplicationEpochHighWaterForTest;
    public AoiDeltaReplicator? Writer => flat is not null ? flat.DeltaReplicatorForTest : sharded!.DeltaReplicatorForTest;

    public Action<int, World, IReadOnlySet<long>, long>? ServeObserved
    {
        set
        {
            if (flat is not null) flat.ServeObservedForTest = value;
            else sharded!.ServeObservedForTest = value;
        }
    }

    public void Poll()
    {
        if (flat is not null) flat.Poll();
        else sharded!.Poll();
    }

    public void Tick()
    {
        if (flat is not null) flat.Tick(Dt);
        else sharded!.Tick(Dt);
    }

    public void Pump(int ticks, params RawRebuildClient[] clients)
    {
        for (int i = 0; i < ticks; i++)
        {
            Poll();
            Tick();
            foreach (RawRebuildClient client in clients) client.Poll();
        }
    }

    public bool TryGetReplicationSelection(int slot, out ReplicationSelection selection) => flat is not null
        ? flat.TryGetReplicationSelection(slot, out selection)
        : sharded!.TryGetReplicationSelection(slot, out selection);

    public bool TryGetRebuildStream(int slot, out RebuildServerStream stream) => flat is not null
        ? flat.TryGetRebuildStreamForTest(slot, out stream)
        : sharded!.TryGetRebuildStreamForTest(slot, out stream);

    public Vector3 PositionOf(int slot)
    {
        bool found = flat is not null ? flat.TryGetPlayerState(slot, out PlayerMoveState state)
            : sharded!.TryGetPlayerState(slot, out state);
        Assert.True(found, $"slot {slot} is joined");
        return state.Position;
    }

    public long SpawnEntity(float x, float z) => flat?.SpawnEntity(x, z) ?? sharded!.SpawnEntity(x, z);

    public int CountSuspicious(int slot, SuspiciousReason reason) =>
        Suspicious.Count(s => s.Slot == slot && s.Reason == reason);
}
