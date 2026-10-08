using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>The capacity outcome of one characterized viewer projection.</summary>
internal enum CapacityDecision
{
    /// <summary>The changing-state datagram fits the packet cap and goes out unreliably.</summary>
    FitsDelta,

    /// <summary>The complete keyframe fits but the changing-state datagram does not, so the stream starts a reliable
    /// keyframe repair instead of sending it.</summary>
    OversizeDeltaKeyframe,

    /// <summary>The complete projection cannot fit an entity, frame, keyframe or byte limit, a typed terminal
    /// failure.</summary>
    CapacityExceeded,
}

/// <summary>Every size recorded for one row. Compared as a whole, independent against actual.</summary>
/// <param name="Entities">Entities in the viewer projection.</param>
/// <param name="Frames">Component frames in the viewer projection, tags included.</param>
/// <param name="PayloadBytes">Component payload bytes in the viewer projection, frame headers excluded.</param>
/// <param name="KeyframeObjectBytes">The complete keyframe object: 12-byte envelope plus the format 2 body.</param>
/// <param name="KeyframeChunks">Reliable chunks of that object at the 512-byte cap's 489-byte width.</param>
/// <param name="KeyframeTicks">Replication ticks those chunks take at four per tick.</param>
/// <param name="ChangingDatagramBytes">The transport payload of the first routine delta after every actor moved,
/// session byte included.</param>
/// <param name="RetainedProjections">Writer projections retained once 31 committed sends went unacknowledged.</param>
/// <param name="RetainedBytes">Distinct reachable backing bytes of those projections.</param>
internal readonly record struct EncodedSizes(
    int Entities,
    int Frames,
    int PayloadBytes,
    int KeyframeObjectBytes,
    int KeyframeChunks,
    int KeyframeTicks,
    int ChangingDatagramBytes,
    int RetainedProjections,
    int RetainedBytes);

/// <summary>One characterized row of the fixed table.</summary>
internal sealed record CharacterizedRow(
    string Name,
    int VisibleEntities,
    EncodedSizes IndependentEncodedBytes,
    EncodedSizes ActualEncodedBytes,
    CapacityDecision IndependentCapacityDecision,
    CapacityDecision ActualCapacityDecision);

/// <summary>One metadata-only tag boundary row. A refused keyframe has no actual object size.</summary>
internal sealed record BoundaryRow(
    string Name,
    int Entities,
    int Frames,
    int IndependentKeyframeObjectBytes,
    int? ActualKeyframeObjectBytes,
    CapacityDecision IndependentCapacityDecision,
    CapacityDecision ActualCapacityDecision,
    CapacityDecision WriterCapacityDecision);

/// <summary>
/// Bounded consumer size characterization for format 2 delta replication. Four fixed viewer projections of 1, 32, 128
/// and 256 visible entities are serialized through NetWorld's real built-in codecs plus opaque consumer extension
/// payloads of the widths the consumer source declares today. An independent length calculation from that manifest
/// must equal the real encoder, and its capacity decision must equal the decision the real writer and stream make.
/// It characterizes the proposed NetWorld adaptation of the consumer's current extension bytes. It is not a benchmark,
/// a production capture, a randomized AOI sweep or an approved entity count.
/// </summary>
public sealed class DeltaConsumerSizeCharacterizationTests
{
    private readonly ITestOutputHelper output;

    public DeltaConsumerSizeCharacterizationTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void ConsumerProjectionSizeCharacterization()
    {
        IReadOnlyList<CharacterizedRow> characterizedRows = SizeCharacterization.CharacterizeRows();
        IReadOnlyList<BoundaryRow> boundaryRows = SizeCharacterization.CharacterizeTagBoundary();
        SizeCharacterization.Write(output, characterizedRows, boundaryRows);

        Assert.Equal(4, characterizedRows.Count);
        Assert.All(characterizedRows, row => Assert.Equal(row.IndependentEncodedBytes, row.ActualEncodedBytes));
        Assert.All(characterizedRows, row => Assert.Equal(row.IndependentCapacityDecision, row.ActualCapacityDecision));
        Assert.Equal(new[] { 1, 32, 128, 256 }, characterizedRows.Select(r => r.VisibleEntities));
        Assert.Equal(new[] { "owner-only", "party-32", "crowd-128", "town-256" }, characterizedRows.Select(r => r.Name));

        Assert.Equal(2, boundaryRows.Count);
        Assert.All(boundaryRows, row => Assert.Equal(row.IndependentCapacityDecision, row.ActualCapacityDecision));
        Assert.All(boundaryRows, row => Assert.Equal(row.ActualCapacityDecision, row.WriterCapacityDecision));
        Assert.Equal(new[] { 16_384, 16_385 }, boundaryRows.Select(r => r.Frames));
        Assert.Equal(boundaryRows[0].IndependentKeyframeObjectBytes, boundaryRows[0].ActualKeyframeObjectBytes);
        Assert.Equal(CapacityDecision.FitsDelta, boundaryRows[0].ActualCapacityDecision);
        Assert.Null(boundaryRows[1].ActualKeyframeObjectBytes);
        Assert.Equal(CapacityDecision.CapacityExceeded, boundaryRows[1].ActualCapacityDecision);
    }
}


/// <summary>An opaque consumer extension payload of a fixed width, one closed type per consumer component.</summary>
internal struct OpaqueExtension<TKind> : IComponent where TKind : struct
{
    public byte Seed;
}

internal struct MonsterKindWire { }
internal struct ActorActionWire { }
internal struct ActorWornWire { }
internal struct ActorFanfareWire { }
internal struct ActorVitalEffectWire { }
internal struct DurableLootSourceWire { }
internal struct CarcassStateWire { }

// Synthetic zero-byte tags for the metadata boundary, outside the consumer's id range.
internal struct MetaTag00 : IComponent { }
internal struct MetaTag01 : IComponent { }
internal struct MetaTag02 : IComponent { }
internal struct MetaTag03 : IComponent { }
internal struct MetaTag04 : IComponent { }
internal struct MetaTag05 : IComponent { }
internal struct MetaTag06 : IComponent { }
internal struct MetaTag07 : IComponent { }
internal struct MetaTag08 : IComponent { }
internal struct MetaTag09 : IComponent { }
internal struct MetaTag10 : IComponent { }
internal struct MetaTag11 : IComponent { }
internal struct MetaTag12 : IComponent { }
internal struct MetaTag13 : IComponent { }
internal struct MetaTag14 : IComponent { }
internal struct MetaTag15 : IComponent { }
internal struct MetaTag16 : IComponent { }

/// <summary>One consumer extension of the source manifest: its id, its width and how the fixture sets it.</summary>
internal sealed record ConsumerExtension(string Name, ushort TypeId, int PayloadBytes,
    Action<ReplicationRegistry> Register, Action<World, Entity, byte> Set);

/// <summary>One synthetic zero-byte tag type.</summary>
internal sealed record TagKind(Action<ReplicationRegistry, ushort> Register, Action<World, Entity> Set)
{
    public static TagKind Of<T>() where T : struct, IComponent => new(
        static (registry, typeId) => registry.Register<T>(typeId, static (_, _) => { }, static _ => default),
        static (world, entity) => world.Set(entity, default(T)));
}

/// <summary>What one viewer is shown in a characterized row.</summary>
internal enum ActorKind
{
    Owner,
    Player,
    Monster,
    Carcass,
}

/// <summary>The fixture, the independent calculation and the real-path observation behind the characterization.</summary>
internal static class SizeCharacterization
{
    // Approved format 2 caps, restated so the independent decision never reads the options it is checked against.
    internal const int ApprovedEntities = 1024;
    internal const int ApprovedFrames = 16_384;
    internal const int ApprovedKeyframeBytes = 64 * 1024;
    internal const int ApprovedRetainedBytes = 2 * 1024 * 1024;
    internal const int ApprovedPacketCap = 512;
    internal const int ApprovedChunksPerTick = 4;
    internal const int ApprovedNoAckWindow = 31;
    internal const int ApprovedRetainedProjections = 32;

