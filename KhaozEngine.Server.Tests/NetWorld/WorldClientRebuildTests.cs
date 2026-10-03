using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Real <see cref="WorldClient"/> format 2 negotiation: the four-way capability matrix on each host, older-server
/// silence, transport-limit fallback, client limit refusal, bad-format incompatibility and generation skew. Also the
/// keyframe chunk boundary cases a client must refuse before any publication.
/// </summary>
public class WorldClientRebuildTests
{
    private static readonly byte[] DeltaCapable = MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable);
    private static readonly byte[] RebuildCapable =
        MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.RebuildDeltaCapable);

    public static IEnumerable<object[]> Matrix()
    {
        foreach (RebuildHostKind kind in new[] { RebuildHostKind.World, RebuildHostKind.Sharded })
            foreach ((bool server, bool client) in new[] { (false, false), (true, false), (false, true), (true, true) })
                yield return new object[] { kind, server, client };
    }

    public static IEnumerable<object[]> Hosts() => ReplicationCapabilityMatrixTests.Hosts();

    [Theory]
    [MemberData(nameof(Matrix))]
    public void RealClientCapabilityMatrix(RebuildHostKind kind, bool serverOptIn, bool clientOptIn)
    {
        ClientRebuildRig rig = ClientRebuildRig.Create(kind, serverOptIn, clientOptIn);

        rig.Steps(12);

        WorldClient client = rig.Client;
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.Contains(rig.Tap.Sent, s => s.Payload.SequenceEqual(DeltaCapable) && s.Reliability == ClientTap.Reliable);
        Assert.Equal(clientOptIn,
            rig.Tap.Sent.Any(s => s.Payload.SequenceEqual(RebuildCapable) && s.Reliability == ClientTap.Reliable));
        Assert.True(rig.Host.TryGetReplicationSelection(0, out ReplicationSelection hostSelection));
        Assert.Equal(hostSelection, client.ReplicationSelection);
        if (!clientOptIn)
        {
            Assert.Equal(default, client.ReplicationSelection);
            Assert.Null(client.DeltaRebuildForTest);
            Assert.True(rig.Tap.LegacyFrames >= 5, "legacy serving continues");
        }
        else if (!serverOptIn)
        {
            Assert.Equal(new ReplicationSelection(ReplicationDeliveryMode.LegacyReliable,
                ReplicationSelectionReason.DisabledServerPolicy, 0), client.ReplicationSelection);
            Assert.True(rig.Tap.LegacyFramesAfterOffer >= 5, "a mode 0 offer keeps legacy serving");
            Assert.Equal(0, client.RebuildDiagnosticsForTest.AcceptedCount);
        }
        else
        {
            Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, client.ReplicationSelection.Mode);
            Assert.Equal(ReplicationSelectionReason.Selected, client.ReplicationSelection.Reason);
            Assert.NotEqual(0UL, client.ReplicationSelection.Epoch);
            Assert.Equal(rig.Host.EpochHighWater, client.ReplicationSelection.Epoch);
            Assert.Equal(0, rig.Tap.LegacyFramesAfterOffer);
            Assert.True(client.RebuildDiagnosticsForTest.AcceptedCount >= 2, "keyframe and steady deltas accepted");
        }
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void LegacyStateNeverSeedsOrOverlaysTheRebuiltWorld(RebuildHostKind kind)
    {
        ClientRebuildRig rig = ClientRebuildRig.Create(kind);
        rig.Steps(12);
        WorldClient client = rig.Client;
        (byte[] legacy, _) = rig.Tap.Received.First(r => r.Frame[0] is (byte)MoveProtocol.ServerFrameKind.Snapshot
            or (byte)MoveProtocol.ServerFrameKind.Delta);
        ClientDeltaRebuild rebuild = client.DeltaRebuildForTest!;
        Assert.True(rebuild.TryGetRetainedForTest(rebuild.LatestAcceptedId!.Value, out ReplicationProjection latest));
        WorldClientRebuildDiagnostics before = client.RebuildDiagnosticsForTest;

        rig.Tap.Inject(legacy, ClientTap.Reliable);
        client.Poll();

        Assert.Equal(before, client.RebuildDiagnosticsForTest);
        Assert.Equal(latest.Entities.Keys.Order(), client.ViewForTest.Entities.Keys.Order());
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void OlderServerSilenceStaysUnnegotiated(RebuildHostKind kind)
    {
        ClientRebuildRig rig = ClientRebuildRig.Create(kind,
            configureTap: tap => tap.DropOutgoing = payload => payload.SequenceEqual(RebuildCapable));

        rig.Steps(30);

        Assert.Equal(WorldConnectionState.Connected, rig.Client.ConnectionState);
        Assert.Equal(default, rig.Client.ReplicationSelection);
        Assert.False(rig.Client.RebuildStreamForTest!.RecoveryActive);
        Assert.True(rig.Client.RebuildDiagnosticsForTest.IngestCount >= 25, "healthy legacy session");
        Assert.Equal(0UL, rig.Host.EpochHighWater);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void UnknownTransportLimitFallsBackToModeZeroReasonOne(RebuildHostKind kind)
    {
        ClientRebuildRig rig = ClientRebuildRig.Create(kind, limit: 0);

        rig.Steps(12);

        Assert.Equal(new ReplicationSelection(ReplicationDeliveryMode.LegacyReliable,
            ReplicationSelectionReason.UnavailableTransportLimit, 0), rig.Client.ReplicationSelection);
        Assert.Equal(WorldConnectionState.Connected, rig.Client.ConnectionState);
        Assert.True(rig.Tap.LegacyFramesAfterOffer >= 5, "legacy serving continues");
        Assert.Empty(rig.Tap.Controls(RebuildControlKind.Accept));
    }

    [Theory]
    [InlineData(RebuildHostKind.World, "packet cap")]
    [InlineData(RebuildHostKind.World, "keyframe bytes")]
    [InlineData(RebuildHostKind.World, "client transport limit")]
    [InlineData(RebuildHostKind.Sharded, "packet cap")]
    public void OfferBeyondClientLimitsRefusesWithPolicy(RebuildHostKind kind, string limit)
    {
        ReplicationStreamOptions stream = limit switch
        {
            "packet cap" => new ReplicationStreamOptions { MaxTransportPayloadBytes = 400 },
            "keyframe bytes" => new ReplicationStreamOptions { Limits = new DeltaRebuildOptions { MaxKeyframeBytes = 32 * 1024 } },
            _ => new ReplicationStreamOptions(),
        };
        ClientRebuildRig rig = ClientRebuildRig.Create(kind, clientStream: stream, reconnecting: true,
            configureTap: tap => tap.Limit = limit == "client transport limit" ? 300 : 0);

        rig.Steps(12);

        Assert.Equal(DisconnectReason.ReplicationPolicyRefused, rig.Client.DisconnectReason);
        Assert.Equal(WorldConnectionState.Disconnected, rig.Client.ConnectionState);
        Assert.Equal(0, rig.Client.ReconnectAttempt);
        Assert.Single(rig.Taps);
        Assert.Empty(rig.Tap.Controls(RebuildControlKind.Accept));
        Assert.Equal(0, rig.Host.PlayerCount);
    }

    [Fact]
    public void BadFormatOfferIsIncompatible()
    {
        ClientRebuildRig rig = ClientRebuildRig.Create(RebuildHostKind.World,
            configureTap: tap => tap.DropOutgoing = payload => payload.SequenceEqual(RebuildCapable));
        var failures = new List<string>();
        rig.Client.SnapshotDecodeFailed += failures.Add;
        rig.Steps(4);
        byte[] offer = RebuildProtocol.EncodeModeOffer(
            RebuildProtocol.SelectedOffer(new ReplicationStreamOptions().StreamLimits(), 512, 4, 3));
        offer[1] = 3;

        rig.Tap.Inject(offer, ClientTap.Reliable);
        rig.Client.Poll();

        Assert.Equal(DisconnectReason.IncompatibleVersion, rig.Client.DisconnectReason);
        Assert.Single(failures);
        Assert.Equal(WorldConnectionState.Disconnected, rig.Client.ConnectionState);
        Assert.Equal(default, rig.Client.ReplicationSelection);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void BuiltinGenerationSkewIsRejected(RebuildHostKind kind)
    {
        byte[] ours = Encoding.UTF8.GetBytes(ProtocolHandshake.WireGenerationLabel(MoveProtocol.WireProtocolVersion));
        byte[] skewed = Encoding.UTF8.GetBytes(ProtocolHandshake.WireGenerationLabel(MoveProtocol.WireProtocolVersion - 1));
        Assert.Equal(ours.Length, skewed.Length);
        ClientRebuildRig rig = ClientRebuildRig.Create(kind, configureTap: tap => tap.RewriteHello = hello =>
        {
            int at = hello.AsSpan().IndexOf(ours);
            Assert.True(at > 0, "the hello carries the wire generation label");
            skewed.CopyTo(hello, at);
            return hello;
        });

        rig.Steps(8);

        Assert.Equal(DisconnectReason.IncompatibleVersion, rig.Client.DisconnectReason);
        Assert.Equal(WorldConnectionState.Disconnected, rig.Client.ConnectionState);
        Assert.Equal(default, rig.Client.ReplicationSelection);
        Assert.DoesNotContain(rig.Tap.Sent, s => s.Payload.SequenceEqual(RebuildCapable));
        Assert.Equal(0UL, rig.Host.EpochHighWater);
        Assert.Equal(0, rig.Host.PlayerCount);
    }

    public static TheoryData<string> ChunkFaults => new()
    {
        "total above keyframe limit", "total below header minimum", "newer epoch mid assembly",
        "other sequence mid assembly", "other stream", "inconsistent total", "excessive chunk count",
        "mismatched low sequence", "width of another offer", "truncated final object",
    };

    [Theory]
    [MemberData(nameof(ChunkFaults))]
    public void ChunkBoundaryFaultsNeverPublish(string fault)
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, fault == "width of another offer" ? 520 : 1400, 7);
        source.Start(5);
        s.Offer(5);
        s.Pump();
        ReplicationDeltaPacket key = source.Build(keyframe: true);
        List<byte[]> frames = source.ChunkFrames(key, fault == "width of another offer" ? 400 : 512);
        int last = frames.Count - 1;
        Assert.True(frames.Count >= 2, "a multi-chunk keyframe");
        switch (fault)
        {
            case "total above keyframe limit": ChunkFrame.SetTotal(frames[0], 64 * 1024 + 1); frames = frames.Take(1).ToList(); break;
            case "total below header minimum": ChunkFrame.SetTotal(frames[0], RebuildClientStream.MinKeyframeObjectBytes - 1); break;
            case "newer epoch mid assembly": ChunkFrame.SetEpoch(frames[1], 6); break;
            case "other sequence mid assembly": ChunkFrame.SetSequence(frames[1], key.Id.Sequence + 1); break;
            case "other stream": frames[0][ChunkFrame.StreamAt] = 1; break;
            case "inconsistent total": ChunkFrame.SetTotal(frames[1], ChunkFrame.Total(frames[1]) + 1); break;
            case "excessive chunk count": frames[0][ChunkFrame.CountAt]++; break;
            case "mismatched low sequence": ChunkFrame.SetLowSequence(frames[0], (ushort)(key.Id.Sequence + 1)); break;
            case "width of another offer": Assert.Equal(2, frames.Count); break;
            case "truncated final object": frames[last] = frames[last][..^1]; break;
        }
        int ingests = s.Client.RebuildDiagnosticsForTest.IngestCount;

        foreach (byte[] frame in frames) s.Send(frame);
        s.Pump(0f);

        Assert.Equal(DisconnectReason.IncompatibleVersion, s.Client.DisconnectReason);
        ClientDeltaRebuild rebuild = s.Client.DeltaRebuildForTest!;
        Assert.Equal(0, rebuild.PublicationCountForTest);
        Assert.Null(rebuild.LatestAcceptedId);
        Assert.Equal(ingests, s.Client.RebuildDiagnosticsForTest.IngestCount);
        Assert.Empty(s.Client.ViewForTest.Entities);
        Assert.Empty(s.Tap.Controls(RebuildControlKind.Acknowledge));
    }

    [Fact]
    public void PartialKeyframeNeverPublishesAndCompletesExactly()
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, 2000, 7);
        source.Start(5);
        s.Offer(5);
        s.Pump();
        ReplicationDeltaPacket key = source.Build(keyframe: true);
        List<byte[]> frames = source.ChunkFrames(key);
        Assert.True(frames.Count >= 3, "a keyframe of several chunks");

        for (int i = 0; i < frames.Count - 1; i++)
        {
            s.Send(frames[i]);
            s.Pump(0f);
            Assert.True(s.Client.RebuildStreamForTest!.AssemblyPending);
            Assert.Equal(0, s.Client.DeltaRebuildForTest!.PublicationCountForTest);
            Assert.Empty(s.Client.ViewForTest.Entities);
        }
        s.Send(frames[^1]);
        s.Pump(0f);

        Assert.False(s.Client.RebuildStreamForTest!.AssemblyPending);
        Assert.Equal(key.Id, s.Client.DeltaRebuildForTest!.LatestAcceptedId);
        Assert.True(s.Client.TryGetComponent(2, out PadState pad));
        Assert.Equal(new PadState { Length = 2000, Fill = 7 }, pad);
        (RebuildClientControl ack, NetChannelReliability reliability) = Assert.Single(s.Tap.Controls(RebuildControlKind.Acknowledge));
        Assert.Equal(new ReplicationPacketId(ack.Epoch, ack.SnapshotSequence), key.Id);
        Assert.Equal(ClientTap.Reliable, reliability);
    }

    [Fact]
    public void ReplacementWidthChunksAreValid()
    {
        var s = new ScriptedRebuildServer();
        var source = new RebuildSource();
        source.Spawn(2, 1400, 7);
        s.Offer(5);
        s.Pump();
        Assert.Equal(489, s.Client.RebuildStreamForTest!.ReassemblyWidth);
        s.Offer(6, packetCap: 400);
        s.Pump();
        Assert.Equal(377, s.Client.RebuildStreamForTest!.ReassemblyWidth);
        source.Start(6);
        ReplicationDeltaPacket key = source.Build(keyframe: true);

        foreach (byte[] frame in source.ChunkFrames(key, 400)) s.Send(frame);
        s.Pump(0f);

        Assert.Equal(WorldConnectionState.Connected, s.Client.ConnectionState);
        Assert.Equal(key.Id, s.Client.DeltaRebuildForTest!.LatestAcceptedId);
        Assert.Equal(new ReplicationSelection(ReplicationDeliveryMode.AcknowledgedUnreliable,
            ReplicationSelectionReason.Selected, 6), s.Client.ReplicationSelection);
    }
}

