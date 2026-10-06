using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>A capsule constraint in the physics frame. Normal points away from the contacted body.
/// Separation is positive outside and negative while penetrating. Provenance is source-local.</summary>
public readonly record struct CapsuleContact(Vector3 Normal, float Separation, bool Dynamic,
    int BodyHandle, int ChildIndex, int FeatureId);

/// <summary>A complete query or an explicit refusal. An incomplete result writes nothing to the caller's
/// buffer. RequiredCapacity is the needed output size when known, or zero for an uncertifiable query.
/// CertifiedErrorMetres bounds the reported separation's numerical budget in the supported frame.</summary>
public readonly record struct CapsuleContactResult(bool Complete, int Written, int RequiredCapacity,
    float CertifiedErrorMetres);

/// <summary>Optional complete capsule-contact queries, including nonpenetrating contacts within a margin.</summary>
public interface IPhysicsCapsuleContacts
{
    /// <summary>Collects all relevant constraints, without a deepest-only or reduced-manifold prefix.
    /// Numerically uncertain boundary contacts may be included within the returned error budget.
    /// Uncertifiable geometry/work limits return incomplete. Unsupported filters are explicitly refused.</summary>
    CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float maxSeparationMetres,
        Span<CapsuleContact> destination, QueryFilter filter = default);
}
