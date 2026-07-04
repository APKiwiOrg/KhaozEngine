using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Declares which component types replicate and how, erasing each type <c>T</c> into closures over the public
/// <see cref="World"/> API keyed by a stable <see cref="ushort"/> type id. Both server (writer) and client
/// (view) must register the same ids/codecs. Type id <c>0</c> is reserved as the snapshot terminator.
/// </summary>
public sealed class ReplicationRegistry
{
    /// <summary>
    /// The lowest type id a consumer ("extension") component may use. Ids below this are reserved for engine
    /// built-ins and keep their exact, unframed wire encoding. Components registered at or above this floor are
    /// <b>length-prefixed on the wire</b> so a client whose registry does not know the id can SKIP it (ignore the
    /// component) instead of failing the snapshot — the seam that lets a server add a replicated component (an NPC
    /// kind, HP, faction, …) while older clients that never registered it keep running. An unknown id BELOW the
    /// floor is still a hard "client out of date" mismatch (it throws), preserving the pre-existing contract.
    /// Register consumer components at <c>FirstExtensionTypeId + n</c>; the engine will never claim those ids.
    /// </summary>
    public const ushort FirstExtensionTypeId = 16;

    /// <summary>True when <paramref name="typeId"/> is a consumer extension id (length-prefixed + skippable on the
    /// wire); false for a reserved built-in id (unframed, throw-on-unknown). See <see cref="FirstExtensionTypeId"/>.</summary>
    public static bool IsExtension(ushort typeId) => typeId >= FirstExtensionTypeId;

    private readonly List<ComponentCodec> ordered = new();
    private readonly Dictionary<ushort, ComponentCodec> byId = new();

    /// <summary>
    /// Registers a replicated component. <paramref name="write"/>/<paramref name="read"/> must be symmetric
    /// (read consumes exactly what write produced). Supplying <paramref name="lerp"/> makes the component
    /// interpolatable (smoothed between snapshots by <see cref="ClientReplicationView.Interpolate"/>).
    /// <paramref name="channels"/> declares which downstream consumers see the component's bytes (client AoI
    /// replication, cell persistence, cell handoff, owner-only visibility); it defaults to
    /// <see cref="ReplicationChannels.Default"/> (replicate + persist + migrate), the pre-9.28.0 behaviour, so
    /// omitting it leaves the wire byte-identical. A built-in id (below <see cref="FirstExtensionTypeId"/>) must
    /// keep <see cref="ReplicationChannels.Default"/> - its unframed encoding is the core protocol - and
    /// <see cref="ReplicationChannels.OwnerOnly"/> requires <see cref="ReplicationChannels.Replicate"/>; either
    /// violation throws.
    /// </summary>
    public void Register<T>(ushort typeId, Action<T, BinaryWriter> write, Func<BinaryReader, T> read,
        Func<T, T, float, T>? lerp = null, ReplicationChannels channels = ReplicationChannels.Default)
        where T : struct, IComponent
    {
        if (typeId == 0) throw new ArgumentOutOfRangeException(nameof(typeId), "Type id 0 is reserved.");
        if (write is null) throw new ArgumentNullException(nameof(write));
        if (read is null) throw new ArgumentNullException(nameof(read));
        if (byId.ContainsKey(typeId)) throw new InvalidOperationException($"Type id {typeId} already registered.");
        // Built-in ids (< the floor) are the core protocol: their exact unframed encoding on every channel is fixed,
        // so per-channel flags must never alter what they write. Reject any non-default channel set below the floor.
        if (!IsExtension(typeId) && channels != ReplicationChannels.Default)
            throw new ArgumentException(
                $"Built-in type id {typeId} (< FirstExtensionTypeId {FirstExtensionTypeId}) must keep ReplicationChannels.Default; " +
                "per-channel flags are only honored for consumer extension components.", nameof(channels));
        // OwnerOnly is a modifier on the Replicate channel (it scopes a replicated component to its owning client);
        // it is meaningless without Replicate, so reject it rather than silently making the component invisible.
        if ((channels & ReplicationChannels.OwnerOnly) != 0 && (channels & ReplicationChannels.Replicate) == 0)
            throw new ArgumentException(
                "ReplicationChannels.OwnerOnly requires ReplicationChannels.Replicate (it scopes a replicated component to its owning client).",
                nameof(channels));

        // Extension components (id >= floor) are length-prefixed so an older client can skip an id it never
        // registered. Built-ins stay inline (zero per-component overhead on the movement hot path).
        bool lengthPrefixed = IsExtension(typeId);

        bool TrySerialize(World w, Entity e, BinaryWriter bw)
        {
            if (!w.TryGet<T>(e, out T v)) return false;
            bw.Write(typeId);
            if (lengthPrefixed)
            {
                using var ms = new MemoryStream();
                using var inner = new BinaryWriter(ms);
                write(v, inner);
                inner.Flush();
                byte[] data = ms.ToArray();
                bw.Write7BitEncodedInt(data.Length);   // [typeId][7-bit len][data]
                bw.Write(data);
            }
            else
            {
                write(v, bw);                            // [typeId][data]
            }
            return true;
        }

        void Deserialize(World w, Entity e, BinaryReader br) => w.Set(e, read(br));

        byte[]? CaptureData(World w, Entity e)
        {
            if (!w.TryGet<T>(e, out T v)) return null;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            write(v, bw);
            bw.Flush();
            return ms.ToArray();
        }

        void RemoveComponent(World w, Entity e) => w.Remove<T>(e);

        Action<World, Entity, byte[], byte[], float>? lerpFromBytes = null;
        if (lerp is not null)
        {
            lerpFromBytes = (w, e, prevBytes, curBytes, t) =>
            {
                T a = read(new BinaryReader(new MemoryStream(prevBytes)));
                T b = read(new BinaryReader(new MemoryStream(curBytes)));
                w.Set(e, lerp(a, b, t));
            };
        }

        var codec = new ComponentCodec(typeId, lengthPrefixed, channels, TrySerialize, Deserialize, lerpFromBytes, CaptureData, RemoveComponent);
        ordered.Add(codec);
        byId[typeId] = codec;
    }

