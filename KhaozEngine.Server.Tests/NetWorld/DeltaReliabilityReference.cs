using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Independent reference for the delta acceptance rig. None of it calls a capture, projection, writer, reader or
/// publication type: viewer projections come straight from the served world through each component's registered
/// serializer, visibility from the rig's own positions and table, and presentation from the timestamped oracles below.
/// </summary>
internal static class DeltaReliabilityReference
{
    /// <summary>
    /// The projection a viewer must be served: every net-id entity of <paramref name="served"/> in
    /// <paramref name="filteredInterest"/>, with each registered component that rides the Replicate channel, an
    /// owner-only one only on <paramref name="ownerNetId"/>'s own entity. Payloads are serialized through
    /// <c>ComponentCodec.TrySerialize</c> and stripped of their type id and extension length frame. Ordered by net id
    /// then type id, as <see cref="ProjectionDump.Of"/> orders.
    /// </summary>
    public static IReadOnlyList<ProjectionEntry> ExpectedProjection(World served, ReplicationRegistry registry,
        IReadOnlySet<long> filteredInterest, long ownerNetId)
    {
        var entries = new List<ProjectionEntry>();
        var seen = new HashSet<long>();
        foreach (Entity entity in served.Query().With<NetId>().Entities())
        {
            long netId = served.Get<NetId>(entity).Value;
            if (!filteredInterest.Contains(netId)) continue;
            Assert.True(seen.Add(netId), $"served world holds net id {netId} twice");
            foreach (ComponentCodec codec in registry.Ordered)
            {
                if ((codec.Channels & ReplicationChannels.Replicate) == 0) continue;
                if ((codec.Channels & ReplicationChannels.OwnerOnly) != 0 && netId != ownerNetId) continue;
                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream);
                if (!codec.TrySerialize(served, entity, writer)) continue;
                writer.Flush();
                entries.Add(new ProjectionEntry(netId, codec.TypeId, StripFrame(stream.ToArray(), codec.TypeId)));
            }
        }
        entries.Sort(static (a, b) => a.NetId != b.NetId ? a.NetId.CompareTo(b.NetId) : a.TypeId.CompareTo(b.TypeId));
        return entries;
    }

    /// <summary>The net ids a viewer must see: itself, plus every known entity within <paramref name="radius"/> on the
    /// ground plane that <paramref name="visible"/> allows. Computed from the rig's own positions and visibility table,
    /// so it never reads a ghost or any other component of the served world.</summary>
    public static HashSet<long> ExpectedVisible(int viewerSlot, long viewerNetId, Vector3 viewerPosition,
        IEnumerable<KeyValuePair<long, Vector3>> positions, float radius, Func<int, long, bool>? visible)
    {
        var set = new HashSet<long> { viewerNetId };
        float r2 = radius * radius;
        foreach ((long netId, Vector3 p) in positions)
        {
            if (netId == viewerNetId) continue;
            float dx = p.X - viewerPosition.X, dz = p.Z - viewerPosition.Z;
            if (dx * dx + dz * dz > r2) continue;
            if (visible is null || visible(viewerSlot, netId)) set.Add(netId);
        }
        return set;
    }

    /// <summary>A projection's net ids.</summary>
    public static HashSet<long> Members(IReadOnlyList<ProjectionEntry> projection)
    {
        var set = new HashSet<long>();
        foreach (ProjectionEntry entry in projection) set.Add(entry.NetId);
        return set;
    }

    // TrySerialize writes [typeId u16]([7-bit length])[payload]. Check the frame and keep the payload.
    private static byte[] StripFrame(byte[] framed, ushort typeId)
    {
        Assert.Equal(typeId, (ushort)(framed[0] | (framed[1] << 8)));
        if (!ReplicationRegistry.IsExtension(typeId)) return framed[2..];
        int length = 0, shift = 0, at = 2;
        while (true)
        {
            byte b = framed[at++];
            length |= (b & 0x7F) << shift;
            if (b < 0x80) break;
            shift += 7;
        }
        Assert.Equal(framed.Length - at, length);
        return framed[at..];
    }
}

/// <summary>The local presentation a frame must show: predicted and rendered state from the oracle.</summary>
internal readonly record struct LocalExpectation(PlayerMoveState Predicted, Vector3 Rendered, bool TeleportKnown);

/// <summary>
/// Local presentation oracle. Replays the recorded submitted commands from the authoritative spawn basis through a
/// separate <see cref="PlayerMoveSimulator"/> on the same flat ground, tuning and tick. The server teleport is applied
/// before the first command the server consumed at or after the teleport tick, but the client can only know it once
/// the teleport ingest was accepted, so frames before that ingest expect the path without it. Rendered position eases
/// from the previous expected state to the current one by the time since that submission, and collapses onto the
/// predicted state at the teleport ingest, which is a hard cut.
/// </summary>
internal sealed class LocalOracle
{
    private readonly List<PlayerMoveState> plain = new();
    private readonly List<PlayerMoveState> teleported = new();
    private readonly List<int> submitFrames = new();
    private readonly PlayerMoveState basis;
    private readonly float tickSeconds;
    private readonly float frameSeconds;
    private readonly int teleportIngestFrame;

