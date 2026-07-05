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
                    len = builtinPayloadLength?.Invoke(typeId) ?? -1;
                    if (len < 0)
                        throw new InvalidOperationException(
                            $"Snapshot built-in component {typeId} is unframed; supply builtinPayloadLength for the old layout to walk it.");
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