    /// <summary>Codecs in registration order (the order the writer serializes present components).</summary>
    internal IReadOnlyList<ComponentCodec> Ordered => ordered;

    internal bool TryGet(ushort typeId, out ComponentCodec codec) => byId.TryGetValue(typeId, out codec!);
}

/// <summary>A single component type's erased serialize/deserialize/lerp closures.</summary>
internal sealed class ComponentCodec
{
    public ComponentCodec(ushort typeId, bool lengthPrefixed, ReplicationChannels channels,
        Func<World, Entity, BinaryWriter, bool> trySerialize,
        Action<World, Entity, BinaryReader> deserialize, Action<World, Entity, byte[], byte[], float>? lerpFromBytes,
        Func<World, Entity, byte[]?> captureData, Action<World, Entity> removeComponent)
    {
        TypeId = typeId;
        LengthPrefixed = lengthPrefixed;
        Channels = channels;
        TrySerialize = trySerialize;
        Deserialize = deserialize;
        LerpFromBytes = lerpFromBytes;
        CaptureData = captureData;
        RemoveComponent = removeComponent;
    }

    public ushort TypeId { get; }

    /// <summary>True when this is a consumer extension component: it is length-prefixed on the wire
    /// (<c>[typeId][7-bit len][data]</c>) so an older client can skip it. See
    /// <see cref="ReplicationRegistry.FirstExtensionTypeId"/>.</summary>
    public bool LengthPrefixed { get; }

    /// <summary>Which downstream consumers see this component's bytes (client AoI replication, cell persistence,
    /// cell handoff, owner-only visibility). See <see cref="ReplicationChannels"/>.</summary>
    public ReplicationChannels Channels { get; }

    /// <summary>
    /// Whether a consumer serving <paramref name="channel"/> (one of <see cref="ReplicationChannels.Replicate"/>,
    /// <see cref="ReplicationChannels.Persist"/>, <see cref="ReplicationChannels.Migrate"/>) should write this
    /// component for the entity whose net id is <paramref name="netId"/>. False if the channel bit is unset; on the
    /// Replicate channel an <see cref="ReplicationChannels.OwnerOnly"/> component is written only to its owner
    /// (<paramref name="ownerNetId"/> equals <paramref name="netId"/>), and to nobody when
    /// <paramref name="ownerNetId"/> is null (e.g. ghost mirroring, a full unowned snapshot).
    /// </summary>
    public bool ShouldWrite(ReplicationChannels channel, int netId, int? ownerNetId)
    {
        if ((Channels & channel) == 0) return false;
        if (channel == ReplicationChannels.Replicate
            && (Channels & ReplicationChannels.OwnerOnly) != 0
            && (ownerNetId is null || netId != ownerNetId.Value)) return false;
        return true;
    }

    /// <summary>Writes <c>[typeId](len)[data]</c> (the length only for an extension component) and returns true if
    /// the entity has the component; else writes nothing.</summary>
    public Func<World, Entity, BinaryWriter, bool> TrySerialize { get; }

    /// <summary>Reads the component data and sets it on the entity.</summary>
    public Action<World, Entity, BinaryReader> Deserialize { get; }

    /// <summary>Returns just the component's data bytes (no type id) if present, else null. Used for delta capture.</summary>
    public Func<World, Entity, byte[]?> CaptureData { get; }

    /// <summary>Removes the component from the entity (for delta-applied component removals).</summary>
    public Action<World, Entity> RemoveComponent { get; }

    /// <summary>Reads two raw component byte slices, lerps, and sets the result. Null if not interpolatable.</summary>
    public Action<World, Entity, byte[], byte[], float>? LerpFromBytes { get; }

    public bool Interpolatable => LerpFromBytes is not null;
}