/// <summary>Field offsets and setters for a kind 5 server frame, kind byte included: [kind][epoch u64][seq u32]
/// [total u32][stream][low seq u16][index][count][bytes].</summary>
internal static class ChunkFrame
{
    public const int StreamAt = 17;
    public const int CountAt = 21;

    public static uint Total(byte[] frame) => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(13));
    public static void SetEpoch(byte[] frame, ulong epoch) => BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), epoch);
    public static void SetSequence(byte[] frame, uint sequence)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), sequence);
        SetLowSequence(frame, unchecked((ushort)sequence));
    }
    public static void SetTotal(byte[] frame, uint total) => BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(13), total);
    public static void SetLowSequence(byte[] frame, ushort low) => BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(18), low);
}

/// <summary>Field setters for a kind 4 server frame: [kind][localNetId i64][movementAck i32][format][flags]
/// [epoch u64][snapshot u32][baseline u32][sections].</summary>
internal static class DeltaFrame
{
    public static byte[] With(byte[] frame, uint? snapshot = null, uint? baseline = null, byte? flags = null,
        byte? format = null)
    {
        byte[] copy = (byte[])frame.Clone();
        if (format is byte v) copy[13] = v;
        if (flags is byte f) copy[14] = f;
        if (snapshot is uint s) BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(23), s);
        if (baseline is uint b) BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(27), b);
        return copy;
    }
}