    /// <param name="tuning">The movement tuning both heads run.</param>
    /// <param name="tickSeconds">The fixed simulation tick.</param>
    /// <param name="frameSeconds">The presentation step.</param>
    /// <param name="spawnBasis">The authoritative state before the first submitted command, absolute.</param>
    /// <param name="submissions">Every submitted command in order: its frame, the command and the server tick that
    /// consumed it.</param>
    /// <param name="teleportTick">The server tick the teleport was applied before, or null.</param>
    /// <param name="destination">The absolute teleport destination.</param>
    /// <param name="teleportIngestFrame">The first frame whose poll accepted state built at or after the teleport
    /// tick. Ignored without a teleport.</param>
    public LocalOracle(MoveTuning tuning, float tickSeconds, float frameSeconds, PlayerMoveState spawnBasis,
        IReadOnlyList<(int Frame, MoveCommand Command, int ConsumedTick)> submissions, int? teleportTick,
        Vector3 destination, int teleportIngestFrame)
    {
        static float Flat(float x, float z) => 0f;
        var simulator = new PlayerMoveSimulator(Flat, tuning);
        this.tickSeconds = tickSeconds;
        this.frameSeconds = frameSeconds;
        this.teleportIngestFrame = teleportTick is null ? int.MaxValue : teleportIngestFrame;
        basis = spawnBasis;
        PlayerMoveState a = spawnBasis, b = spawnBasis;
        bool cut = false;
        foreach ((int frame, MoveCommand command, int consumedTick) in submissions)
        {
            if (!cut && teleportTick is int t && consumedTick >= t)
            {
                cut = true;
                PlayerMoveState moved = b;
                moved.Position = new Vector3(destination.X, b.Position.Y, destination.Z);
                moved.TeleportEpoch = b.TeleportEpoch + 1u;
                b = moved;
            }
            a = simulator.Step(a, command, tickSeconds);
            b = simulator.Step(b, command, tickSeconds);
            plain.Add(a);
            teleported.Add(b);
            submitFrames.Add(frame);
        }
    }

    /// <summary>The expected predicted and rendered state after the presentation of <paramref name="frame"/>.</summary>
    public LocalExpectation At(int frame)
    {
        bool known = frame >= teleportIngestFrame;
        List<PlayerMoveState> path = known ? teleported : plain;
        int last = submitFrames.Count - 1;
        while (last >= 0 && submitFrames[last] > frame) last--;
        if (last < 0) return new LocalExpectation(basis, basis.Position, known);
        PlayerMoveState current = path[last];
        if (known && submitFrames[last] <= teleportIngestFrame)
            return new LocalExpectation(current, current.Position, known);
        PlayerMoveState previous = last == 0 ? basis : path[last - 1];
        float seconds = 0f;
        for (int f = submitFrames[last]; f <= frame; f++) seconds = MathF.Min(seconds + frameSeconds, tickSeconds);
        float frac = MathF.Min(1f, seconds / tickSeconds);
        Vector2 planar = Vector2.Lerp(new Vector2(previous.Position.X, previous.Position.Z),
            new Vector2(current.Position.X, current.Position.Z), frac);
        float vertical = previous.Position.Y + ((current.Position.Y - previous.Position.Y) * frac);
        return new LocalExpectation(current, new Vector3(planar.X, vertical, planar.Y), known);
    }
}

/// <summary>One authoritative sample of the observed remote, read from the served world when its state was built.</summary>
internal readonly record struct RemoteSample(Vector3 Position, short FacingYawQ, bool Grounded, bool Swimming,
    uint TeleportEpoch);

/// <summary>The remote presentation a frame must show.</summary>
internal readonly record struct RemoteExpectation(Vector3 Position, short FacingYawQ, bool Grounded, bool Swimming,
    bool Held);

/// <summary>
/// Remote presentation oracle. Each accepted ingest of the observed remote is stamped with the rig's presentation
/// clock, a double accumulated from the same float steps the rig passes to <c>AdvancePresentation</c>. A stamp at or
/// before the previous one overwrites it. A teleport epoch advance at ingest drops every older sample. Render time is
/// the clock minus the interpolation delay, a float product as the client computes it. Before the oldest sample it
/// clamps to the oldest, at or past the newest it holds the newest and flags a hold when render time exceeds the newest
/// stamp by more than 1e-9, otherwise position lerps by the clamped fraction and heading and flags take the lower
/// sample when the fraction is at most one half.
/// </summary>
internal sealed class RemoteOracle
{
    private readonly List<(double Stamp, RemoteSample Sample)> samples = new();
    private readonly float delaySeconds;
    private uint? epochWatermark;

