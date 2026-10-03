using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

// Shared format 2 rig for the NetWorld replication tests: a padding component with a hand-written wire frame, a
// limit and fault injecting transport, an independent payload reader, a raw format 2 client and a stepped host rig.
/// <summary>One writer or stream row of the repair contract table.</summary>
public enum RepairTrigger
{
    NoAckWindow,
    SequenceAmbiguous,
    OversizeDelta,
    RepairRequest,
    LimitDropFeasible,
    LimitDropInfeasible,
    LimitUnknown,
    CapacityExceeded,
    RecoveryDeadline,
    SendRefused,
}

/// <summary>A replicated padding component of a chosen length and fill byte, so a case controls projection and delta
/// sizes exactly.</summary>
internal struct PadState : IComponent
{
    public int Length;
    public byte Fill;
}

/// <summary>The padding codec and its expected wire frame, written by hand rather than by the encoder.</summary>
internal static class Pad
{
    public const ushort TypeId = ReplicationRegistry.FirstExtensionTypeId + 9;

    public static ReplicationRegistry Registry() => MoveProtocol.CreateRegistry(r => r.Register<PadState>(TypeId,
        static (pad, writer) =>
        {
            for (int i = 0; i < pad.Length; i++) writer.Write(pad.Fill);
        },
        static reader =>
        {
            int length = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
            byte[] bytes = reader.ReadBytes(length);
            return new PadState { Length = length, Fill = length == 0 ? (byte)0 : bytes[0] };
        }));

    /// <summary>Frame bytes on the wire: type id, 7-bit length, payload.</summary>
    public static int FrameBytes(int length) => sizeof(ushort) + SevenBitBytes(length) + length;

    /// <summary>The exact frame a pad of <paramref name="length"/> bytes of <paramref name="fill"/> takes.</summary>
    public static byte[] Frame(int length, byte fill)
    {
        var frame = new byte[FrameBytes(length)];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, TypeId);
        int at = sizeof(ushort);
        for (uint v = (uint)length; ; v >>= 7)
        {
            if (v < 0x80) { frame[at++] = (byte)v; break; }
            frame[at++] = (byte)(v | 0x80);
        }
        frame.AsSpan(at).Fill(fill);
        return frame;
    }

    /// <summary>True when <paramref name="body"/> carries the exact pad frame.</summary>
    public static bool Contains(ReadOnlySpan<byte> body, int length, byte fill) =>
        body.IndexOf(Frame(length, fill)) >= 0;

    private static int SevenBitBytes(int value)
    {
        int bytes = 1;
        for (uint v = (uint)value; v >= 0x80; v >>= 7) bytes++;
        return bytes;
    }
}

/// <summary>One payload the server handed to the rig transport, session frame byte included, with its host tick.</summary>
internal readonly record struct RigSend(int Step, byte[] Payload, NetChannelReliability Reliability)
{
    // [session opcode][server frame kind][...]. A chunk continues [epoch u64][seq u32][total u32][stream][seq16]
    // [index][count][bytes]. A delta continues [localNetId i64][movementAck i32][format 2 body].
    private const int ChunkGeneric = 18;

    public bool IsData => Payload.Length > 1 && Payload[0] == (byte)SessionOpcode.Data;
    public MoveProtocol.ServerFrameKind Kind => (MoveProtocol.ServerFrameKind)Payload[1];
    public bool IsChunk => IsData && Kind == MoveProtocol.ServerFrameKind.RebuildKeyframeChunk;
    public bool IsDelta => IsData && Kind == MoveProtocol.ServerFrameKind.RebuildDelta;
    public bool IsOffer => IsData && Kind == MoveProtocol.ServerFrameKind.ReplicationMode;
    public bool IsLegacy => IsData && Kind is MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta;

    public ReplicationPacketId ChunkId => new(BinaryPrimitives.ReadUInt64LittleEndian(Payload.AsSpan(2)),
        BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(10)));
    public uint ChunkTotal => BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(14));
    public byte ChunkStreamId => Payload[ChunkGeneric];
    public ushort ChunkFragmentSequence => BinaryPrimitives.ReadUInt16LittleEndian(Payload.AsSpan(ChunkGeneric + 1));
    public int ChunkIndex => Payload[ChunkGeneric + 3];
    public int ChunkCount => Payload[ChunkGeneric + 4];
    public bool IsLastChunk => ChunkIndex == ChunkCount - 1;
    public byte[] DeltaBody => Payload[(2 + RebuildProtocol.EnvelopeBytes)..];
}

/// <summary>A server transport over the in-memory hub that answers one configured limit per channel, records every
/// payload with its host tick and can throw on a chosen send.</summary>
internal sealed class RigTransport : INetTransport
{
    private readonly INetTransport inner;

