using System.Numerics;

namespace KhaozEngine.Locomotion;

public static partial class ExplicitCharacterMovement
{
    static MovementAvailability TryStepUp(in MovementBodyQuery start, Vector2 horizontal, in MoveTuning tuning,
        in WaterTraversalPolicy water, MovementQueryLease queries, out MovementSupportPlacement? reached)
    {
        reached = null;
        if (horizontal == Vector2.Zero || tuning.StepHeight == 0) return MovementAvailability.Known;
        Vector3 target = start.Centre + new Vector3(horizontal.X, 0, horizontal.Y);
        if (!MovementEnvironmentValidation.Finite(target)) return MovementAvailability.Invalid;
        var goal = new MovementBodyQuery(target, start.Radius, start.HalfHeight, start.CurrentSpace, start.CurrentSupport);
        var request = new MovementSupportRequest(goal, tuning.StepHeight, 0, tuning.MaxSlopeRadians,
            new(start.CurrentSpace, start.Feet));
        MovementAvailability status = MovementSupportResolver.Select(request, queries, out var candidate);
        if (status != MovementAvailability.Known || candidate is null) return status;
        MovementSupportPlacement support = candidate.Value;
        if (support.Centre.Y <= start.Centre.Y) return MovementAvailability.Known;

        // Use the actual eligible support height, not a full StepHeight rise. A full rise can hit
        // a ceiling even though the supported destination and its exact approach both fit.
        Vector3 above = new(start.Centre.X, support.Centre.Y, start.Centre.Z);
        status = Move(start, above - start.Centre, queries, water, out Vector3 raised, out _);
        if (status != MovementAvailability.Known) return status;
        if (raised != above) return MovementAvailability.Known;
        var airborne = new MovementBodyQuery(raised, start.Radius, start.HalfHeight, start.CurrentSpace, null);
        status = Move(airborne, support.Centre - raised, queries, water, out Vector3 end, out _);
        if (status != MovementAvailability.Known) return status;
        if (end != support.Centre) return MovementAvailability.Known;
        queries.AssertCurrent();
        reached = support;
        return MovementAvailability.Known;
    }
}
