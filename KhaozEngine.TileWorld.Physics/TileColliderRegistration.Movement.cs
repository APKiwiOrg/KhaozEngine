using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileColliderRegistration
{
    /// <summary>Captures fixed installer owners and analytic-ground exclusions in a fresh physical read.
    /// Apply runtime batches through the returned registration, within its fixed declared extent.</summary>
    public OrdinaryMovementRegistration CreateMovementRegistration(string worldId)
    {
        var builder = CreateMovementRegistrationBuilder(worldId);
        if (_world is not IPhysicsQueryLeaseSource source)
            throw new NotSupportedException("Movement registration requires a certified physical read source.");
        using var lease = source.AcquireQueryReadLease();
        return builder.Build(lease);
    }

    /// <summary>Starts a source-bound builder with tile owners and ground exclusions. Add other installed
    /// statics and reserve runtime volume in the combined extent, then build under a fresh source read.</summary>
    public OrdinaryMovementRegistration.Builder CreateMovementRegistrationBuilder(string worldId,
        OrdinaryMovementExtent? combinedExtent = null)
    {
        if (_provenance is null || !string.Equals(worldId, _worldId, StringComparison.Ordinal))
            throw new ArgumentException("The movement registration requires the captured water world's identity.", nameof(worldId));
        for (int i = 0; i < _removed.Length; i++)
            if (_removed[i]) throw new InvalidOperationException("Removed tile statics cannot be registered for movement.");
        var builder = OrdinaryMovementRegistration.CreateBuilder(worldId, _world, combinedExtent ?? _extent);
        for (int i = 0; i < _handles.Length; i++)
        {
            TileColliderProvenance row = _provenance[i];
            builder.Add(_handles[i], 0, int.MaxValue, new(worldId, row.OwnerLocalId), row.AbsoluteMinimum, row.AbsoluteMaximum);
        }
        foreach (StaticHandle handle in _groundHandles) builder.Exclude(handle);
        return builder;
    }
}