    public RemoteOracle(float interpolationDelayTicks, float tickSeconds) =>
        delaySeconds = MathF.Max(0f, interpolationDelayTicks) * tickSeconds;

    /// <summary>True once a sample arrived.</summary>
    public bool HasSamples => samples.Count > 0;

    public void Ingest(double stamp, RemoteSample sample)
    {
        if (samples.Count > 0 && stamp <= samples[^1].Stamp) samples[^1] = (stamp, sample);
        else samples.Add((stamp, sample));
        if (epochWatermark is uint known && sample.TeleportEpoch > known) samples.RemoveRange(0, samples.Count - 1);
        epochWatermark = epochWatermark is uint w && w > sample.TeleportEpoch ? w : sample.TeleportEpoch;
    }

    /// <summary>The expectation after a presentation advance that left the clock at <paramref name="clock"/>.</summary>
    public RemoteExpectation Render(double clock)
    {
        if (samples.Count == 0) throw new InvalidOperationException("No remote sample to render.");
        double renderTime = clock - delaySeconds;
        int lo = -1;
        for (int i = 0; i < samples.Count; i++) { if (samples[i].Stamp <= renderTime) lo = i; else break; }
        if (lo < 0) return Expect(samples[0].Sample, held: false);
        if (lo == samples.Count - 1)
        {
            (double stamp, RemoteSample newest) = samples[lo];
            return Expect(newest, held: renderTime > stamp + 1e-9);
        }
        (double aStamp, RemoteSample a) = samples[lo];
        (double bStamp, RemoteSample b) = samples[lo + 1];
        double span = bStamp - aStamp;
        float frac = span > 0 ? (float)Math.Clamp((renderTime - aStamp) / span, 0.0, 1.0) : 1f;
        RemoteSample discrete = frac <= 0.5f ? a : b;
        return new RemoteExpectation(Vector3.Lerp(a.Position, b.Position, frac), discrete.FacingYawQ,
            discrete.Grounded, discrete.Swimming, Held: false);
    }

    private static RemoteExpectation Expect(RemoteSample s, bool held) =>
        new(s.Position, s.FacingYawQ, s.Grounded, s.Swimming, held);
}

/// <summary>A replicated value sentinel, 4 bytes.</summary>
internal struct SentinelValue : IComponent
{
    public int Value;
}

/// <summary>A replicated flags sentinel, 1 byte.</summary>
internal struct SentinelFlag : IComponent
{
    public byte Flags;
}

/// <summary>A replicated zero-byte tag.</summary>
internal struct SentinelTag : IComponent
{
}

/// <summary>An owner-only sentinel, served only on the viewer's own entity.</summary>
internal struct SentinelOwner : IComponent
{
    public long Pattern;
}

/// <summary>A persist and migrate sentinel that never rides the Replicate channel.</summary>
internal struct SentinelServerOnly : IComponent
{
    public long Pattern;
}

/// <summary>The rig's shared registry: movement built-ins, the padding component and the sentinels.</summary>
internal static class DeltaRigRegistry
{
    public const ushort ValueTypeId = ReplicationRegistry.FirstExtensionTypeId + 20;
    public const ushort FlagTypeId = ReplicationRegistry.FirstExtensionTypeId + 21;
    public const ushort TagTypeId = ReplicationRegistry.FirstExtensionTypeId + 22;
    public const ushort OwnerTypeId = ReplicationRegistry.FirstExtensionTypeId + 23;
    public const ushort ServerOnlyTypeId = ReplicationRegistry.FirstExtensionTypeId + 24;

    public static ReplicationRegistry Create() => MoveProtocol.CreateRegistry(r =>
    {
        r.Register<PadState>(Pad.TypeId,
            static (pad, writer) =>
            {
                for (int i = 0; i < pad.Length; i++) writer.Write(pad.Fill);
            },
            static reader =>
            {
                int length = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
                byte[] bytes = reader.ReadBytes(length);
                return new PadState { Length = length, Fill = length == 0 ? (byte)0 : bytes[0] };
            });
        r.Register<SentinelValue>(ValueTypeId, static (v, w) => w.Write(v.Value),
            static reader => new SentinelValue { Value = reader.ReadInt32() });
        r.Register<SentinelFlag>(FlagTypeId, static (v, w) => w.Write(v.Flags),
            static reader => new SentinelFlag { Flags = reader.ReadByte() });
        r.Register<SentinelTag>(TagTypeId, static (_, _) => { }, static _ => default);
        r.Register<SentinelOwner>(OwnerTypeId, static (v, w) => w.Write(v.Pattern),
            static reader => new SentinelOwner { Pattern = reader.ReadInt64() },
            channels: ReplicationChannels.Default | ReplicationChannels.OwnerOnly);
        r.Register<SentinelServerOnly>(ServerOnlyTypeId, static (v, w) => w.Write(v.Pattern),
            static reader => new SentinelServerOnly { Pattern = reader.ReadInt64() },
            channels: ReplicationChannels.Persist | ReplicationChannels.Migrate);
    });
}
