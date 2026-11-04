using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

public enum PhysicsNavDescentEvidenceReason { CapabilityUnavailable, BackendRefused, IdentityUnavailable, RawAssociationAmbiguous, CapacityExceeded, IndexStorageExceeded, DependencyMissing }
public sealed class PhysicsNavDescentEvidenceException(PhysicsNavDescentEvidenceReason reason, PhysicsEvidenceStatus status)
    : InvalidOperationException("Complete authenticated descent evidence is unavailable.")
{
    internal PhysicsNavDescentEvidenceException(long requestedBytes, long maximumBytes)
        : this(PhysicsNavDescentEvidenceReason.IndexStorageExceeded, PhysicsEvidenceStatus.CapacityExceeded)
    { RequestedIndexBytes = requestedBytes; MaximumIndexBytes = maximumBytes; }
    public PhysicsNavDescentEvidenceReason Reason { get; } = reason;
    public PhysicsEvidenceStatus Status { get; } = status;
    public long? RequestedIndexBytes { get; }
    public long? MaximumIndexBytes { get; }
}