/// <summary>
/// A client transport over an in-memory endpoint that records every Data payload the client sends with its channel,
/// records every server frame it receives, can drop chosen outgoing or incoming frames, rewrite the Hello, inject
/// server frames and report a client-side payload limit (0, unknown, by default).
/// </summary>
internal sealed class ClientTap : INetTransport
{
    public const NetChannelReliability Reliable = NetChannelReliability.ReliableOrdered;
    public const NetChannelReliability Unreliable = NetChannelReliability.UnreliableSequenced;

    private readonly INetTransport inner;
    private readonly Queue<NetEvent> ready = new();
    private NetConnectionId server = NetConnectionId.None;

    public ClientTap(INetTransport inner) => this.inner = inner;

    public int Limit { get; set; }
    public List<(byte[] Payload, NetChannelReliability Reliability)> Sent { get; } = new();
    public List<(byte[] Frame, NetChannelReliability Reliability)> Received { get; } = new();
    public Func<byte[], bool>? DropOutgoing { get; set; }
    public Func<byte[], byte[]>? RewriteHello { get; set; }

    public int LegacyFrames => Received.Count(r => IsLegacy(r.Frame));

    public int LegacyFramesAfterOffer
    {
        get
        {
            int offer = Received.FindIndex(r => r.Frame[0] == (byte)MoveProtocol.ServerFrameKind.ReplicationMode);
            return offer < 0 ? 0 : Received.Skip(offer + 1).Count(r => IsLegacy(r.Frame));
        }
    }

