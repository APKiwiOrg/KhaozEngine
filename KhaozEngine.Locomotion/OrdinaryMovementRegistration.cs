using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Immutable installer provenance. Physical certificates identify features, this snapshot identifies owners.</summary>
public sealed partial class OrdinaryMovementRegistration
{
    internal readonly record struct Entry(StaticHandle Handle, int First, int Last, MovementSupportKey Owner,
        Vector3 Minimum, Vector3 Maximum);
    readonly Dictionary<StaticHandle, Entry[]> _entries;
    readonly HashSet<StaticHandle> _exclusions;
    readonly IPhysicsStaticGeometrySource? _staticSource;
    readonly long _staticGeneration;
    internal string SemanticDigest { get; }
    internal StaticHandle[] ExclusionSnapshot { get; }
    public string WorldId { get; }
    public IPhysicsWorld SourceWorld { get; }
    public long GeometryGeneration { get; }
    public long Generation { get; }
    public OrdinaryMovementExtent Extent { get; }
    public IReadOnlyList<StaticHandle> ExcludedStatics { get; }

    OrdinaryMovementRegistration(Builder builder, IPhysicsQueryLease lease, Dictionary<StaticHandle, Entry[]> entries,
        StaticHandle[] exclusions, string digest)
    {
        WorldId = builder.WorldId;
        SourceWorld = builder.Source;
        Extent = builder.Extent;
        GeometryGeneration = Generation = lease.GeometryGeneration;
        _staticSource = SourceWorld as IPhysicsStaticGeometrySource;
        _staticGeneration = _staticSource?.StaticGeometryGeneration ?? GeometryGeneration;
        _entries = entries;
        ExclusionSnapshot = exclusions;
        ExcludedStatics = Array.AsReadOnly(exclusions);
        _exclusions = new(exclusions);
        SemanticDigest = digest;
    }

    public static Builder CreateBuilder(string worldId, IPhysicsWorld source, OrdinaryMovementExtent extent) =>
        new(worldId, source, extent);

    internal MovementAvailability Authenticate(IPhysicsQueryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.AssertCurrent();
        if (!ReferenceEquals(lease.SourceWorld, SourceWorld)) return MovementAvailability.Invalid;
        return (_staticSource is null ? lease.GeometryGeneration == GeometryGeneration :
            _staticSource.StaticGeometryGeneration == _staticGeneration)
            ? MovementAvailability.Known : MovementAvailability.Unresolved;
    }

    internal MovementAvailability TryBind(IPhysicsQueryLease lease, StaticHandle handle,
        int featureId, out MovementSupportKey owner)
    {
        owner = default;
        MovementAvailability current = Authenticate(lease);
        if (current != MovementAvailability.Known) return current;
        if (featureId < 0 || _exclusions.Contains(handle) || !_entries.TryGetValue(handle, out Entry[]? rows))
            return MovementAvailability.Unresolved;
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
