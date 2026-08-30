using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Replication;

/// <summary>One component frame parsed from a persist snapshot blob: its stable type id and raw payload bytes
/// (no type id, no length prefix).</summary>
public readonly struct SnapshotBlobComponent
{
    public SnapshotBlobComponent(ushort typeId, byte[] payload)
    {
        TypeId = typeId;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    /// <summary>The component's stable replication type id.</summary>
    public ushort TypeId { get; }

    /// <summary>The component's serialized payload bytes (exactly what the codec wrote, no framing).</summary>
    public byte[] Payload { get; }

    /// <summary>True when this is a length-prefixed consumer extension frame
    /// (id &gt;= <see cref="ReplicationRegistry.FirstExtensionTypeId"/>), false for an unframed built-in.</summary>
    public bool IsExtension => ReplicationRegistry.IsExtension(TypeId);
}

/// <summary>One entity parsed from a persist snapshot blob: its net id and ordered component frames.</summary>
public sealed class SnapshotBlobEntity
{
    public SnapshotBlobEntity(long netId, IReadOnlyList<SnapshotBlobComponent> components)
    {
        NetId = netId;
        Components = components ?? throw new ArgumentNullException(nameof(components));
    }

    /// <summary>The entity's network id (64-bit since 10.0.0).</summary>
    public long NetId { get; }

    /// <summary>The entity's component frames, in blob order.</summary>
    public IReadOnlyList<SnapshotBlobComponent> Components { get; }
}

/// <summary>
/// Walks a persist snapshot blob (<c>[count][per entity: netId + (typeId,[len],payload).. + 0]</c>) into structured
/// entities/components, so a cell-blob migration can map / drop / transform per-component payloads without
/// hand-parsing the stream. Extension frames (id &gt;= <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) are
/// length-prefixed and self-describing. Built-in frames are unframed, so they are walkable only when the caller
/// supplies <c>builtinPayloadLength</c> giving each built-in id's payload byte count at the OLD layout the migration
/// targets; without it (or a negative length) an encountered built-in frame throws rather than mis-parsing. Every
/// malformed input (truncation, a length past the buffer) throws <see cref="InvalidOperationException"/>. Pair with
/// <see cref="SnapshotBlobWriter"/> to re-emit; a well-formed blob round-trips byte-identically.
/// <para><b>A built-in that carries its own length.</b> Not every built-in's size is a function of its type id
/// alone: an identity frame is <c>[ushort byteLen][byteLen UTF-8 bytes]</c>, so an id-keyed callback has no value it
/// can return for it, and such a frame used to throw with no way to walk it. The stream-aware overload takes a
/// resolver that is handed the reader AT the payload's first byte, so it can read a prefix to work the length out.
/// Whatever it reads is rewound before the payload is captured, so the captured bytes are the whole frame body,
/// prefix included, and the round trip through <see cref="SnapshotBlobWriter"/> is still byte-identical.</para>
/// </summary>
public sealed class SnapshotBlobReader
{
    private readonly List<SnapshotBlobEntity> entities = new();

    /// <param name="snapshot">The raw snapshot body (post any wrapper header).</param>
    /// <param name="builtinPayloadLength">Old-layout payload byte count for a built-in (unframed) type id, or a
    /// negative value / null for "unknown" (which makes an encountered built-in frame throw). Not consulted for
    /// extension ids.</param>
    public SnapshotBlobReader(byte[] snapshot, Func<ushort, int>? builtinPayloadLength = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Parse(snapshot, builtinPayloadLength is null ? null : (id, _) => builtinPayloadLength(id));
    }

    /// <param name="snapshot">The raw snapshot body (post any wrapper header).</param>
    /// <param name="builtinPayloadReader">Old-layout resolver for a built-in (unframed) type id, called with the
    /// reader positioned at the FIRST BYTE of that frame's payload and returning the frame's TOTAL payload byte
    /// count. It may read ahead to work that out, which is how a self-describing built-in (an identity frame's
    /// <c>[ushort byteLen]</c> prefix) reports its own size; the stream is rewound to the frame start before the
    /// payload is captured, so the captured bytes carry the prefix and re-emit byte-identically. A negative return
    /// means "unknown" and throws, exactly as it does on the id-only overload. Not consulted for extension ids.</param>
    public SnapshotBlobReader(byte[] snapshot, Func<ushort, BinaryReader, int> builtinPayloadReader)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(builtinPayloadReader);
        Parse(snapshot, builtinPayloadReader);
    }

    private void Parse(byte[] snapshot, Func<ushort, BinaryReader, int>? builtinPayloadLength)
    {
        using var ms = new MemoryStream(snapshot, writable: false);
        using var br = new BinaryReader(ms);

        int count = ReadInt32(br, ms, "entity count");
        if (count < 0) throw new InvalidOperationException($"Snapshot entity count {count} is negative.");
        for (int i = 0; i < count; i++)
        {
            long netId = ReadInt64(br, ms, "entity net id");
            var comps = new List<SnapshotBlobComponent>();
            while (true)
            {
                ushort typeId = ReadUInt16(br, ms, "component type id");
                if (typeId == 0) break;

                int len;
                if (ReplicationRegistry.IsExtension(typeId))
                {
                    len = br.Read7BitEncodedInt();   // extension payloads carry their own length
                }
                else
                {
                    len = ResolveBuiltinLength(typeId, br, ms, builtinPayloadLength);
                }

                long pos = ms.Position;
                if (len < 0 || pos + len > ms.Length)
                    throw new InvalidOperationException($"Snapshot component {typeId} has an invalid length {len}.");
                byte[] payload = br.ReadBytes(len);
                if (payload.Length != len)
                    throw new InvalidOperationException($"Snapshot component {typeId} payload truncated.");
                comps.Add(new SnapshotBlobComponent(typeId, payload));
            }
            entities.Add(new SnapshotBlobEntity(netId, comps));
        }
    }

    /// <summary>The parsed entities in blob order.</summary>
    public IReadOnlyList<SnapshotBlobEntity> Entities => entities;

    // Asks the resolver how many payload bytes an unframed built-in frame occupies. The resolver reads from the
    // payload's first byte, so a self-describing built-in can consume its own prefix to answer, and the stream is
    // rewound either way: the payload captured above is the WHOLE frame body, prefix included, which is what keeps
    // the writer's re-emit byte-identical. A resolver that runs off the end of a truncated body surfaces as this
    // class's own InvalidOperationException rather than a raw EndOfStreamException, so every malformed input still
    // reports through one exception type.
    private static int ResolveBuiltinLength(ushort typeId, BinaryReader br, MemoryStream ms,
        Func<ushort, BinaryReader, int>? resolver)
    {
        int len;
        if (resolver is null) len = -1;
        else
        {
            long start = ms.Position;
            try { len = resolver(typeId, br); }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Snapshot built-in component {typeId} could not be measured; the frame is malformed or truncated.", ex);
            }
            finally { ms.Position = start; }
        }

        if (len < 0)
            throw new InvalidOperationException(
                $"Snapshot built-in component {typeId} is unframed; supply builtinPayloadLength for the old layout to walk it.");
        return len;
    }

    private static int ReadInt32(BinaryReader br, MemoryStream ms, string what)
    {
        if (ms.Position + 4 > ms.Length) throw new InvalidOperationException($"Snapshot truncated reading {what}.");
        return br.ReadInt32();
    }

    private static long ReadInt64(BinaryReader br, MemoryStream ms, string what)
    {
        if (ms.Position + 8 > ms.Length) throw new InvalidOperationException($"Snapshot truncated reading {what}.");
        return br.ReadInt64();
    }

    private static ushort ReadUInt16(BinaryReader br, MemoryStream ms, string what)
    {
        if (ms.Position + 2 > ms.Length) throw new InvalidOperationException($"Snapshot truncated reading {what}.");
        return br.ReadUInt16();
    }
}
