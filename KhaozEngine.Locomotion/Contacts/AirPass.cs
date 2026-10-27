using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>How an air tick ended. <see cref="Landed"/> seats the feet on walkable support. <see cref="Sliding"/>
/// seats them on steep support, which never grounds the body.</summary>
internal enum AirOutcome : byte { Airborne, Landed, Sliding }

/// <summary>One air tick. <see cref="VerticalVelocity"/> is the speed after gravity and the tick's contacts, zero
/// after a landing, and on a slide the speed at the hand-off, without the gravity of the time left.
/// <see cref="Achieved"/> is the horizontal move that happened. <see cref="Remaining"/> is the horizontal displacement
/// the tick planned after its landing substep, left for the ground core, and zero without a landing or a slide.
/// <see cref="FallSpeedAtContact"/> is the vertical speed at the landing substep, positive downward, and zero without
/// one. <see cref="Blocked"/> says a shell contact or a terrain wall removed part of the move.</summary>
internal readonly record struct AirStepResult(Vector3 Feet, float VerticalVelocity, Vector2 Achieved,
    Vector2 Remaining, AirOutcome Outcome, SupportSample Support, float FallSpeedAtContact, bool Blocked)
{
    /// <summary>The horizontal velocity after the tick's contacts.</summary>
    internal Vector2 HorizontalVelocity { get; init; }

    /// <summary>The seconds of the tick after its landing or slide substep, for the next mode to run. Zero without
    /// one.</summary>
    internal float RemainingTime { get; init; }

    internal bool MediumHandoff { get; init; }
    internal bool HorizontalPlanPreserved { get; init; }
    internal bool DescendingAtContact { get; init; }

    /// <summary>The climb budget left after a landing's paced rise, in metres.</summary>
    internal double ClimbBudget { get; init; }
}

/// <summary>Moves a body without footing for one tick: gravity, the shell's flight in substeps, and landing on
/// footprint support while descending. Only the shell blocks, and the legs pass ledge edges.</summary>
internal static class AirPass
{
    // A bound on one tick's substeps, so a huge displacement throws rather than running unbounded.
    internal const int MaxSubsteps = 1024;

    static readonly SupportSample NoSupport =
        new(SupportStatus.None, float.NaN, float.NaN, Vector3.Zero, null, -1, Vector3.Zero);

    /// <summary>Moves a body whose feet are at <paramref name="feet"/>, in the world's local frame. Gravity applies
    /// first, clamped at <see cref="MoveTuning.MaxFallSpeed"/>. The tick's displacement then runs in substeps of at
    /// most half the capsule radius, and every shell contact removes the velocity component into it. After each
    /// substep that ends descending, the ground core's support query runs at the new axis, from the substep's lowest
    /// feet to <see cref="MoveTuning.StepHeight"/> above its highest. Walkable support in it lands the body through the
    /// ground core's placement, the step up a standing body would take, with the rise above the substep's start feet
    /// paced by <see cref="MoveTuning.MaxStepClimbSpeed"/>. Steep support within the travelled span starts a
    /// slide. Analytic terrain at the new axis more than <see cref="MoveTuning.StepHeight"/> above the feet, or steep
    /// and above them, is a wall. <paramref name="velocity"/> is the tick's horizontal velocity and
    /// <paramref name="verticalVelocity"/> the carried one before gravity. <paramref name="tractionSlopeRadians"/> is
    /// the tick's traction gate. The <paramref name="queries"/> context enforces the <see cref="FootSupport"/>
    /// contract. <paramref name="climbBudget"/> is the climb the tick may still pay, in metres. Null means a whole
    /// tick's <c>MaxStepClimbSpeed * dt</c>. <paramref name="clampXz"/> is the play-area clamp. It cuts every
    /// substep's horizontal move before the sweep, a terrain wall's rerun included, and every slide of the shell
    /// along a contact, so no substep that starts inside the bound ends outside it. The clamp is not a contact, so it
    /// leaves the velocity alone.</summary>
    internal static AirStepResult Step(Vector3 feet, Vector2 velocity, float verticalVelocity, float dt,
        float gravity, in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians,
        ContactQueryContext queries, double? climbBudget = null, Func<float, float, Vector2>? clampXz = null) =>
        StepWithMedium(feet, velocity, verticalVelocity, dt, gravity, tuning, settings, tractionSlopeRadians,
            queries, climbBudget, clampXz);