    // Format 2 layout from the documented wire, never from the encoder.
    private const int SessionByte = 1;
    private const int KindByte = 1;
    private const int Envelope = 8 + 4;                       // local net id, movement ack
    private const int BodyHeader = 1 + 1 + 8 + 4 + 4;         // format, flags, epoch, snapshot, baseline
    private const int Counts = 4 + 4;                         // removed entity count, changed entity count
    private const int Entry = 8 + 1 + 4 + 2;                  // net id, entry flag, removed component count, terminator
    private const int TypeIdBytes = 2;
    private const int ChunkOverhead = 1 + 1 + 8 + 4 + 4 + 5;  // session, kind, epoch, sequence, total, fragment header
    private const int Slot = 0;

    // NetWorld built-in payloads, rechecked against MoveProtocol.CreateRegistry: the position frame stamp is two shorts
    // then three floats, MovementState is 18 movement bytes, a 38-byte commitment and one excursion byte. PlayerIdentity is a ushort
    // length then UTF-8, and MovementOwnerState is two floats on the owner's wire only.
    internal const int PositionPayload = 2 + 2 + 4 + 4 + 4;
    internal const int MovementPayload = (4 + 1 + 1 + 4 + 1 + 1 + 2 + 2 + 2) + (4 + 1 + 8 + 4 + 4 + 4 + 4 + 4 + 4 + 1) + 1;
    internal const int OwnerStatePayload = 4 + 4;
    internal const int NameLengthPrefix = 2;

    /// <summary>A 32-byte UTF-8 name with two 2-byte letters, the longest name this fixture uses.</summary>
    internal const string PlayerName = "Ærin Þorvald of the Grimhollow";

    // The consumer's replicated extensions, rechecked field by field against Grimhollow.Shared/
    // GrimhollowProtocol.Components.cs as last changed in Grimhollow a4557c8c. Ids follow the consumer from
    // TileProtocol.FirstGameTypeId. Only the widths are copied, the payloads are opaque bytes.
    internal static readonly ConsumerExtension MonsterKind = Extension<MonsterKindWire>("MonsterKind", 0, 1);
    internal static readonly ConsumerExtension ActorAction =
        Extension<ActorActionWire>("ActorAction", 1, 1 + 1 + 4 + 4 + 8 + 8 + 4);
    internal static readonly ConsumerExtension ActorWorn = Extension<ActorWornWire>("ActorWorn", 2, 4 + 4 + 4);
    internal static readonly ConsumerExtension ActorFanfare = Extension<ActorFanfareWire>("ActorFanfare", 3, 1 + 1);
    internal static readonly ConsumerExtension ActorVitalEffect =
        Extension<ActorVitalEffectWire>("ActorVitalEffect", 4, 1 + 2 + 1);
    internal static readonly ConsumerExtension DurableLootSource =
        Extension<DurableLootSourceWire>("DurableLootSource", 5, 16);
    internal static readonly ConsumerExtension CarcassState =
        Extension<CarcassStateWire>("CarcassState", 6, 16 + 1 + 1 + 8 + 8 + 8 + 8);

    internal static readonly ConsumerExtension[] Manifest =
        { MonsterKind, ActorAction, ActorWorn, ActorFanfare, ActorVitalEffect, DurableLootSource, CarcassState };

    // The full actor mix every player and monster carries, and the full carcass mix.
    private static readonly ConsumerExtension[] ActorMix = { ActorAction, ActorWorn, ActorFanfare, ActorVitalEffect };
    private static readonly ConsumerExtension[] CarcassMix = { CarcassState, DurableLootSource };

    private static readonly ushort TagFirstTypeId = (ushort)(TileProtocol.FirstGameTypeId + 32);
    private const int TagsPerEntity = 16;

    private static readonly TagKind[] Tags =
    {
        TagKind.Of<MetaTag00>(), TagKind.Of<MetaTag01>(), TagKind.Of<MetaTag02>(), TagKind.Of<MetaTag03>(),
        TagKind.Of<MetaTag04>(), TagKind.Of<MetaTag05>(), TagKind.Of<MetaTag06>(), TagKind.Of<MetaTag07>(),
        TagKind.Of<MetaTag08>(), TagKind.Of<MetaTag09>(), TagKind.Of<MetaTag10>(), TagKind.Of<MetaTag11>(),
        TagKind.Of<MetaTag12>(), TagKind.Of<MetaTag13>(), TagKind.Of<MetaTag14>(), TagKind.Of<MetaTag15>(),
        TagKind.Of<MetaTag16>(),
    };