    public RigTransport(int limit)
    {
        Hub = new InMemoryTransportHub();
        inner = Hub.Server;
        Limit = limit;
    }

    public InMemoryTransportHub Hub { get; }
    public int UnreliableLimit { get; set; }
    public int ReliableLimit { get; set; }
    public int Limit { set { UnreliableLimit = value; ReliableLimit = value; } }
    public int Step { get; set; }
    public List<RigSend> Sends { get; } = new();

    /// <summary>Throws an <see cref="IOException"/> from the first send it matches, then clears itself.</summary>
    public Func<RigSend, bool>? ThrowWhen { get; set; }

    public int MaxUnfragmentedPayloadBytes(NetConnectionId connection, NetChannelReliability reliability) =>
        reliability == NetChannelReliability.ReliableOrdered ? ReliableLimit : UnreliableLimit;

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        var send = new RigSend(Step, payload.ToArray(), reliability);
        if (ThrowWhen is { } predicate && predicate(send))
        {
            ThrowWhen = null;
            throw new IOException("injected send fault");
        }
        Sends.Add(send);
        inner.Send(target, payload, reliability);
    }

    public void Poll() => inner.Poll();
    public bool TryDequeueEvent(out NetEvent ev) => inner.TryDequeueEvent(out ev);
    public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => inner.Disconnect(connection, reason);
    public void Dispose() => inner.Dispose();
}

internal readonly record struct RigKeyframe(int Step, ReplicationPacketId Id, long LocalNetId, int MovementAck,
    byte[] Body, int ObjectLength);

internal readonly record struct RigDelta(int Step, long LocalNetId, int MovementAck, byte[] Body)
{
    public RebuildWireHeader Header => RebuildWire.ReadHeader(Body);
    public ReplicationPacketId Id => new(Header.Epoch, Header.Snapshot);
}

/// <summary>
/// A raw format 2 client: joins, advertises both delta capabilities reliably, accepts mode 1 offers, reassembles
/// keyframe chunks and checks each object against its declared total, and acknowledges keyframes reliably and routine
/// deltas unreliably unless told not to. It never reconstructs state.
/// </summary>
internal sealed class V2Client
{
    private readonly NetClient net;
    private readonly bool requestRebuild;
    private readonly List<byte> assembly = new();
    private ReplicationPacketId? assemblingId;
    private uint assemblyTotal;

    public V2Client(INetTransport transport, bool requestRebuild)
    {
        net = new NetClient(transport, TestHandshake.Wire());
        this.requestRebuild = requestRebuild;
    }

    public bool AutoAccept { get; set; } = true;
    public bool AutoAckKeyframes { get; set; } = true;
    public bool AutoAckDeltas { get; set; } = true;
    public int Step { get; set; }
    public bool Joined { get; private set; }
    public int Slot => net.Slot;
    public string? RejectReason { get; private set; }
    public List<(int Step, ReplicationModeOffer Offer)> Offers { get; } = new();
    public List<RigKeyframe> Keyframes { get; } = new();
    public List<RigDelta> Deltas { get; } = new();
    public List<(int Step, MoveProtocol.ServerFrameKind Kind)> LegacyFrames { get; } = new();

    public int LegacyFramesAfterFirstOffer =>
        Offers.Count == 0 ? 0 : LegacyFrames.Count(f => f.Step > Offers[0].Step);

    public void Ack(ReplicationPacketId id, NetChannelReliability reliability) =>
        net.Send(RebuildProtocol.EncodeAck(id), reliability);

    public void Accept(ulong epoch) =>
        net.Send(RebuildProtocol.EncodeAcceptance(epoch), NetChannelReliability.ReliableOrdered);

