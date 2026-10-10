using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>A seated placement proposal with its certified support and remaining climb budget. Only a valid
/// proposal commits the budget or changes the tick's position.</summary>
internal readonly record struct GroundPlacement(bool Valid, Vector3 Feet, SupportSample Support,
    double Budget, bool Pushed, Vector2 Total)
{
    internal static GroundPlacement Try(Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in SupportSample start, float startFeetY, Vector2 startAxis, Vector2 total, in GroundSeatResult seat,
        float footRadius, in MoveTuning tuning, float cosMaxSlope, double budget)
    {
        float feetY = seat.Outcome switch
        {
            SeatOutcome.Seated => Paced(start, startFeetY, seat, ref budget),
            SeatOutcome.SteepSeated => seat.FeetY,
            _ => startFeetY,
        };
        Vector2 axis = startAxis + total;
        var target = new Vector3(axis.X, feetY, axis.Y);
        // Steep placement and its clearance obligation belong to phase 3.
        if (seat.Outcome == SeatOutcome.SteepSeated)
            return new(true, target, seat.Support, budget, false, total);
        if (!Clear(world, tuning, target, startAxis, ref total, out Vector3 placed)) return default;
        if (placed == target) return new(true, target, seat.Support, budget, false, total);

        // A clearance push proposes a different axis and height. It must earn its own footing, without hovering
        // above it. Feet below a tread remain permitted for paced climbs.
        SupportSample support = FootSupport.Find(groundHeight, groundNormal, world, lease,
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

    // A touching shell is clear. An overlapping shell gets one MTV proposal and one clearance check.
    static bool Clear(IPhysicsWorld? world, in MoveTuning tuning, Vector3 target, Vector2 startAxis,
        ref Vector2 total, out Vector3 placed)
    {
        placed = target;
        if (world is null) return true;
        CapsuleShape shape = ShellGeometry.Shape(tuning);
        if (!world.ComputePenetration(shape, Pose.At(ShellGeometry.Centre(target, tuning)), out Vector3 mtv))
            return true;
        float depth = mtv.Length();
        if (depth == 0) return true;
        if (!float.IsFinite(depth)) return false;
        Vector3 pushed = target + mtv * ((depth + ShellMotion.ContactSkin) / depth);
        // The achieved prefix owns the reported axis, including its rounding. Certify and clear that exact axis.
        total += new Vector2(pushed.X - target.X, pushed.Z - target.Z);
        Vector2 reported = startAxis + total;
        pushed = new Vector3(reported.X, pushed.Y, reported.Y);
        if (world.ComputePenetration(shape, Pose.At(ShellGeometry.Centre(pushed, tuning)), out Vector3 again) &&
            again != Vector3.Zero)
            return false;
        placed = pushed;
        return true;
    }
}