    private static readonly (string Name, int Visible)[] Rows =
        { ("owner-only", 1), ("party-32", 32), ("crowd-128", 128), ("town-256", 256) };

    internal static IReadOnlyList<CharacterizedRow> CharacterizeRows()
    {
        RecheckSourceWidths();
        var rows = new List<CharacterizedRow>();
        foreach ((string name, int visible) in Rows)
        {
            EncodedSizes independent = Independent(visible);
            CapacityDecision independentDecision = Decide(independent.Entities, independent.Frames,
                independent.PayloadBytes, independent.KeyframeObjectBytes, independent.ChangingDatagramBytes);

            WriterMeasure writer = MeasureWriter(ViewerFixture.Actors(visible));
            StreamResult stream = ObserveStream(ViewerFixture.Actors(visible), moveAfterKeyframe: true);
            Assert.Equal(writer.KeyframeObjectBytes, stream.KeyframeObjectBytes);
            if (stream.DatagramBytes is int sent) Assert.Equal(writer.DatagramBytes, sent);

            var actual = new EncodedSizes(writer.Entities, writer.Frames, writer.PayloadBytes,
                writer.KeyframeObjectBytes, stream.KeyframeChunks, stream.KeyframeTicks, writer.DatagramBytes,
                writer.RetainedProjections, writer.RetainedBytes);
            rows.Add(new CharacterizedRow(name, visible, independent, actual, independentDecision, stream.Decision));
        }
        return rows;
    }

    internal static IReadOnlyList<BoundaryRow> CharacterizeTagBoundary()
    {
        var rows = new List<BoundaryRow>();
        foreach ((string name, bool oneOver) in new[] { ("tags-at-cap", false), ("tags-one-over", true) })
        {
            int frames = ApprovedEntities * TagsPerEntity + (oneOver ? 1 : 0);
            int keyframe = Envelope + BodyHeader + Counts + ApprovedEntities * Entry + frames * FrameBytes(true, 0);
            int emptyDatagram = SessionByte + KindByte + Envelope + BodyHeader + Counts;
            CapacityDecision independent = Decide(ApprovedEntities, frames, 0, keyframe, emptyDatagram);

            CapacityDecision writer = WriterDecision(ViewerFixture.TagBoundary(oneOver));
            StreamResult stream = ObserveStream(ViewerFixture.TagBoundary(oneOver), moveAfterKeyframe: false);
            rows.Add(new BoundaryRow(name, ApprovedEntities, frames, keyframe, stream.KeyframeObjectBytes, independent,
                stream.Decision, writer));
        }
        return rows;
    }

    internal static void Write(ITestOutputHelper output, IReadOnlyList<CharacterizedRow> rows,
        IReadOnlyList<BoundaryRow> boundary)
    {
        output.WriteLine($"Caps: packet {ApprovedPacketCap} B, keyframe {ApprovedKeyframeBytes} B, entities "
            + $"{ApprovedEntities}, frames {ApprovedFrames}, retained {ApprovedRetainedBytes} B, no-ack window "
            + $"{ApprovedNoAckWindow}, {ApprovedChunksPerTick} chunks per tick of {ApprovedPacketCap - ChunkOverhead} B.");
        output.WriteLine("| Row | Visible | Frames | Payload B | Keyframe object B | Chunks | Chunk ticks | "
            + "Changing datagram B | Retained projections | Retained B | Independent | Actual |");
        output.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |");
        foreach (CharacterizedRow row in rows)
        {
            EncodedSizes a = row.ActualEncodedBytes;
            string match = a == row.IndependentEncodedBytes ? string.Empty : " (independent differs)";
            output.WriteLine($"| {row.Name} | {row.VisibleEntities} | {a.Frames} | {a.PayloadBytes} | "
                + $"{a.KeyframeObjectBytes} | {a.KeyframeChunks} | {a.KeyframeTicks} | {a.ChangingDatagramBytes} | "
                + $"{a.RetainedProjections} | {a.RetainedBytes}{match} | {row.IndependentCapacityDecision} | "
                + $"{row.ActualCapacityDecision} |");
        }
        output.WriteLine("| Boundary | Entities | Frames | Keyframe object B (independent) | Keyframe object B (stream) | "
            + "Independent | Stream | Writer |");
        output.WriteLine("| --- | ---: | ---: | ---: | ---: | --- | --- | --- |");
        foreach (BoundaryRow row in boundary)
            output.WriteLine($"| {row.Name} | {row.Entities} | {row.Frames} | {row.IndependentKeyframeObjectBytes} | "
                + $"{row.ActualKeyframeObjectBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "refused"} | "
                + $"{row.IndependentCapacityDecision} | {row.ActualCapacityDecision} | {row.WriterCapacityDecision} |");
    }

