using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Admission-time footprint and shell placement, without tick clocks, pacing or input effects.</summary>
internal static class ContactPlacement
{
    internal static void Validate(ContactQueryContext queries, Vector3 feet, in MoveTuning tuning)
    {
        queries.ValidatePlacement(feet);
        if (!Clear(queries, feet, tuning))
            throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.PlacementRefused);
    }

    internal static SupportSample Find(ContactQueryContext queries, Vector3 feet, in MoveTuning tuning,
        float rise, float drop, out MovementContactSupportBinding? binding) =>
        queries.FindSupport(new(new(feet.X, feet.Z), feet.Y, new GroundCoreSettings().FootRadiusFraction * tuning.CapsuleRadius,
            rise, drop, MathF.Cos(tuning.MaxSlopeRadians)), out binding);

    internal static bool TrySeat(ContactQueryContext queries, Vector3 feet, in SupportSample support,
        in MoveTuning tuning, out Vector3 placed)
    {
        placed = feet;
        if (support.Status != SupportStatus.Walkable) return false;
        Vector3 target = feet with { Y = support.Height };
        if (!Approach(queries, feet, target, tuning)) return false;
        var route = new ContactMotionRoute(queries, feet);
        var seat = new GroundSeatResult(SeatOutcome.Seated, support.Height, support, 0, Vector3.Zero);
        GroundPlacement placement = GroundPlacement.Try(queries, default, feet.Y, new(feet.X, feet.Z),
            Vector2.Zero, seat, new GroundCoreSettings().FootRadiusFraction * tuning.CapsuleRadius,
            tuning, MathF.Cos(tuning.MaxSlopeRadians),
            double.PositiveInfinity, ref route);
        if (!placement.Valid || !Approach(queries, target, placement.Feet, tuning)) return false;
        placed = placement.Feet;
        return true;
    }

    internal static bool Clear(ContactQueryContext queries, Vector3 feet, in MoveTuning tuning) =>
        !queries.PenetrationShell(ShellGeometry.Shape(tuning), ShellGeometry.Centre(feet, tuning), out Vector3 mtv) ||
        mtv == Vector3.Zero;

    static bool Approach(ContactQueryContext queries, Vector3 from, Vector3 to, in MoveTuning tuning)
    {
        Vector3 delta = to - from;
        float distance = delta.Length();
        if (!float.IsFinite(distance)) throw new ContactQueryFailure(MovementAvailability.Unresolved);
        return distance == 0 || !queries.SweepShell(ShellGeometry.Shape(tuning), ShellGeometry.Centre(from, tuning),
            delta / distance, distance, out _, QueryFilter.StaticsOnly);
    }
}
