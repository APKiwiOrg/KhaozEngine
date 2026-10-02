using System;
using System.Collections.Generic;

namespace KhaozEngine.Replication;

/// <summary>
/// One component frame of a projection: a type id and the exact raw payload bytes as a slice of a backing array.
/// Unknown extension frames keep their opaque payload. The backing is never written after construction, and retention
/// charges its full <c>Length</c>, not the slice.
/// </summary>
internal readonly struct ProjectedComponent
{
    public ProjectedComponent(ushort typeId, byte[] backing, int offset, int length)
    {
        if (typeId == 0) throw new ArgumentOutOfRangeException(nameof(typeId), "Type id 0 is the terminator.");
        ArgumentNullException.ThrowIfNull(backing);
        if (offset < 0 || length < 0 || offset > backing.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length), "The payload slice lies outside its backing.");
        TypeId = typeId;
        Backing = backing;
        Offset = offset;
        Length = length;
    }

    public ushort TypeId { get; }

    /// <summary>The array holding the payload. Shared only between immutable projections.</summary>
    public byte[] Backing { get; }

    public int Offset { get; }

    public int Length { get; }

    public ReadOnlySpan<byte> Span => new(Backing, Offset, Length);

    public ReadOnlyMemory<byte> Memory => new(Backing, Offset, Length);
}

/// <summary>
/// One entity's complete replicated component set inside a projection, in capture (registration) order. Immutable, so
/// a reconstructed projection may share an unchanged entity with its baseline.
/// </summary>
internal sealed class ProjectedEntity
{
    private readonly ProjectedComponent[] components;

    /// <summary>Takes ownership of <paramref name="components"/>. The caller must not keep or write the array.</summary>
    /// <exception cref="ArgumentException">Two frames share a type id.</exception>
    internal ProjectedEntity(ProjectedComponent[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Length > 1)
        {
            var seen = new HashSet<ushort>();
            foreach (ProjectedComponent c in components)
                if (!seen.Add(c.TypeId))
                    throw new ArgumentException($"Duplicate component type id {c.TypeId}.", nameof(components));
        }
        this.components = components;
    }

    public int Count => components.Length;

    public ProjectedComponent this[int index] => components[index];

    public bool TryGet(ushort typeId, out ProjectedComponent component)
    {
        foreach (ProjectedComponent c in components)
        {
            if (c.TypeId != typeId) continue;
            component = c;
            return true;
        }
        component = default;
        return false;
    }

    public bool Contains(ushort typeId) => TryGet(typeId, out _);
}

/// <summary>
/// An immutable viewer projection: net id to its complete component set, with exact raw payload bytes. Entity
/// insertion order is capture order, which format 2 encoding preserves. Construction enforces the per-projection
/// entity, component frame and backing byte limits, so an impossible projection fails with
/// <see cref="DeltaRebuildFailure.CapacityExceeded"/> before anything retains it. Zero-length arrays hold no bytes and
/// are not counted as backing.
/// </summary>
internal sealed class ReplicationProjection
{
    private readonly Dictionary<long, ProjectedEntity> entities;
    private readonly byte[][] backings;

    private ReplicationProjection(Dictionary<long, ProjectedEntity> entities, int componentCount, byte[][] backings,
        long backingBytes)
    {
        this.entities = entities;
        this.backings = backings;
        ComponentCount = componentCount;
        BackingBytes = backingBytes;
    }

    /// <summary>The empty projection a keyframe reconstructs from.</summary>
    public static ReplicationProjection Empty { get; } =
        new(new Dictionary<long, ProjectedEntity>(), 0, Array.Empty<byte[]>(), 0);

    public int EntityCount => entities.Count;

    /// <summary>Component frames across every entity, zero-byte and opaque frames included.</summary>
    public int ComponentCount { get; }

    /// <summary>The full <c>Length</c> of every distinct backing array this projection reaches.</summary>
    public long BackingBytes { get; }

    /// <summary>The entities in capture order.</summary>
    public IReadOnlyDictionary<long, ProjectedEntity> Entities => entities;

    /// <summary>The distinct nonempty backing arrays this projection reaches, by reference identity.</summary>
    internal IReadOnlyList<byte[]> Backings => backings;

    public bool TryGetEntity(long netId, out ProjectedEntity entity) => entities.TryGetValue(netId, out entity!);

