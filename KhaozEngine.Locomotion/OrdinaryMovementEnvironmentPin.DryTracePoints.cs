namespace KhaozEngine.Locomotion;

internal sealed partial class OrdinaryMovementEnvironmentPin : IMovementDryTracePointPin
{
    public bool HasIntrinsicDryTrace => _owner.WaterField is null;

    public bool CanClassifyDryTracePoints(in MovementMediumSweepQuery query)
    {
        AssertCurrent();
        // Absence has an intrinsic successful dry point operation in this pin. A supplied field must
        // separately certify operation-level closure, even when its sweep describes dry geometry.
        bool certified = _owner.WaterField is null || _owner.WaterField is IMovementDryTracePointProof proof &&
            proof.CanClassifyDryTracePoints(query, Frame.PhysicsOrigin);
        AssertCurrent();
        return certified;
    }
}