    public List<(RebuildClientControl Control, NetChannelReliability Reliability)> Controls(RebuildControlKind kind)
    {
        var controls = new List<(RebuildClientControl, NetChannelReliability)>();
        foreach ((byte[] payload, NetChannelReliability reliability) in Sent)
            if (RebuildProtocol.DecodeClientControl(payload, out RebuildClientControl control) == ControlReadResult.Valid
                && control.Kind == kind) controls.Add((control, reliability));
        return controls;
    }

    public void Inject(byte[] serverFrame, NetChannelReliability reliability) =>
        ready.Enqueue(new NetEvent(NetEventType.Data, server, SessionFrame.Write(SessionOpcode.Data, serverFrame), reliability));

    public void InjectReject(string token) => ready.Enqueue(new NetEvent(NetEventType.Data, server,
        SessionFrame.Write(SessionOpcode.Reject, Encoding.UTF8.GetBytes(token)), Reliable));

    public void Poll()
    {
        inner.Poll();
        while (inner.TryDequeueEvent(out NetEvent ev))
        {
            if (ev.Type == NetEventType.Connected) server = ev.Connection;
            if (ev.Type == NetEventType.Data && SessionFrame.ReadOpcode(ev.Data) == SessionOpcode.Data)
                Received.Add((SessionFrame.ReadBody(ev.Data), ev.Reliability));
            ready.Enqueue(ev);
        }
    }