    /// <summary>
    /// Copies the visible payloads of an already interest-filtered and owner-scoped viewer source into one fresh,
    /// exact-size compact buffer. The result never references a shared capture buffer, so a retained viewer state can
    /// neither keep hidden owner bytes alive nor be charged a visible slice while holding the whole capture. Counts
    /// and the byte total are checked before the buffer is allocated.
    /// </summary>
    /// <exception cref="DeltaRebuildException">The source exceeds an entity, frame or byte limit.</exception>
    public static ReplicationProjection Compact(Dictionary<long, CapturedComponents> source, DeltaRebuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (source.Count > options.MaxEntities) throw EntityLimit(source.Count, options);

        long componentCount = 0, byteCount = 0;
        foreach (CapturedComponents comps in source.Values)
        {
            componentCount += comps.Count;
            foreach (ushort typeId in comps.TypeIds)
                if (comps.TryGetSpan(typeId, out ReadOnlySpan<byte> payload)) byteCount += payload.Length;
        }
        if (componentCount > options.MaxComponents) throw ComponentLimit(componentCount, options);
        if (byteCount > options.MaxRetainedPayloadBytes) throw ByteLimit(byteCount, options);

        byte[] compact = byteCount == 0 ? Array.Empty<byte>() : new byte[byteCount];
        var map = new Dictionary<long, ProjectedEntity>(source.Count);
        int offset = 0;
        foreach (KeyValuePair<long, CapturedComponents> kv in source)
        {
            var frames = new ProjectedComponent[kv.Value.Count];
            int i = 0;
            foreach (ushort typeId in kv.Value.TypeIds)
            {
                kv.Value.TryGetSpan(typeId, out ReadOnlySpan<byte> payload);
                payload.CopyTo(compact.AsSpan(offset));
                frames[i++] = new ProjectedComponent(typeId, compact, offset, payload.Length);
                offset += payload.Length;
            }
            map.Add(kv.Key, new ProjectedEntity(frames));
        }
        byte[][] backing = compact.Length == 0 ? Array.Empty<byte[]>() : new[] { compact };
        return new ReplicationProjection(map, (int)componentCount, backing, compact.Length);
    }

    /// <summary>
    /// Builds a projection from entities in encoding order, sharing their frames and backing arrays. Reconstruction
    /// uses it to share unchanged baseline entities. A shared backing array is charged once at its full length.
    /// </summary>
    /// <exception cref="ArgumentException">A net id repeats.</exception>
    /// <exception cref="DeltaRebuildException">The result exceeds an entity, frame or byte limit.</exception>
    public static ReplicationProjection Create(IEnumerable<KeyValuePair<long, ProjectedEntity>> entities,
        DeltaRebuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var map = new Dictionary<long, ProjectedEntity>();
        long componentCount = 0;
        foreach (KeyValuePair<long, ProjectedEntity> kv in entities)
        {
            ArgumentNullException.ThrowIfNull(kv.Value, nameof(entities));
            if (!map.TryAdd(kv.Key, kv.Value))
                throw new ArgumentException($"Duplicate net id {kv.Key} in a projection.", nameof(entities));
            if (map.Count > options.MaxEntities) throw EntityLimit(map.Count, options);
            componentCount += kv.Value.Count;
            if (componentCount > options.MaxComponents) throw ComponentLimit(componentCount, options);
        }

        var distinct = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        long byteCount = 0;
        foreach (ProjectedEntity entity in map.Values)
            for (int i = 0; i < entity.Count; i++)
            {
                byte[] backing = entity[i].Backing;
                if (backing.Length > 0 && distinct.Add(backing)) byteCount += backing.Length;
            }
        if (byteCount > options.MaxRetainedPayloadBytes) throw ByteLimit(byteCount, options);
        var backings = new byte[distinct.Count][];
        distinct.CopyTo(backings);
        return new ReplicationProjection(map, (int)componentCount, backings, byteCount);
    }

    /// <summary>Every frame ordered by net id then type id, payloads as read-only views.</summary>
    internal IEnumerable<(long NetId, ushort TypeId, ReadOnlyMemory<byte> Payload)> EntriesForTest()
    {
        var netIds = new List<long>(entities.Keys);
        netIds.Sort();
        foreach (long netId in netIds)
        {
            ProjectedEntity entity = entities[netId];
            var frames = new List<ProjectedComponent>(entity.Count);
            for (int i = 0; i < entity.Count; i++) frames.Add(entity[i]);
            frames.Sort((a, b) => a.TypeId.CompareTo(b.TypeId));
            foreach (ProjectedComponent frame in frames) yield return (netId, frame.TypeId, frame.Memory);
        }
    }

    internal IEnumerable<byte[]> BackingArraysForTest() => backings;

    private static DeltaRebuildException EntityLimit(long count, DeltaRebuildOptions options) =>
        new(DeltaRebuildFailure.CapacityExceeded, $"Projection has {count} entities, limit {options.MaxEntities}.");

    private static DeltaRebuildException ComponentLimit(long count, DeltaRebuildOptions options) =>
        new(DeltaRebuildFailure.CapacityExceeded,
            $"Projection has {count} component frames, limit {options.MaxComponents}.");

    private static DeltaRebuildException ByteLimit(long count, DeltaRebuildOptions options) =>
        new(DeltaRebuildFailure.CapacityExceeded,
            $"Projection reaches {count} backing bytes, limit {options.MaxRetainedPayloadBytes}.");
}
