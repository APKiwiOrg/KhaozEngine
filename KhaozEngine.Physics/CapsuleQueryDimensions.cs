using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Exact represented dimensions for a geometric capsule query, without a persistent shape identity.</summary>
public readonly record struct CapsuleQueryDimensions(float Radius, float Length);

/// <summary>Optional value-input counterpart of the complete capsule sweep and contact capabilities.
/// Selected-view restrictions, query lifetime, geometry, filters and refusal rules are identical.</summary>
public interface IPhysicsCapsuleGeometryQueries : IPhysicsCapsuleSweep, IPhysicsCapsuleContacts
{
    /// <summary>Certifies the complete path using the exact radius and cylindrical length supplied by the caller.</summary>
    CapsuleSweepResult SweepCapsuleCertified(in CapsuleQueryDimensions capsule, Pose pose, Vector3 displacement,
        QueryFilter filter = default);

    /// <summary>Collects complete constraints for the exact dimensions without constructing a persistent shape.</summary>
    CapsuleContactResult QueryCapsuleContacts(in CapsuleQueryDimensions capsule, Pose pose, float maxSeparationMetres,
        Span<CapsuleContact> destination, QueryFilter filter = default);
}