    public bool TryDequeueEvent(out NetEvent ev) => ready.TryDequeue(out ev);

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        byte[] bytes = payload.ToArray();
        SessionOpcode opcode = SessionFrame.ReadOpcode(bytes);
        if (opcode == SessionOpcode.Hello && RewriteHello is not null) bytes = RewriteHello(bytes);
        if (opcode == SessionOpcode.Data)
        {
            byte[] body = SessionFrame.ReadBody(bytes);
            Sent.Add((body, reliability));
            if (DropOutgoing?.Invoke(body) == true) return;
        }
        inner.Send(target, bytes, reliability);
    }

    public int MaxUnfragmentedPayloadBytes(NetConnectionId connection, NetChannelReliability reliability) => Limit;
    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => inner.Disconnect(connection, reason);
    public void Dispose() => inner.Dispose();

    private static bool IsLegacy(byte[] frame) =>
        frame[0] is (byte)MoveProtocol.ServerFrameKind.Snapshot or (byte)MoveProtocol.ServerFrameKind.Delta;
}

/// <summary>A real host and a real <see cref="WorldClient"/> over a <see cref="ClientTap"/>, stepped together one
/// host tick at a time: host poll, host tick, client poll with the same elapsed time.</summary>
internal sealed class ClientRebuildRig
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    private ClientRebuildRig(RigTransport transport, RebuildHost host)
    {
        Transport = transport;
        Host = host;
    }

    public RigTransport Transport { get; }
    public RebuildHost Host { get; }
    public WorldClient Client { get; private set; } = null!;
    public List<ClientTap> Taps { get; } = new();
    public ClientTap Tap => Taps[^1];

    public static ClientRebuildRig Create(RebuildHostKind kind, bool serverOptIn = true, bool clientOptIn = true,
        int limit = 1400, ReplicationStreamOptions? clientStream = null, bool reconnecting = false,
        Action<ClientTap>? configureTap = null, Action<RebuildHost>? configureHost = null)
    {
        var transport = new RigTransport(limit);
        var rig = new ClientRebuildRig(transport,
            RebuildHost.Create(kind, transport, allowUnreliable: serverOptIn, registry: Pad.Registry()));
        configureHost?.Invoke(rig.Host);
        var config = new WorldClientConfig
        {
            TickSeconds = RebuildHost.Dt,
            RequestUnreliableDeltaReplication = clientOptIn,
            ReplicationStream = clientStream ?? new ReplicationStreamOptions(),
            RetryOnReject = false,   // a restart reject must reconnect through its own backoff rule
            Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 1f, MaxSeconds = 0.1f },
        };
        INetTransport Connect()
        {
            var tap = new ClientTap(transport.Hub.CreateClient());
            configureTap?.Invoke(tap);
            rig.Taps.Add(tap);
            return tap;
        }
        rig.Client = reconnecting
            ? new WorldClient(Connect, Flat, MoveTuning.Default, config, registry: Pad.Registry())
            : new WorldClient(Connect(), Flat, MoveTuning.Default, config, registry: Pad.Registry());
        return rig;
    }

    public void Step(float dt = RebuildHost.Dt)
    {
        Transport.Step++;
        Host.Poll();
        Host.Tick(dt);
        Client.Poll(dt);
    }

    public void Steps(int count)
    {
        for (int i = 0; i < count; i++) Step();
    }

    public long SpawnPad(int length, byte fill) => Host.SpawnEntity(RebuildHost.Spawn.X + 2f, RebuildHost.Spawn.Z + 2f,
        (world, entity) => world.Set(entity, new PadState { Length = length, Fill = fill }));

    public void SetPad(long netId, int length, byte fill)
    {
        Assert.True(Host.TryGetEntity(netId, out World world, out Entity entity), $"entity {netId} exists");
        world.Set(entity, new PadState { Length = length, Fill = fill });
    }
}