    internal static ReplicationRegistry Registry() => MoveProtocol.CreateRegistry(registry =>
    {
        foreach (ConsumerExtension extension in Manifest) extension.Register(registry);
        for (int i = 0; i < Tags.Length; i++) Tags[i].Register(registry, (ushort)(TagFirstTypeId + i));
    });

    // The independent calculation: counts, payloads and wire lengths from the manifest widths and the documented layout.
    internal static EncodedSizes Independent(int visible)
    {
        int nameBytes = Encoding.UTF8.GetByteCount(PlayerName);
        int frames = 0, payload = 0;
        int keyframe = Envelope + BodyHeader + Counts;
        int datagram = SessionByte + KindByte + Envelope + BodyHeader + Counts;
        for (int i = 0; i < visible; i++)
        {
            ActorKind kind = KindOf(i);
            keyframe += Entry;
            foreach ((bool extension, int bytes) in VisibleFrames(kind, nameBytes))
            {
                frames++;
                payload += bytes;
                keyframe += FrameBytes(extension, bytes);
            }
            if (kind != ActorKind.Carcass)
                datagram += Entry + FrameBytes(false, PositionPayload) + FrameBytes(false, MovementPayload);
        }
        int chunks = Ceiling(keyframe, ApprovedPacketCap - ChunkOverhead);
        int retained = ApprovedNoAckWindow + 1;
        return new EncodedSizes(visible, frames, payload, keyframe, chunks, Ceiling(chunks, ApprovedChunksPerTick),
            datagram, retained, retained * payload);
    }

    // Capacity first, because no repair can fix it. Then the routine datagram against the packet cap.
    internal static CapacityDecision Decide(int entities, int frames, long payload, long keyframe, int datagram) =>
        entities > ApprovedEntities || frames > ApprovedFrames || keyframe > ApprovedKeyframeBytes
            || payload > ApprovedRetainedBytes
            ? CapacityDecision.CapacityExceeded
            : datagram > ApprovedPacketCap ? CapacityDecision.OversizeDeltaKeyframe : CapacityDecision.FitsDelta;

    // The owner is the first entity. Remote entities cycle player, monster, carcass.
    internal static ActorKind KindOf(int index) => index == 0
        ? ActorKind.Owner
        : (index % 3) switch { 1 => ActorKind.Player, 2 => ActorKind.Monster, _ => ActorKind.Carcass };

    private static IEnumerable<(bool Extension, int Payload)> VisibleFrames(ActorKind kind, int nameBytes)
    {
        yield return (false, PositionPayload);
        if (kind != ActorKind.Carcass) yield return (false, MovementPayload);
        if (kind is ActorKind.Owner or ActorKind.Player) yield return (false, NameLengthPrefix + nameBytes);
        if (kind == ActorKind.Owner) yield return (false, OwnerStatePayload);
        if (kind == ActorKind.Monster) yield return (true, MonsterKind.PayloadBytes);
        foreach (ConsumerExtension extension in kind == ActorKind.Carcass ? CarcassMix : ActorMix)
            yield return (true, extension.PayloadBytes);
    }

    private static int FrameBytes(bool extension, int payload) =>
        TypeIdBytes + (extension ? SevenBitBytes(payload) : 0) + payload;

    private static int SevenBitBytes(int value)
    {
        int bytes = 1;
        for (uint v = (uint)value; v >= 0x80; v >>= 7) bytes++;
        return bytes;
    }

    private static int Ceiling(int value, int divisor) => (value + divisor - 1) / divisor;

