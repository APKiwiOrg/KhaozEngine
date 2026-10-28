using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Fixed authored provenance and immutable runtime publications. Physical certificates identify
/// features, while this registration identifies canonical owners under the same physical interval.</summary>
public sealed partial class OrdinaryMovementRegistration
{
    internal readonly record struct Entry(StaticHandle Handle, int First, int Last, MovementSupportKey Owner,
        Vector3 Minimum, Vector3 Maximum);
    readonly Dictionary<StaticHandle, Entry[]> _entries;
    readonly HashSet<StaticHandle> _exclusions;
    readonly IPhysicsStaticGeometrySource? _staticSource;
    readonly HashSet<MovementSupportKey> _authoredOwners;
    OrdinaryRuntimeStaticState _runtime;
    internal string SemanticDigest { get; }
    internal StaticHandle[] ExclusionSnapshot { get; }
    public string WorldId { get; }
    public IPhysicsWorld SourceWorld { get; }
    /// <summary>The physical read generation at installation. Runtime edits never change it.</summary>
    public long GeometryGeneration { get; }
    /// <summary>Live process-local registration revision. Each successful nonempty runtime batch advances it once.</summary>
    public long Generation => Volatile.Read(ref _runtime).Revision;
    internal bool HasRuntimeStatics => Volatile.Read(ref _runtime).Owners.Count != 0;
    /// <summary>The fixed absolute installation extent, including any reserved runtime volume.</summary>
    public OrdinaryMovementExtent Extent { get; }
    /// <summary>The fixed authored selection exclusions. Runtime handles are never appended.</summary>
    public IReadOnlyList<StaticHandle> ExcludedStatics { get; }

    OrdinaryMovementRegistration(Builder builder, IPhysicsQueryLease lease, Dictionary<StaticHandle, Entry[]> entries,
        StaticHandle[] exclusions, string digest)
    {
        WorldId = builder.WorldId;
        SourceWorld = builder.Source;
        Extent = builder.Extent;
        GeometryGeneration = lease.GeometryGeneration;
        _staticSource = SourceWorld as IPhysicsStaticGeometrySource;
        _runtime = new(_staticSource?.StaticGeometryGeneration ?? GeometryGeneration, GeometryGeneration);
        _entries = entries;
        _authoredOwners = [];
        foreach (Entry[] rows in entries.Values)
            foreach (Entry row in rows) _authoredOwners.Add(row.Owner);
        ExclusionSnapshot = exclusions;
        ExcludedStatics = Array.AsReadOnly(exclusions);
        _exclusions = new(exclusions);
        SemanticDigest = digest;
    }

    public static Builder CreateBuilder(string worldId, IPhysicsWorld source, OrdinaryMovementExtent extent) =>
        new(worldId, source, extent);

    internal MovementAvailability Authenticate(IPhysicsQueryLease lease)
        => Authenticate(lease, Volatile.Read(ref _runtime));

    MovementAvailability Authenticate(IPhysicsQueryLease lease, OrdinaryRuntimeStaticState runtime)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.AssertCurrent();
        if (!ReferenceEquals(lease.SourceWorld, SourceWorld)) return MovementAvailability.Invalid;
        return !runtime.Poisoned && (_staticSource is null ? lease.GeometryGeneration == GeometryGeneration :
            _staticSource.StaticGeometryGeneration == runtime.ExpectedStaticRevision)
            ? MovementAvailability.Known : MovementAvailability.Unresolved;
    }

    internal MovementAvailability TryBind(IPhysicsQueryLease lease, StaticHandle handle,
        int featureId, out MovementSupportKey owner)
    {
        owner = default;
        OrdinaryRuntimeStaticState runtime = Volatile.Read(ref _runtime);
        MovementAvailability current = Authenticate(lease, runtime);
        if (current != MovementAvailability.Known) return current;
        if (featureId < 0 || _exclusions.Contains(handle))
            return MovementAvailability.Unresolved;
        if (!_entries.TryGetValue(handle, out Entry[]? rows))
        {
            if (!runtime.Handles.TryGetValue(handle, out Entry row)) return MovementAvailability.Unresolved;
            owner = row.Owner;
            return MovementAvailability.Known;
        }
        bool found = false;
        foreach (Entry row in rows)
        {
            if (featureId < row.First || featureId > row.Last) continue;
            if (found && row.Owner != owner) { owner = default; return MovementAvailability.Unresolved; }
            owner = row.Owner;
            found = true;
        }
        return found ? MovementAvailability.Known : MovementAvailability.Unresolved;
    }
}
