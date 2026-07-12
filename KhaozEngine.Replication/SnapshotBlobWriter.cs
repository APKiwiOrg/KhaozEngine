using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Replication;

/// <summary>
/// Builds a persist snapshot blob (<c>[count][per entity: netId + (typeId,[len],payload).. + 0]</c>) from structured
/// entities/components, the write side of <see cref="SnapshotBlobReader"/>. Extension frames (id &gt;=
/// <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) are re-emitted length-prefixed; built-ins are written
/// unframed. Feeding a reader's parsed entities straight back reproduces the original bytes, so a migration that
/// touches one component leaves every other byte identical.
/// </summary>
public sealed class SnapshotBlobWriter
{
    private readonly List<(long netId, List<SnapshotBlobComponent> comps)> entities = new();

    // Reused across repeated ToArray calls on this instance (its capacity is retained), so re-serializing a blob does
    // not allocate a fresh stream each time. Not shared across instances or threads.
    private readonly MemoryStream scratch = new();
    private readonly BinaryWriter writer;

    /// <summary>Creates an empty blob writer.</summary>
    public SnapshotBlobWriter() => writer = new BinaryWriter(scratch);

    /// <summary>Appends an entity with its ordered component frames. Component order is preserved verbatim.</summary>
    public SnapshotBlobWriter AddEntity(long netId, IEnumerable<SnapshotBlobComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        entities.Add((netId, new List<SnapshotBlobComponent>(components)));
        return this;
    }

    /// <summary>Serializes the accumulated entities to a snapshot blob. The returned array is a fresh exact-size copy
    /// the caller owns, and the internal scratch stream is reused across calls.</summary>
    public byte[] ToArray()
    {
        scratch.SetLength(0); // reset position + length, keep the buffer capacity for reuse
        BinaryWriter bw = writer;
        bw.Write(entities.Count);
        foreach ((long netId, List<SnapshotBlobComponent> comps) in entities)
        {
            bw.Write(netId);
            foreach (SnapshotBlobComponent c in comps)
            {
                bw.Write(c.TypeId);
                if (ReplicationRegistry.IsExtension(c.TypeId)) bw.Write7BitEncodedInt(c.Payload.Length);
                bw.Write(c.Payload);
            }
            bw.Write((ushort)0);   // end-of-entity terminator
        }
        bw.Flush();
        return scratch.ToArray();
    }
}