    // Fails before any row is serialized when a copied width or the name framing drifts.
    private static void RecheckSourceWidths()
    {
        Assert.Equal(new[] { 1, 30, 12, 2, 4, 16, 50 }, Manifest.Select(m => m.PayloadBytes));
        Assert.Equal(Enumerable.Range(TileProtocol.FirstGameTypeId, 7).Select(i => (ushort)i), Manifest.Select(m => m.TypeId));
        Assert.Equal(16, PositionPayload);
        Assert.Equal(57, MovementPayload);
        Assert.Equal(32, Encoding.UTF8.GetByteCount(PlayerName));
        Assert.True(Encoding.UTF8.GetByteCount(PlayerName) <= MoveProtocol.MaxDisplayNameBytes);
        var defaults = new DeltaRebuildOptions();
        Assert.Equal((ApprovedEntities, ApprovedFrames, ApprovedKeyframeBytes, ApprovedRetainedBytes),
            (defaults.MaxEntities, defaults.MaxComponents, defaults.MaxKeyframeBytes, defaults.MaxRetainedPayloadBytes));
        Assert.Equal((ApprovedNoAckWindow, ApprovedRetainedProjections),
            (defaults.NoAckSendWindow, defaults.MaxRetainedProjections));
        var stream = new ReplicationStreamOptions();
        Assert.Equal((ApprovedPacketCap, ApprovedChunksPerTick), (stream.MaxTransportPayloadBytes, stream.MaxChunksPerTick));
    }

    private static ConsumerExtension Extension<TKind>(string name, int index, int payloadBytes) where TKind : struct
    {
        ushort typeId = (ushort)(TileProtocol.FirstGameTypeId + index);
        return new ConsumerExtension(name, typeId, payloadBytes,
            registry => registry.Register<OpaqueExtension<TKind>>(typeId,
                (value, writer) =>
                {
                    for (int i = 0; i < payloadBytes; i++) writer.Write(unchecked((byte)(value.Seed + i)));
                },
                reader =>
                {
                    byte[] bytes = reader.ReadBytes(payloadBytes);
                    if (bytes.Length != payloadBytes) throw new InvalidDataException($"{name} is truncated.");
                    return new OpaqueExtension<TKind> { Seed = bytes[0] };
                },
                channels: ReplicationChannels.Replicate | ReplicationChannels.Migrate),
            (world, entity, seed) => world.Set(entity, new OpaqueExtension<TKind> { Seed = seed }));
    }

    private readonly record struct WriterMeasure(int Entities, int Frames, int PayloadBytes, int KeyframeObjectBytes,
        int DatagramBytes, int RetainedProjections, int RetainedBytes);

    private readonly record struct StreamResult(CapacityDecision Decision, int? KeyframeObjectBytes, int KeyframeChunks,
        int KeyframeTicks, int? DatagramBytes);

    // The real writer and encoder: the keyframe, the first routine delta after every actor moved, and the retention left
    // once the no-ack window of committed sends went unacknowledged.
    private static WriterMeasure MeasureWriter(ViewerFixture fixture)
    {
        var writer = new AoiDeltaReplicator(Registry());
        writer.StartRebuild(Slot, 1, new ReplicationStreamOptions().StreamLimits());
        writer.BeginTick();
        ReplicationDeltaPacket keyframe = writer.BuildRebuildFor(Slot, fixture.World, fixture.Interest,
            fixture.OwnerNetId, keyframe: true);
        Assert.True(writer.TryGetRetainedProjectionForTest(Slot, keyframe.Id, out ReplicationProjection projection));
        writer.RecordRebuildSent(Slot, keyframe.Id);
        writer.AcknowledgeRebuild(Slot, keyframe.Id);

        int datagram = 0;
        for (int send = 1; send <= ApprovedNoAckWindow; send++)
        {
            Assert.False(writer.RebuildNeedsRepair(Slot));
            fixture.Move(send);
            writer.BeginTick();
            ReplicationDeltaPacket delta = writer.BuildRebuildFor(Slot, fixture.World, fixture.Interest,
                fixture.OwnerNetId);
            if (send == 1) datagram = SessionByte + RebuildProtocol.EncodeDelta(fixture.OwnerNetId, 0, delta).Length;
            writer.RecordRebuildSent(Slot, delta.Id);
        }
        Assert.True(writer.RebuildNeedsRepair(Slot));
        RebuildUsage usage = writer.RebuildUsageForTest(Slot);
        return new WriterMeasure(projection.EntityCount, projection.ComponentCount, (int)projection.BackingBytes,
            RebuildProtocol.EnvelopeBytes + keyframe.Bytes.Length, datagram, usage.RetainedCount, usage.RetainedBytes);
    }