/// <summary>
/// A real <see cref="WorldClient"/> opted into format 2 against a plain session server the test drives by hand, so
/// every server frame is scripted: offers, chunks and deltas built by a <see cref="RebuildSource"/>, plus crafted
/// faults. Joined after construction. A reconnecting client builds later transports through a counted factory.
/// </summary>
internal sealed class ScriptedRebuildServer
{
    public const float Dt = 1f / 30f;
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    public ScriptedRebuildServer(ReplicationStreamOptions? stream = null, float disconnectTimeout = 3f,
        bool reconnecting = false)
    {
        Server = new NetServer(Hub.Server, 4, new AllowAllAuthenticator());
        Tap = new ClientTap(Hub.CreateClient());
        Stream = stream ?? new ReplicationStreamOptions();
        var config = new WorldClientConfig
        {
            TickSeconds = Dt,
            RequestUnreliableDeltaReplication = true,
            ReplicationStream = Stream,
            DisconnectTimeoutSeconds = disconnectTimeout,
            Reconnect = new ReconnectBackoff { InitialSeconds = Dt, Multiplier = 1f, MaxSeconds = Dt },
        };
        INetTransport Connect() => ++ConnectCount == 1 ? Tap : new ClientTap(Hub.CreateClient());
        Client = reconnecting
            ? new WorldClient(Connect, Flat, MoveTuning.Default, config, registry: Pad.Registry())
            : new WorldClient(Tap, Flat, MoveTuning.Default, config, registry: Pad.Registry());
        Pump(0f);
        Pump(0f);
        Pump(0f);
        Assert.True(Client.Joined);
    }

    public InMemoryTransportHub Hub { get; } = new();
    public NetServer Server { get; }
    public ClientTap Tap { get; }
    public WorldClient Client { get; }
    public ReplicationStreamOptions Stream { get; }
    public int Slot { get; private set; } = -1;
    public int LeftCount { get; private set; }
    public int ConnectCount { get; private set; }

    public void Pump(float dt = Dt)
    {
        Server.Poll();
        while (Server.TryDequeueEvent(out ServerSessionEvent ev))
        {
            if (ev.Kind == ServerSessionEventKind.Joined) Slot = ev.Slot;
            else if (ev.Kind == ServerSessionEventKind.Left) LeftCount++;
        }
        Client.Poll(dt);
    }

    public void Pumps(int count, float dt = Dt)
    {
        for (int i = 0; i < count; i++) Pump(dt);
    }

    public void Send(byte[] serverFrame, NetChannelReliability reliability = ClientTap.Reliable) =>
        Server.SendTo(Slot, serverFrame, reliability);

    public void Offer(ulong epoch, int packetCap = 512) => Send(RebuildProtocol.EncodeModeOffer(
        RebuildProtocol.SelectedOffer(Stream.StreamLimits(), packetCap, Stream.MaxChunksPerTick, epoch)));