    public void RequestRepair(ulong epoch, uint lastAccepted, uint missingBaseline) =>
        net.Send(RebuildProtocol.EncodeRepair(epoch, lastAccepted, missingBaseline), NetChannelReliability.ReliableOrdered);

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
                    net.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.DeltaCapable),
                        NetChannelReliability.ReliableOrdered);
                    if (requestRebuild)
                        net.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.RebuildDeltaCapable),
                            NetChannelReliability.ReliableOrdered);
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
        switch (kind)
        {
            case MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta:
                LegacyFrames.Add((Step, kind));
                break;
            case MoveProtocol.ServerFrameKind.ReplicationMode:
                Assert.True(RebuildProtocol.TryDecodeModeOffer(payload, out ReplicationModeOffer offer), "offer decodes");
                Offers.Add((Step, offer));
                if (AutoAccept && offer.Mode == ReplicationDeliveryMode.AcknowledgedUnreliable) Accept(offer.Epoch);
                break;
            case MoveProtocol.ServerFrameKind.RebuildKeyframeChunk:
                OnChunk(payload);
                break;
            case MoveProtocol.ServerFrameKind.RebuildDelta:
                var delta = new RigDelta(Step, BinaryPrimitives.ReadInt64LittleEndian(payload),
                    BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8)), payload[RebuildProtocol.EnvelopeBytes..]);
                Deltas.Add(delta);
                if (AutoAckDeltas) Ack(delta.Id, NetChannelReliability.UnreliableSequenced);
                break;
        }
    }

    private void OnChunk(byte[] payload)
    {
        var id = new ReplicationPacketId(BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8)));
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
        ReadOnlySpan<byte> chunk = payload.AsSpan(16);
        int index = chunk[3];
        int count = chunk[4];
        if (index == 0)
        {
            assemblingId = id;
            assemblyTotal = total;
            assembly.Clear();
        }
        else if (assemblingId != id)
        {
            return;
        }
        Assert.Equal(assemblyTotal, total);
        assembly.AddRange(chunk[MessageFragmenter.HeaderBytes..].ToArray());
        if (index != count - 1) return;
        byte[] whole = assembly.ToArray();
        Assert.Equal((int)total, whole.Length);
        assemblingId = null;
        Keyframes.Add(new RigKeyframe(Step, id, BinaryPrimitives.ReadInt64LittleEndian(whole),
            BinaryPrimitives.ReadInt32LittleEndian(whole.AsSpan(8)), whole[RebuildProtocol.EnvelopeBytes..],
            whole.Length));
        if (AutoAckKeyframes) Ack(id, NetChannelReliability.ReliableOrdered);
    }
}

/// <summary>A host, its rig transport and raw format 2 clients, stepped together one host tick at a time with the
/// configured tick length, so every step is exactly one cadence tick unless a case passes a shorter frame.</summary>
internal sealed class V2Rig
{
    private V2Rig(RigTransport transport, RebuildHost host)
    {
        Transport = transport;
        Host = host;
    }

    public RigTransport Transport { get; }
    public RebuildHost Host { get; }
    public List<V2Client> Clients { get; } = new();
    public int StepIndex { get; private set; }
    public AoiDeltaReplicator Writer => Host.Writer!;

    public static V2Rig Create(RebuildHostKind kind, int limit = 1400, ReplicationStreamOptions? stream = null,
        Func<int, long, bool>? visible = null)
    {
        var transport = new RigTransport(limit);
        return new V2Rig(transport, RebuildHost.Create(kind, transport, allowUnreliable: true, visible: visible,
            stream: stream, registry: Pad.Registry()));
    }

    public V2Client Connect(bool requestRebuild = true)
    {
        var client = new V2Client(Transport.Hub.CreateClient(), requestRebuild);
        Clients.Add(client);
        return client;
    }

    /// <summary>Connects one client and runs it into steady serving on its exact schedule: connect, join and the
    /// answered capability take three ticks, the keyframe takes <paramref name="keyframeTicks"/>, and the first routine
    /// delta follows its reliable acknowledgement one tick later.</summary>
    public V2Client JoinSteady(int keyframeTicks = 1)
    {
        V2Client client = Connect();
        Steps(3);
        (_, ReplicationModeOffer offer) = Assert.Single(client.Offers);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, offer.Mode);
        Steps(keyframeTicks);
        Assert.Single(client.Keyframes);
        Assert.Empty(client.Deltas);
        Step();
        Assert.Single(client.Deltas);
        return client;
    }

    public List<RigSend> Step(float dt = RebuildHost.Dt)
    {
        StepIndex++;
        Transport.Step = StepIndex;
        foreach (V2Client client in Clients) client.Step = StepIndex;
        int before = Transport.Sends.Count;
        Host.Poll();
        Host.Tick(dt);
        foreach (V2Client client in Clients) client.Poll();
        return Transport.Sends.GetRange(before, Transport.Sends.Count - before);
    }

    public List<RigSend> Steps(int count)
    {
        var sends = new List<RigSend>();
        for (int i = 0; i < count; i++) sends.AddRange(Step());
        return sends;
    }

    public long SpawnPad(int length, byte fill, float dx = 2f, float dz = 2f) =>
        Host.SpawnEntity(RebuildHost.Spawn.X + dx, RebuildHost.Spawn.Z + dz,
            (world, entity) => world.Set(entity, new PadState { Length = length, Fill = fill }));

    public void SetPad(long netId, int length, byte fill)
    {
        Assert.True(Host.TryGetEntity(netId, out World world, out Entity entity), $"entity {netId} exists");
        world.Set(entity, new PadState { Length = length, Fill = fill });
    }

    public RebuildServerStream Stream(V2Client client)
    {
        Assert.True(Host.TryGetRebuildStream(client.Slot, out RebuildServerStream stream), "the slot has a stream");
        return stream;
    }
}
