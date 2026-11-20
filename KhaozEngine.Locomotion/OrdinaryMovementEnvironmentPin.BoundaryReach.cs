using System.Numerics;

namespace KhaozEngine.Locomotion;

internal sealed partial class OrdinaryMovementEnvironmentPin : IMovementBoundaryReachPin
{
    public MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query)
    {
        AssertCurrent();
        // The trace's own validation, then a producer that opted in. Absence has an intrinsic dry trace, but it is
        // not a certificate producer, so it keeps its trace.
        if (!query.IsValid || !ValidSpace(query.Body.CurrentSpace) ||
            _owner.WaterField is not IMovementWaterBoundaryReachProof proof) return MovementWaterReach.Declined;
        Vector3 origin = Frame.PhysicsOrigin;
        MovementWaterReach reach = proof.CertifyBoundaryReach(query, origin, _owner.Identity);
        AssertCurrent();
        MovementWaterReachKind kind = reach.Kind;
        if (kind == MovementWaterReachKind.Empty) return MovementWaterReach.Empty;
        if (kind != MovementWaterReachKind.OneFreeSurface) return MovementWaterReach.Declined;
        float surface = OrdinaryMovementEnvironment.LocalSurface(reach.SurfaceY, origin.Y);
        return float.IsFinite(surface) ? MovementWaterReach.FreeSurface(surface) : MovementWaterReach.Declined;
    }
}