    /// <summary>Offers <paramref name="epoch"/>, delivers the source's keyframe for it in one drain and acknowledges
    /// it on the source writer, so the next build is a delta.</summary>
    public ReplicationDeltaPacket Establish(RebuildSource source, ulong epoch, int packetCap = 512)
    {
        Offer(epoch, packetCap);
        Pump(0f);
        source.Start(epoch);
        ReplicationDeltaPacket key = source.Build(keyframe: true);
        foreach (byte[] frame in source.ChunkFrames(key, packetCap)) Send(frame);
        Pump(0f);
        Assert.Equal(key.Id, Client.DeltaRebuildForTest!.LatestAcceptedId);
        source.Ack(key.Id);
        return key;
    }

    public void Deliver(byte[] frame, NetChannelReliability reliability = ClientTap.Unreliable, float dt = 0f)
    {
        Send(frame, reliability);
        Pump(dt);
    }
}

/// <summary>
/// Real format 2 packets for scripted cases: a server world with padding entities and optional positions, one real
/// writer slot owned by net id 1, and the envelope and chunk framing NetWorld's server uses.
/// </summary>
internal sealed class RebuildSource
{
    public const long Owner = 1;
    private readonly AoiDeltaReplicator writer = new(Pad.Registry());
    private readonly Dictionary<long, Entity> entities = new();

    public World World { get; } = new();
    public HashSet<long> Interest { get; } = new();
    public long LocalNetId { get; set; } = Owner;
    public int MovementAck { get; set; }

    public void Spawn(long netId, int padLength, byte fill, Vector3? position = null)
    {
        Entity e = World.Spawn();
        World.Set(e, new NetId(netId));
        if (padLength >= 0) World.Set(e, new PadState { Length = padLength, Fill = fill });
        if (position is Vector3 p) World.Set(e, ReplicatedPosition.FromWorld(p, WorldFrame.Origin));
        entities[netId] = e;
        Interest.Add(netId);
    }

    public void SetPad(long netId, int length, byte fill) =>
        World.Set(entities[netId], new PadState { Length = length, Fill = fill });

    public void Move(long netId, Vector3 position) =>
        World.Set(entities[netId], ReplicatedPosition.FromWorld(position, WorldFrame.Origin));

    public void Start(ulong epoch, uint? nextSequence = null)
    {
        writer.StartRebuild(0, epoch, new ReplicationStreamOptions().StreamLimits());
        if (nextSequence is uint sequence) writer.SeedRebuildSequenceForTest(0, sequence);
    }

    public ReplicationDeltaPacket Build(bool keyframe = false)
    {
        writer.BeginTick();
        ReplicationDeltaPacket packet = writer.BuildRebuildFor(0, World, Interest, Owner, keyframe);
        writer.RecordRebuildSent(0, packet.Id);
        return packet;
    }

    public void Ack(ReplicationPacketId id) => writer.AcknowledgeRebuild(0, id);

    public ReplicationProjection Retained(ReplicationPacketId id)
    {
        Assert.True(writer.TryGetRetainedProjectionForTest(0, id, out ReplicationProjection projection));
        return projection;
    }

    public byte[] DeltaFrame(ReplicationDeltaPacket packet) => RebuildProtocol.EncodeDelta(LocalNetId, MovementAck, packet);

    public List<byte[]> ChunkFrames(ReplicationDeltaPacket packet, int packetCap = 512)
    {
        ReadOnlySpan<byte> body = packet.Bytes.Span;
        var keyframe = new byte[RebuildProtocol.EnvelopeBytes + body.Length];
        BinaryPrimitives.WriteInt64LittleEndian(keyframe, LocalNetId);
        BinaryPrimitives.WriteInt32LittleEndian(keyframe.AsSpan(sizeof(long)), MovementAck);
        body.CopyTo(keyframe.AsSpan(RebuildProtocol.EnvelopeBytes));
        byte[][] chunks = MessageFragmenter.Fragment(RebuildServerStream.KeyframeStreamId,
            unchecked((ushort)packet.Id.Sequence), keyframe, ReplicationStreamOptions.ChunkWidth(packetCap));
        return chunks.Select(c => RebuildProtocol.EncodeKeyframeChunk(packet.Id, (uint)keyframe.Length, c)).ToList();
    }
}
