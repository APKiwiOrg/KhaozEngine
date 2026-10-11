using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>A seated placement proposal with its certified support and remaining climb budget. Only a valid
/// proposal commits the budget or changes the tick's position.</summary>
internal readonly partial record struct GroundPlacement(bool Valid, Vector3 Feet, SupportSample Support,
    double Budget, bool Pushed, Vector2 Total)
{
    /// <summary>Places a seat. A steep seat is placed as seated unless <paramref name="clearSteep"/> asks for its
    /// clearance, which a slide does. A steep seat takes no push, because a push off the face would leave the feet
    /// above it.</summary>
    internal static GroundPlacement Try(ContactQueryContext queries,
        in SupportSample start, float startFeetY, Vector2 startAxis, Vector2 total, in GroundSeatResult seat,
        float footRadius, in MoveTuning tuning, float cosMaxSlope, double budget, bool clearSteep = false)
    {
        Vector2 axis = startAxis + total;
        var route = new ContactMotionRoute(queries, new(axis.X, startFeetY, axis.Y));
        return Try(queries, start, startFeetY, startAxis, total, seat, footRadius, tuning, cosMaxSlope,
            budget, ref route, clearSteep);
    }

    internal static GroundPlacement Try(ContactQueryContext queries,
        in SupportSample start, float startFeetY, Vector2 startAxis, Vector2 total, in GroundSeatResult seat,
        float footRadius, in MoveTuning tuning, float cosMaxSlope, double budget, ref ContactMotionRoute route,
        bool clearSteep = false)
    {
        float feetY = seat.Outcome switch
        {
            SeatOutcome.Seated => Paced(start, startFeetY, seat, ref budget),
            SeatOutcome.SteepSeated => seat.FeetY,
            _ => startFeetY,
        };
        Vector2 axis = startAxis + total;
        var target = new Vector3(axis.X, feetY, axis.Y);
        if (!route.Append(target)) return default;
        if (seat.Outcome == SeatOutcome.SteepSeated)
            return clearSteep && Overlaps(queries, tuning, target)
                ? default
                : new(true, target, seat.Support, budget, false, total);
        if (!Clear(queries, tuning, target, startAxis, ref total, ref route, out Vector3 placed)) return default;
        if (placed == target) return new(true, target, seat.Support, budget, false, total);

        // A clearance push proposes a different axis and height. It must earn its own footing, without hovering
        // above it. Feet below a tread remain permitted for paced climbs.
        SupportSample support = queries.FindSupport(
            GroundSeat.Query(new Vector2(placed.X, placed.Z), start, startFeetY, footRadius, tuning, cosMaxSlope));
        if (support.Status != SupportStatus.Walkable || (double)placed.Y > (double)support.Height + support.HeightError)
            return default;
        double raised = Math.Max(0, (double)placed.Y - target.Y);
        if (raised > budget) return default;
        return new(true, placed, support, budget - raised, true, total);
    }

    // The step part plus the unpaid lag is paid from this attempt's copy of the tick budget.
    static float Paced(in SupportSample start, float startFeetY, in GroundSeatResult seat, ref double budget)
    {
        double lag = start.Status == SupportStatus.Walkable ? Math.Max(0, (double)start.Height - startFeetY) : 0;
        double wanted = (double)seat.StepPart + lag;
        if (!(wanted > 0)) return seat.FeetY;
        double paid = Math.Min(wanted, budget);
        budget -= paid;
        return paid < wanted ? NotAbove((double)seat.FeetY - (wanted - paid)) : seat.FeetY;
    }

    /// <summary>The largest float not above <paramref name="value"/>. Paced feet round down, so a climb paid from
    /// a budget that is not a float never exceeds it.</summary>
    internal static float NotAbove(double value)
    {
        float rounded = (float)value;
        return rounded > value ? MathF.BitDecrement(rounded) : rounded;
    }

    /// <summary>The smallest float not below <paramref name="value"/>, the mirror of <see cref="NotAbove"/>. A step
    /// part rounds up, so the climb it charges is never less than the rise.</summary>
    internal static float NotBelow(double value)
    {
        float rounded = (float)value;
        return rounded < value ? MathF.BitIncrement(rounded) : rounded;
    }

    // A touching shell is clear.
    static bool Overlaps(ContactQueryContext queries, in MoveTuning tuning, Vector3 target) =>
        queries.HasShell &&
        queries.PenetrationShell(ShellGeometry.Shape(tuning), ShellGeometry.Centre(target, tuning),
            out Vector3 mtv) &&
        mtv != Vector3.Zero;

    // A touching shell is clear. An overlapping shell gets one MTV proposal and one clearance check.
    static bool Clear(ContactQueryContext queries, in MoveTuning tuning, Vector3 target, Vector2 startAxis,
        ref Vector2 total, ref ContactMotionRoute route, out Vector3 placed)
    {
        placed = target;
        if (!queries.HasShell) return true;
        CapsuleShape shape = ShellGeometry.Shape(tuning);
        if (!queries.PenetrationShell(shape, ShellGeometry.Centre(target, tuning), out Vector3 mtv))
            return true;
        float depth = mtv.Length();
        if (depth == 0) return true;
        if (!float.IsFinite(depth)) return false;
        Vector3 pushed = target + mtv * ((depth + ShellMotion.ContactSkin) / depth);
        // The achieved prefix owns the reported axis, including its rounding. Certify and clear that exact axis.
        total += new Vector2(pushed.X - target.X, pushed.Z - target.Z);
        Vector2 reported = startAxis + total;
        pushed = new Vector3(reported.X, pushed.Y, reported.Y);
        if (queries.PenetrationShell(shape, ShellGeometry.Centre(pushed, tuning), out Vector3 again) &&
            again != Vector3.Zero)
            return false;
        if (!route.Append(pushed)) return false;
        placed = pushed;
        return true;
    }
}
