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
    private readonly List<(int netId, List<SnapshotBlobComponent> comps)> entities = new();

    /// <summary>Appends an entity with its ordered component frames. Component order is preserved verbatim.</summary>
    public SnapshotBlobWriter AddEntity(int netId, IEnumerable<SnapshotBlobComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        entities.Add((netId, new List<SnapshotBlobComponent>(components)));
        return this;
    }

    /// <summary>Serializes the accumulated entities to a snapshot blob.</summary>
    public byte[] ToArray()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(entities.Count);
        foreach ((int netId, List<SnapshotBlobComponent> comps) in entities)
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
        return ms.ToArray();
    }
}
