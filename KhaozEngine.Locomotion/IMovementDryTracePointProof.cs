using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>Optional operation-level closure for a completely dry trace. Call only after the same query,
/// origin and generation returned Known coverage with no water contacts and dry coverage on every span.
/// True additionally certifies that every valid SampleCentreWater query in the same space/frame whose exact
/// feet point lies in that closed swept volume succeeds with Known, dry, speed scale one and no domain or
/// interval. Geometric dryness alone does not imply this guarantee. False retains ordinary point queries.</summary>
public interface IMovementDryTracePointProof : IMovementWaterField
{
    bool CanClassifyDryTracePoints(in MovementMediumSweepQuery query, Vector3 physicsOrigin);
}

internal interface IMovementDryTracePointPin
{
    bool CanClassifyDryTracePoints(in MovementMediumSweepQuery query);
}
