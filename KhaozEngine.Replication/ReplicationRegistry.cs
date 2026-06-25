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
    private readonly List<ComponentCodec> ordered = new();
    private readonly Dictionary<ushort, ComponentCodec> byId = new();

    /// <summary>
    /// Registers a replicated component. <paramref name="write"/>/<paramref name="read"/> must be symmetric
    /// (read consumes exactly what write produced). Supplying <paramref name="lerp"/> makes the component
    /// interpolatable (smoothed between snapshots by <see cref="ClientReplicationView.Interpolate"/>).
    /// </summary>
    public void Register<T>(ushort typeId, Action<T, BinaryWriter> write, Func<BinaryReader, T> read,
        Func<T, T, float, T>? lerp = null) where T : struct, IComponent
    {
        if (typeId == 0) throw new ArgumentOutOfRangeException(nameof(typeId), "Type id 0 is reserved.");
        if (write is null) throw new ArgumentNullException(nameof(write));
        if (read is null) throw new ArgumentNullException(nameof(read));
        if (byId.ContainsKey(typeId)) throw new InvalidOperationException($"Type id {typeId} already registered.");

        bool TrySerialize(World w, Entity e, BinaryWriter bw)
        {
            if (!w.TryGet<T>(e, out T v)) return false;
            bw.Write(typeId);
            write(v, bw);
            return true;
        }

        void Deserialize(World w, Entity e, BinaryReader br) => w.Set(e, read(br));

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

        var codec = new ComponentCodec(typeId, TrySerialize, Deserialize, lerpFromBytes);
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
    public ComponentCodec(ushort typeId, Func<World, Entity, BinaryWriter, bool> trySerialize,
        Action<World, Entity, BinaryReader> deserialize, Action<World, Entity, byte[], byte[], float>? lerpFromBytes)
    {
        TypeId = typeId;
        TrySerialize = trySerialize;
        Deserialize = deserialize;
        LerpFromBytes = lerpFromBytes;
    }

    public ushort TypeId { get; }

    /// <summary>Writes <c>[typeId][data]</c> and returns true if the entity has the component; else writes nothing.</summary>
    public Func<World, Entity, BinaryWriter, bool> TrySerialize { get; }

    /// <summary>Reads the component data and sets it on the entity.</summary>
    public Action<World, Entity, BinaryReader> Deserialize { get; }

    /// <summary>Reads two raw component byte slices, lerps, and sets the result. Null if not interpolatable.</summary>
    public Action<World, Entity, byte[], byte[], float>? LerpFromBytes { get; }

    public bool Interpolatable => LerpFromBytes is not null;
}