    // The real writer's own verdict for an unchanging projection: refused, or its empty delta against the cap.
    private static CapacityDecision WriterDecision(ViewerFixture fixture)
    {
        var writer = new AoiDeltaReplicator(Registry());
        writer.StartRebuild(Slot, 1, new ReplicationStreamOptions().StreamLimits());
        writer.BeginTick();
        ReplicationDeltaPacket keyframe;
        try
        {
            keyframe = writer.BuildRebuildFor(Slot, fixture.World, fixture.Interest, fixture.OwnerNetId, keyframe: true);
        }
        catch (DeltaRebuildException e) when (e.Failure == DeltaRebuildFailure.CapacityExceeded)
        {
            return CapacityDecision.CapacityExceeded;
        }
        writer.RecordRebuildSent(Slot, keyframe.Id);
        writer.AcknowledgeRebuild(Slot, keyframe.Id);
        writer.BeginTick();
        ReplicationDeltaPacket delta = writer.BuildRebuildFor(Slot, fixture.World, fixture.Interest, fixture.OwnerNetId);
        int datagram = SessionByte + RebuildProtocol.EncodeDelta(fixture.OwnerNetId, 0, delta).Length;
        return datagram > ApprovedPacketCap ? CapacityDecision.OversizeDeltaKeyframe : CapacityDecision.FitsDelta;
    }

    // The real stream over a session server at the approved cap: negotiate, freeze and send the keyframe at four chunks
    // per tick, acknowledge it exactly, then serve one routine tick and read what the stream chose to send.
    private static StreamResult ObserveStream(ViewerFixture fixture, bool moveAfterKeyframe)
    {
        var transport = new RigTransport(ApprovedPacketCap);
        var net = new NetServer(transport, 4, new AllowAllAuthenticator());
        var client = new NetClient(transport.Hub.CreateClient(), TestHandshake.Wire());
        for (int i = 0; i < 3; i++) Pump(net, client);
        Assert.True(client.Slot >= 0, "the client holds a slot");
        var writer = new AoiDeltaReplicator(Registry());
        var options = new ReplicationStreamOptions();
        var stream = new RebuildServerStream(net, client.Slot, writer, options, options.StreamLimits(),
            new ReplicationEpochAllocator());

        stream.OnRebuildCapability(0);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, stream.Selection.Mode);
        Assert.Equal(ApprovedPacketCap, stream.PacketCap);
        ulong epoch = stream.Selection.Epoch;
        stream.HandleControl(new RebuildClientControl(RebuildControlKind.Accept, epoch, 0, 0),
            NetChannelReliability.ReliableOrdered, 0);

        long tick = 0;
        var chunks = new List<RigSend>();
        int chunkTicks = 0;
        while (chunks.Count == 0 || !chunks[^1].IsLastChunk)
        {
            List<RigSend> sends = Serve(++tick);
            if (stream.Failure is ReplicationFailure failure)
            {
                Assert.Equal(DisconnectReason.ReplicationCapacityExceeded, failure.Reason);
                Assert.Empty(chunks);
                Assert.DoesNotContain(sends, s => s.IsChunk || s.IsDelta);
                return new StreamResult(CapacityDecision.CapacityExceeded, null, 0, 0, null);
            }
            List<RigSend> sent = sends.Where(s => s.IsChunk).ToList();
            Assert.InRange(sent.Count, 1, ApprovedChunksPerTick);
            chunks.AddRange(sent);
            chunkTicks++;
        }
        ReplicationPacketId keyframeId = chunks[0].ChunkId;
        Assert.All(chunks, c => Assert.Equal(keyframeId, c.ChunkId));
        Assert.All(chunks, c => Assert.True(c.Payload.Length <= ApprovedPacketCap));
        int keyframeObject = (int)chunks[0].ChunkTotal;
        stream.HandleControl(new RebuildClientControl(RebuildControlKind.Acknowledge, keyframeId.Epoch,
            keyframeId.Sequence, 0), NetChannelReliability.ReliableOrdered, tick);

