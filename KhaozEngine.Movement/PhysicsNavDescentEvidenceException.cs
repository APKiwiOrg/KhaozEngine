using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

public enum PhysicsNavDescentEvidenceReason { CapabilityUnavailable, BackendRefused, IdentityUnavailable, RawAssociationAmbiguous, CapacityExceeded }
public sealed class PhysicsNavDescentEvidenceException(PhysicsNavDescentEvidenceReason reason, PhysicsEvidenceStatus status)
    : InvalidOperationException("Complete authenticated descent evidence is unavailable.")
{
    public PhysicsNavDescentEvidenceReason Reason { get; } = reason;
    public PhysicsEvidenceStatus Status { get; } = status;
}
