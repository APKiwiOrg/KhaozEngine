using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Reusable scratch buffers for the indexed <see cref="SnapshotWriter"/> path: a retained
/// <see cref="System.IO.MemoryStream"/> + <see cref="System.IO.BinaryWriter"/> (so a filtered snapshot allocates only
/// its returned wire array, not a fresh stream + writer per call) and the ordering list a
/// <see cref="WorldSnapshotIndex"/> resolves an interest / border set into. Hand one instance to every
/// <see cref="SnapshotWriter"/> indexed call on a given server-tick thread and it is reused across all of them.
/// Single-threaded: not safe to share across threads (each tick thread keeps its own).
/// </summary>
public sealed class SnapshotScratch
{
    internal readonly MemoryStream Stream = new();
    internal readonly BinaryWriter Writer;
    internal readonly List<(int order, long netId, Entity entity)> Ordered = new();

    /// <summary>Creates an empty scratch. Reuse it across many <see cref="SnapshotWriter"/> indexed calls.</summary>
    public SnapshotScratch() => Writer = new BinaryWriter(Stream);
}
