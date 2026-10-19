using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Optional certified queries for continuous convex values through the exact selected view.
/// Invalid inputs, unsupported geometry, stale reads and exhausted proof budgets explicitly refuse.
/// No proxy miss, incomplete traversal or partial output is a complete result.</summary>
public interface IPhysicsConvexQueries
{
    /// <summary>Clear proves the entire closed requested path, including a stationary query.
    /// Hit brackets the earliest impact and carries its proved operational witness and normal.
    /// A Hit bound is not an inclusive safe placement. Consumers independently validate request
    /// coverage, evidence, scope and lifetime. Unresolved exposes no usable prefix.</summary>
    ConvexSweepResult SweepConvexCertified(in ConvexQueryShape shape, Pose pose, Vector3 displacement,
        QueryFilter filter = default);

    /// <summary>Encloses nearest nonnegative separation with attained witnesses and raw provenance.
    /// Empty selection is complete without a target. Overlap has zero distance and is resolved
    /// separately by penetration. Normal points away from the target when separation is positive.</summary>
    ConvexDistanceResult DistanceConvex(in ConvexQueryShape shape, Pose pose, QueryFilter filter = default);

    /// <summary>Collects every relevant raw constraint for this shape, including its declared margin.
    /// Validate the whole selected view before copying. Refusal writes nothing and leaves the caller's
    /// buffer intact. Complete output leaves its unused tail intact. Known capacity shortfalls report
    /// the required count. Unsupported filters and uncertain boundaries refuse atomically.</summary>
    ConvexContactResult QueryConvexContacts(in ConvexQueryShape shape, Pose pose,
        Span<ConvexContact> destination, QueryFilter filter = default);

    /// <summary>Returns the deepest member's certified minimum translation proposal, with raw-ID tie
    /// breaking. This is not a union MTV or a placement certificate. Recovery requeries and validates
    /// every proposed placement, including after the final allowed correction.</summary>
    ConvexPenetrationResult ComputeConvexPenetration(in ConvexQueryShape shape, Pose pose,
        QueryFilter filter = default);
}
