using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Optional capture certificate for the water boundary question. A SurfaceSwimmer is constrained only by
/// closed water and by unequal free levels in one span, and a WadeOnly body only by reaching its enter line, so a
/// path whose reach is empty or holds one free level can be answered from capture facts without the coverage
/// trace. An implementer makes four guarantees for every answer other than
/// <see cref="MovementWaterReachKind.Declined"/>, under the same query, origin, identity and generation:
/// <list type="number">
/// <item>every contact its <see cref="IMovementWaterField.TraceWater"/> can report for the query is free surface,</item>
/// <item>every such contact carries its own column's top as <see cref="MovementWaterInterval.NominalSurfaceY"/>,</item>
/// <item>every such contact belongs to a column the certificate counted, and</item>
/// <item>every domain of the capture has one level.</item>
/// </list>
/// It answers only when the capture has at most <see cref="MovementQueryLease.MaxWaterDomainsPerLease"/> domains.
/// <see cref="MovementWaterReachKind.OneFreeSurface"/> requires every counted column's surface to have identical bits.
/// A producer that cannot guarantee all four must not implement this interface. A wrapper that forwards only the
/// inner field's trace hides the certificate, and its leases keep the full trace. Decline whenever unsure, since
/// a declined query runs the full coverage trace unchanged.</summary>
public interface IMovementWaterBoundaryReachProof : IMovementWaterField
{
    MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity);
}

internal interface IMovementBoundaryReachPin
{
    /// <summary>The producer's reach certificate in this pin's frame. A surface is converted exactly as the pin's
    /// trace converts every contact's nominal surface, so a Known trace's levels equal it bit for bit.</summary>
    MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query);
}
