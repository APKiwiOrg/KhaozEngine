namespace KhaozEngine.NetWorld;

/// <summary>World configuration rules refused before a host or client starts.</summary>
public enum WorldConfigurationRefusal
{
    /// <summary>Refuses a sharded cell origin with a nonfinite planar coordinate.</summary>
    CellOriginNonfinite,
    /// <summary>Refuses a finite worst frame-local planar magnitude above the float32 divergence ceiling.</summary>
    FramePlanarMagnitudeExceeded,
    /// <summary>Refuses an interest radius greater than the home cell's ghost overlap margin.</summary>
    InterestOutsideOverlap,
    /// <summary>Refuses frame anchoring with a physics world that cannot rebase.</summary>
    PhysicsCannotRebase,
    /// <summary>Refuses an unreliable replication opt-in without stream options.</summary>
    ReplicationStreamMissing,
    /// <summary>Refuses receiving the server tick alongside unreliable delta replication, which carries no tick.</summary>
    ServerTickWithUnreliableReplication,
    /// <summary>Refuses unreliable delta replication without the existing delta switch.</summary>
    UnreliableWithoutDelta,
    /// <summary>Refuses a nonfinite worst frame-local planar magnitude that exceeds the divergence ceiling.</summary>
    FramePlanarMagnitudeNonfinite,
}