        if (moveAfterKeyframe) fixture.Move(1);
        List<RigSend> routine = Serve(++tick);
        Assert.Null(stream.Failure);
        List<RigSend> deltas = routine.Where(s => s.IsDelta).ToList();
        if (deltas.Count == 1)
        {
            Assert.DoesNotContain(routine, s => s.IsChunk);
            Assert.Equal(NetChannelReliability.UnreliableSequenced, deltas[0].Reliability);
            return new StreamResult(CapacityDecision.FitsDelta, keyframeObject, chunks.Count, chunkTicks,
                deltas[0].Payload.Length);
        }
        Assert.Empty(deltas);
        RigSend repair = routine.First(s => s.IsChunk);
        Assert.True(repair.ChunkId.Epoch > keyframeId.Epoch, "an oversize delta starts a repair on a fresh epoch");
        return new StreamResult(CapacityDecision.OversizeDeltaKeyframe, keyframeObject, chunks.Count, chunkTicks, null);

        List<RigSend> Serve(long at)
        {
            int before = transport.Sends.Count;
            writer.BeginTick();
            stream.Serve(fixture.World, fixture.Interest, fixture.OwnerNetId, 0, at, allowance: true);
            Pump(net, client);
            return transport.Sends.Skip(before).ToList();
        }
    }

    private static void Pump(NetServer net, NetClient client)
    {
        net.Poll();
        while (net.TryDequeueEvent(out _)) { }
        client.Poll();
        while (client.TryDequeueEvent(out _)) { }
    }

    /// <summary>One viewer's served world: the entities, the interest set and the owner.</summary>
    private sealed class ViewerFixture
    {
        private readonly List<(Entity Entity, long NetId)> movers = new();

        public World World { get; } = new();

        public HashSet<long> Interest { get; } = new();

        public long OwnerNetId => 1;

        public static ViewerFixture Actors(int visible)
        {
            var fixture = new ViewerFixture();
            for (int i = 0; i < visible; i++) fixture.SpawnActor(KindOf(i), i + 1);
            return fixture;
        }

        public static ViewerFixture TagBoundary(bool oneOver)
        {
            var fixture = new ViewerFixture();
            for (int i = 0; i < ApprovedEntities; i++)
            {
                Entity entity = fixture.SpawnNetId(i + 1);
                for (int t = 0; t < TagsPerEntity; t++) Tags[t].Set(fixture.World, entity);
                if (oneOver && i == 0) Tags[TagsPerEntity].Set(fixture.World, entity);
            }
            return fixture;
        }

        // Every actor walks: a new frame-local position and heading per step. Carcasses stay put.
        public void Move(int step)
        {
            foreach ((Entity entity, long netId) in movers)
            {
                World.Set(entity, ReplicatedPosition.InFrame(WorldFrame.Origin, new Vector3(netId, 0f, step)));
                World.Set(entity, new MovementState { Grounded = true, FacingYawQ = (short)step });
            }
        }

        private void SpawnActor(ActorKind kind, long netId)
        {
            Entity entity = SpawnNetId(netId);
            byte seed = unchecked((byte)netId);
            World.Set(entity, ReplicatedPosition.InFrame(WorldFrame.Origin, new Vector3(netId, 0f, 0f)));
            if (kind != ActorKind.Carcass)
            {
                World.Set(entity, new MovementState { Grounded = true });
                movers.Add((entity, netId));
            }
            if (kind is ActorKind.Owner or ActorKind.Player)
            {
                World.Set(entity, new PlayerIdentity { DisplayName = PlayerName });
                // Every player holds owner state. Only the owner's reaches this viewer.
                World.Set(entity, new MovementOwnerState { TimeSinceGrounded = 0.25f, JumpBufferRemaining = 0.5f });
            }
            if (kind == ActorKind.Monster) MonsterKind.Set(World, entity, seed);
            foreach (ConsumerExtension extension in kind == ActorKind.Carcass ? CarcassMix : ActorMix)
                extension.Set(World, entity, seed);
        }

        private Entity SpawnNetId(long netId)
        {
            Entity entity = World.Spawn();
            World.Set(entity, new NetId(netId));
            Interest.Add(netId);
            return entity;
        }
    }
}