    /// <summary>As <see cref="Step"/>, with <paramref name="medium"/> classifying footing and owner changes and
    /// <paramref name="verticalDisplacement"/>, when given, replacing the integrated vertical move. A
    /// <paramref name="buoyant"/> body rides a waterline rather than an arc, so its rising substeps also run the
    /// support query and contact walkable support above their highest feet, clipping to a submerged bed or
    /// landing when it allows footing.</summary>
    internal static AirStepResult StepWithMedium(Vector3 feet, Vector2 velocity, float verticalVelocity, float dt,
        float gravity, in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians,
        ContactQueryContext queries, double? climbBudget = null,
        Func<float, float, Vector2>? clampXz = null, ContactMediumPolicy? medium = null,
        float? verticalDisplacement = null, bool buoyant = false)
    {
        Validate(feet, velocity, verticalVelocity, dt, gravity, tuning, settings, tractionSlopeRadians);
        double budget = climbBudget ?? Budget(tuning, dt);
        if (!(budget >= 0))
            throw new ArgumentOutOfRangeException(nameof(climbBudget), "The climb budget must not be negative.");
        float maxFallSpeed = tuning.MaxFallSpeed;
        float vy = verticalVelocity - gravity * dt;
        if (vy < -tuning.MaxFallSpeed) vy = -tuning.MaxFallSpeed;
        var displacement = new Vector3(velocity.X * dt, verticalDisplacement ?? vy * dt, velocity.Y * dt);
        int substeps = Substeps(displacement, tuning);
        float stepTime = dt / substeps;
        float footRadius = settings.FootRadiusFraction * tuning.CapsuleRadius;
        float cosMaxSlope = MathF.Cos(tractionSlopeRadians);

        // A shell that cannot be freed does not move or fall, as a held ground tick does not move.
        Vector3 start = ShellMotion.RecoverWithQueries(queries, feet, tuning, out bool cleared);
        queries.RequireRecoveryAccepted();
        if (!cleared)
            return new(feet, 0f, Vector2.Zero, Vector2.Zero, AirOutcome.Airborne, NoSupport, 0f, true)
            {
                ClimbBudget = budget,
            };

        // While nothing has deviated from the plan, the move is the planned prefix itself, so free flight is exact
        // however many substeps it takes. After a contact the rest of the tick flies at the changed velocity.
        var current = new Vector3(velocity.X, vy, velocity.Y);
        Vector3 moved = Vector3.Zero, planned = Vector3.Zero;
        bool onPlan = true, blocked = false;
        for (int i = 0; i < substeps; i++)
        {
            Vector3 next = i == substeps - 1 ? displacement : displacement * ((float)(i + 1) / substeps);
            Vector3 move = onPlan ? next - planned : current * stepTime;
            planned = next;
            if (move == Vector3.Zero) continue;

            Vector3 from = start + moved;
            Vector3 before = current;
            var origin = new Vector2(from.X, from.Z);
            if (Bounded(origin, ref move, clampXz)) onPlan = false;
            ShellFlight flight = ShellMotion.FlyWithQueries(queries, from, move, ref current, tuning, cosMaxSlope, clampXz);
            // Analytic terrain has no shell to meet. As in the ground core, terrain at the new axis more than
            // StepHeight above the substep's highest feet is a wall, and so is steep terrain above them, so the axis
            // never sinks into a face it cannot stand on. The substep reruns without its component into the wall,
            // then without its horizontal part if the wall still stops it.
            if (TerrainWall(queries, from, flight, tuning, cosMaxSlope) is Vector2 wall)
            {
                Vector2 alongMove = Along(new Vector2(move.X, move.Z), wall);
                PlayArea.Clamp(origin, ref alongMove, clampXz);
                Vector2 alongVelocity = Along(new Vector2(before.X, before.Z), wall);
                current = new Vector3(alongVelocity.X, before.Y, alongVelocity.Y);
                flight = ShellMotion.FlyWithQueries(queries, from, new Vector3(alongMove.X, move.Y, alongMove.Y), ref current,
                    tuning, cosMaxSlope, clampXz);
                if (TerrainWall(queries, from, flight, tuning, cosMaxSlope) is not null)
                {
                    current = new Vector3(0f, before.Y, 0f);
                    flight = ShellMotion.FlyWithQueries(queries, from, new Vector3(0f, move.Y, 0f), ref current, tuning,
                        cosMaxSlope, clampXz);
                }
                blocked = true;
            }
            if (flight.Blocked || flight.Achieved != move || current != before) onPlan = false;
            blocked |= flight.Blocked;
            moved = onPlan ? next : moved + flight.Achieved;
            float remainingTime = stepTime * (substeps - i - 1);
            bool rising = current.Y > 0f;
            if (rising)
            {
                if (medium?.ChangesOwner(start + moved, current.Y, NoSupport, false) == true)
                    return Handoff(start + moved, NoSupport, AirOutcome.Airborne, remainingTime);
                if (!buoyant) continue;
            }

            Vector3 at = start + moved;
            var route = new ContactMotionRoute(queries, from + flight.Achieved);
            if (!route.Append(at))
                throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.Blocked);
            float high = MathF.Max(from.Y + flight.Highest, at.Y);
            float low = MathF.Min(from.Y + flight.Lowest, at.Y);
            var axis = new Vector2(at.X, at.Z);
            SupportSample support = queries.FindSupport(
                new FootSupportQuery(axis, high, footRadius, tuning.StepHeight, MathF.Max(0f, high - low),
                    cosMaxSlope));
            // A buoyant body settles on its waterline, so the sign of its vertical speed says nothing about its legs.
            // Rising, it takes only walkable support that rose above its highest feet. That contact must clip the
            // legs even before the water allows footing. A bed it rose off seats nothing.
            if (rising && !(support.Status == SupportStatus.Walkable &&
                    (double)support.Height - support.HeightError > high))
                continue;
            var remaining = new Vector2(displacement.X - next.X, displacement.Z - next.Z);
            var horizontal = new Vector2(current.X, current.Z);
            if (support.Status == SupportStatus.Walkable && medium?.AllowsFooting(at, support) == false &&
                (double)support.Height + support.HeightError >= low)
            {
                // A submerged bed clips descent and a bank rising above the feet through the same proved
                // placement as a landing. This is a fluid floor, so it grants no footing and pays no land climb.
                // Buoyancy spends the remainder from the contact-clipped carry.
                // Keep the fluid contact outside the bed after centre mapping rounds. This is one representable
                // clearance ordinate, not a climb or a second placement skin, and earns the placement proof.
                var seat = new GroundSeatResult(SeatOutcome.Seated, MathF.BitIncrement(support.Height), support, 0, Vector3.Zero);
                GroundPlacement bed = GroundPlacement.Try(queries, NoSupport, high, axis, Vector2.Zero,
                    seat, footRadius, tuning, cosMaxSlope, budget, ref route);
                // An unraised fallback cannot clear a bank above the whole travelled span. Refuse this tick
                // rather than publish swimming feet inside that bank when the required lift has no proof.
                if (!bed.Valid && (double)support.Height - support.HeightError > high)
                    throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.Blocked);
                // A refused seat clips at the clear unraised substep, as a land landing does.
                // Continuing the descent would let the legs pass through an analytic bed.
                return new(bed.Valid ? bed.Feet : at, 0,
                    Achieved(feet, start, moved + (bed.Valid ? new Vector3(bed.Total.X, 0, bed.Total.Y) : Vector3.Zero)),
                    remaining, AirOutcome.Airborne, NoSupport, 0, blocked || bed.Valid && bed.Pushed)
                {
                    HorizontalVelocity = horizontal,
                    RemainingTime = remainingTime,
                    ClimbBudget = bed.Valid ? bed.Budget : budget,
                    MediumHandoff = true,
                    DescendingAtContact = !rising,
                };
            }
            if (support.Status == SupportStatus.Walkable && medium?.AllowsFooting(at, support) != false)
            {
                // The seat goes through the ground core's placement, so the feet never seat into a shell overlap and
                // a rise above the substep's start feet is paced like a step up. The ground core pays the rest. The
                // paced feet are min(support, start feet + budget), never below the start feet, so the lag stays
                // under StepHeight less the budget and the next ground tick's band still holds the support. A
                // clearance push can only lower them until the shell clears, which keeps them over the support.
                double rise = (double)support.Height - high;
                var seat = new GroundSeatResult(SeatOutcome.Seated, support.Height, support,
                    rise > 0 ? GroundPlacement.NotBelow(rise) : 0f, Vector3.Zero);
                GroundPlacement placement = GroundPlacement.Try(queries, NoSupport,
                    high, axis, Vector2.Zero, seat, footRadius, tuning, cosMaxSlope, budget, ref route);
                if (!placement.Valid)
                {
                    // A seat that fails clearance lands unraised at the substep's end feet while the support is
                    // still within StepHeight above them. The sweep left the shell clear there, and the ground core
                    // pays the climb as it pays any lag. Falling on would let the legs pass the support, and analytic
                    // terrain has no shell to stop them.
                    double above = (double)support.Height - at.Y;
                    if (!(above >= -support.HeightError && above <= tuning.StepHeight)) continue;
                    return new(at, 0f, Achieved(feet, start, moved), remaining, AirOutcome.Landed, support,
                        MathF.Max(0f, -current.Y), blocked)
                    {
                        HorizontalVelocity = horizontal,
                        RemainingTime = remainingTime,
                        MediumHandoff = medium?.ChangesOwner(at, 0, support, true) == true,
                        HorizontalPlanPreserved = onPlan && start.X == feet.X && start.Z == feet.Z,
                        ClimbBudget = budget,
                    };
                }
                Vector3 push = new(placement.Total.X, 0f, placement.Total.Y);
                return new(placement.Feet, 0f, Achieved(feet, start, moved + push), remaining, AirOutcome.Landed,
                    placement.Support, MathF.Max(0f, -current.Y), blocked || placement.Pushed)
                {
                    HorizontalVelocity = horizontal,
                    RemainingTime = remainingTime,
                    MediumHandoff = medium?.ChangesOwner(placement.Feet, 0, placement.Support, true) == true,
                    HorizontalPlanPreserved = onPlan && !placement.Pushed && start.X == feet.X && start.Z == feet.Z,
                    ClimbBudget = placement.Budget,
                };
            }
            // Steep support never lifts the body: it starts a slide only within the span the feet travelled. The
            // slide integrates its own gravity over the rest of the tick, so the hand-off gives back the gravity this
            // tick integrated for that time. The substep was descending, so the hand-off never rises.
            if (support.Status == SupportStatus.Steep && medium?.AllowsFooting(at, support) != false &&
                (double)support.Height - support.HeightError <= high &&
                (double)support.Height + support.HeightError >= low)
            {
                if (!route.Append(at with { Y = support.Height }))
                    throw new ContactQueryFailure(MovementAvailability.Known, MovementStepOutcome.Blocked);
                return new(at with { Y = support.Height }, MathF.Min(0f, current.Y + gravity * remainingTime),
                    Achieved(feet, start, moved), remaining, AirOutcome.Sliding, support, 0f - current.Y, blocked)
                {
                    HorizontalVelocity = horizontal,
                    RemainingTime = remainingTime,
                    MediumHandoff = medium?.ChangesOwner(at with { Y = support.Height },
                        MathF.Min(0f, current.Y + gravity * remainingTime), support, false) == true,
                    HorizontalPlanPreserved = onPlan && start.X == feet.X && start.Z == feet.Z,
                    ClimbBudget = budget,
                };
            }
            if (!rising && medium?.ChangesOwner(at, current.Y, NoSupport, false) == true)
                return Handoff(at, NoSupport, AirOutcome.Airborne, remainingTime);
        }
        return new(start + moved, current.Y, Achieved(feet, start, moved), Vector2.Zero, AirOutcome.Airborne,
            NoSupport, 0f, blocked)
        {
            HorizontalVelocity = new Vector2(current.X, current.Z),
            HorizontalPlanPreserved = onPlan && start.X == feet.X && start.Z == feet.Z,
            ClimbBudget = budget,
        };

        AirStepResult Handoff(Vector3 at, SupportSample support, AirOutcome outcome, float remaining) =>
            new(at, current.Y == vy
                    ? MathF.Max(-maxFallSpeed, verticalVelocity - gravity * (dt - remaining)) : current.Y,
                Achieved(feet, start, moved), Vector2.Zero, outcome, support, 0, blocked)
            {
                HorizontalVelocity = new(current.X, current.Z),
                RemainingTime = remaining,
                HorizontalPlanPreserved = onPlan && start.X == feet.X && start.Z == feet.Z,
                ClimbBudget = budget,
                MediumHandoff = true,
            };
    }

    // Cuts the substep's horizontal move to the play area. True when the clamp shortened it.
    static bool Bounded(Vector2 axis, ref Vector3 move, Func<float, float, Vector2>? clampXz)
    {
        var horizontal = new Vector2(move.X, move.Z);
        if (!PlayArea.Clamp(axis, ref horizontal, clampXz)) return false;
        move = new Vector3(horizontal.X, move.Y, horizontal.Y);
        return true;
    }

    static double Budget(in MoveTuning tuning, float dt) =>
        tuning.MaxStepClimbSpeed > 0 ? (double)tuning.MaxStepClimbSpeed * dt : double.PositiveInfinity;

    // The horizontal wall normal of analytic terrain at the flight's new axis that is more than StepHeight above the
    // substep's highest feet, or steep by the tick's gate and above them: the delegate's normal made horizontal, else
    // the reverse of the flight's horizontal move. Null without a wall. Steep is the foot support's analytic test.
    static Vector2? TerrainWall(ContactQueryContext queries,
        Vector3 from, in ShellFlight flight, in MoveTuning tuning, float cosMaxSlope)
    {
        if (flight.Achieved.X == 0f && flight.Achieved.Z == 0f) return null;
        float x = from.X + flight.Achieved.X, z = from.Z + flight.Achieved.Z;
        float height = queries.HeightAt(new(x, from.Y + flight.Highest, z)), highest = from.Y + flight.Highest;
        if (!(height > highest)) return null;
        Vector3? delegated = queries.NormalAt(new(x, from.Y + flight.Highest, z));
        bool steep = delegated is Vector3 n && n.Y < cosMaxSlope;
        if (!steep && !(height > highest + tuning.StepHeight)) return null;
        Vector3 normal = delegated ?? Vector3.Zero;
        var across = new Vector2(normal.X, normal.Z);
        float length = across.Length();
        if (length > 0f && float.IsFinite(length)) return across / length;
        return Vector2.Normalize(new Vector2(-flight.Achieved.X, -flight.Achieved.Z));
    }

    // The horizontal vector less its component into the wall. Zero when it does not press into the wall, as the
    // ground core's wall tangent.
    static Vector2 Along(Vector2 vector, Vector2 wall)
    {
        float into = Vector2.Dot(vector, wall);
        return into < 0f ? vector - into * wall : Vector2.Zero;
    }

    // The achieved move counts the recovery offset too, which is exactly zero when recovery did nothing.
    static Vector2 Achieved(Vector3 input, Vector3 start, Vector3 moved) =>
        new Vector2(start.X - input.X, start.Z - input.Z) + new Vector2(moved.X, moved.Z);

    static int Substeps(Vector3 displacement, in MoveTuning tuning)
    {
        double length = Math.Sqrt((double)displacement.X * displacement.X +
            (double)displacement.Y * displacement.Y + (double)displacement.Z * displacement.Z);
        double count = Math.Ceiling(length / (0.5 * tuning.CapsuleRadius));
        if (count > MaxSubsteps)
            throw new ArgumentOutOfRangeException(nameof(displacement), "The displacement is too long for one tick.");
        return Math.Max(1, (int)count);
    }

    static void Validate(Vector3 feet, Vector2 velocity, float verticalVelocity, float dt, float gravity,
        in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians)
    {
        if (!float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
            throw new ArgumentOutOfRangeException(nameof(feet), "The feet must be finite.");
        if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(verticalVelocity))
            throw new ArgumentOutOfRangeException(nameof(velocity), "The velocity must be finite.");
        if (!float.IsFinite(dt) || dt <= 0)
            throw new ArgumentOutOfRangeException(nameof(dt), "dt must be positive and finite.");
        if (!float.IsFinite(gravity))
            throw new ArgumentOutOfRangeException(nameof(gravity), "Gravity must be finite.");
        if (!float.IsFinite(tractionSlopeRadians))
            throw new ArgumentOutOfRangeException(nameof(tractionSlopeRadians), "The traction gate must be finite.");
        if (!float.IsFinite(settings.FootRadiusFraction) || settings.FootRadiusFraction <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings),
                "FootRadiusFraction must be positive and finite.");
        if (!float.IsFinite(tuning.CapsuleRadius) || tuning.CapsuleRadius <= 0 ||
            !float.IsFinite(tuning.CapsuleHalfHeight) || !float.IsFinite(tuning.StepHeight) ||
            tuning.StepHeight < 0 || !float.IsFinite(tuning.MaxFallSpeed) || tuning.MaxFallSpeed < 0)
            throw new ArgumentOutOfRangeException(nameof(tuning), "The air pass tuning values must be finite.");
        ShellGeometry.Validate(tuning, settings);
        // The foot probe meets the steepest walkable plane footRadius (1 / cos - 1) above the plane at the axis, and
        // the landing band reaches StepHeight above the feet. A footed body keeps footing to the banded gate.
        float band = tuning.TractionHysteresisRadians;
        float steepest = band > 0f ? tuning.MaxSlopeRadians + band : tuning.MaxSlopeRadians;
        float footRadius = settings.FootRadiusFraction * tuning.CapsuleRadius;
        if (!(footRadius * (1f / MathF.Cos(steepest) - 1f) <= tuning.StepHeight))
            throw new ArgumentOutOfRangeException(nameof(settings),
                "StepHeight must be at least FootRadiusFraction * CapsuleRadius * (1 / cos(MaxSlopeRadians + " +
                "TractionHysteresisRadians) - 1), so the landing band holds the foot probe's contact on the " +
                "steepest walkable plane.");
    }
}
