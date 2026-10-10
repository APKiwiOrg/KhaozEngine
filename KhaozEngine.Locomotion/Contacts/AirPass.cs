using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>How an air tick ended. <see cref="Landed"/> seats the feet on walkable support. <see cref="Sliding"/>
/// seats them on steep support, which never grounds the body.</summary>
internal enum AirOutcome : byte { Airborne, Landed, Sliding }

/// <summary>One air tick. <see cref="VerticalVelocity"/> is the speed after gravity and the tick's contacts, and zero
/// after a landing. <see cref="Achieved"/> is the horizontal move that happened. <see cref="Remaining"/> is the
/// horizontal displacement the tick planned after its landing substep, left for the ground core, and zero without a
/// landing or a slide. <see cref="FallSpeedAtContact"/> is the vertical speed at the landing substep, positive
/// downward, and zero without one. <see cref="Blocked"/> says a shell contact or a terrain wall removed part of the
/// move.</summary>
internal readonly record struct AirStepResult(Vector3 Feet, float VerticalVelocity, Vector2 Achieved,
    Vector2 Remaining, AirOutcome Outcome, SupportSample Support, float FallSpeedAtContact, bool Blocked);

/// <summary>Moves a body without footing for one tick: gravity, the shell's flight in substeps, and landing on
/// footprint support while descending. Only the shell blocks, and the legs pass ledge edges.</summary>
internal static class AirPass
{
    // A bound on one tick's substeps, so a huge displacement throws rather than running unbounded.
    const int MaxSubsteps = 1024;

    static readonly SupportSample NoSupport =
        new(SupportStatus.None, float.NaN, float.NaN, Vector3.Zero, null, -1, Vector3.Zero);

    /// <summary>Moves a body whose feet are at <paramref name="feet"/>, in the world's local frame. Gravity applies
    /// first, clamped at <see cref="MoveTuning.MaxFallSpeed"/>. The tick's displacement then runs in substeps of at
    /// most half the capsule radius, and every shell contact removes the velocity component into it. After each
    /// substep that ends descending, the ground core's support query runs at the new axis, from the substep's lowest
    /// feet to <see cref="MoveTuning.StepHeight"/> above its highest. Walkable support in it lands the body through the
    /// ground core's placement, the step up a standing body would take, with the rise above the substep's start feet
    /// paced by <see cref="MoveTuning.MaxStepClimbSpeed"/>. Steep support within the travelled span starts a
    /// slide. Analytic terrain more than <see cref="MoveTuning.StepHeight"/> above the feet at the new axis is
    /// a wall. <paramref name="velocity"/> is the tick's horizontal velocity and <paramref name="verticalVelocity"/>
    /// the carried one before gravity. <paramref name="tractionSlopeRadians"/> is the tick's traction gate. A non-null
    /// <paramref name="world"/> follows the <see cref="FootSupport.Find"/> contract.</summary>
    internal static AirStepResult Step(Vector3 feet, Vector2 velocity, float verticalVelocity, float dt,
        float gravity, in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians,
        Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal,
        IPhysicsWorld? world, IPhysicsQueryLease? lease)
    {
        Validate(feet, velocity, verticalVelocity, dt, gravity, tuning, settings, tractionSlopeRadians);
        float vy = verticalVelocity - gravity * dt;
        if (vy < -tuning.MaxFallSpeed) vy = -tuning.MaxFallSpeed;
        var displacement = new Vector3(velocity.X * dt, vy * dt, velocity.Y * dt);
        int substeps = Substeps(displacement, tuning);
        float stepTime = dt / substeps;
        float footRadius = settings.FootRadiusFraction * tuning.CapsuleRadius;
        float cosMaxSlope = MathF.Cos(tractionSlopeRadians);

        // A shell that cannot be freed does not move or fall, as a held ground tick does not move.
        Vector3 start = ShellMotion.Recover(world, feet, tuning, out bool cleared);
        if (!cleared)
            return new(feet, 0f, Vector2.Zero, Vector2.Zero, AirOutcome.Airborne, NoSupport, 0f, true);

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
            ShellFlight flight = ShellMotion.Fly(world, from, move, ref current, tuning, cosMaxSlope);
            // Analytic terrain has no shell to meet. As in the ground core, terrain more than StepHeight above the
            // feet at the new axis is a wall. The substep reruns without its component into the wall, then without
            // its horizontal part if the wall still stops it.
            if (TerrainWall(groundHeight, groundNormal, from, flight, tuning) is Vector2 wall)
            {
                Vector2 alongMove = Along(new Vector2(move.X, move.Z), wall);
                Vector2 alongVelocity = Along(new Vector2(before.X, before.Z), wall);
                current = new Vector3(alongVelocity.X, before.Y, alongVelocity.Y);
                flight = ShellMotion.Fly(world, from, new Vector3(alongMove.X, move.Y, alongMove.Y), ref current,
                    tuning, cosMaxSlope);
                if (TerrainWall(groundHeight, groundNormal, from, flight, tuning) is not null)
                {
                    current = new Vector3(0f, before.Y, 0f);
                    flight = ShellMotion.Fly(world, from, new Vector3(0f, move.Y, 0f), ref current, tuning,
                        cosMaxSlope);
                }
                blocked = true;
            }
            if (flight.Blocked || flight.Achieved != move || current != before) onPlan = false;
            blocked |= flight.Blocked;
            moved = onPlan ? next : moved + flight.Achieved;
            if (current.Y > 0f) continue;

            Vector3 at = start + moved;
            float high = MathF.Max(from.Y + flight.Highest, at.Y);
            float low = MathF.Min(from.Y + flight.Lowest, at.Y);
            var axis = new Vector2(at.X, at.Z);
            SupportSample support = FootSupport.Find(groundHeight, groundNormal, world, lease,
                new FootSupportQuery(axis, high, footRadius, tuning.StepHeight, MathF.Max(0f, high - low),
                    cosMaxSlope));
            var remaining = new Vector2(displacement.X - next.X, displacement.Z - next.Z);
            if (support.Status == SupportStatus.Walkable)
            {
                // The seat goes through the ground core's placement, so the feet never seat into a shell overlap and
                // a rise above the substep's start feet is paced like a step up. The ground core pays the rest.
                double rise = (double)support.Height - high;
                var seat = new GroundSeatResult(SeatOutcome.Seated, support.Height, support,
                    rise > 0 ? GroundPlacement.NotBelow(rise) : 0f, Vector3.Zero);
                GroundPlacement placement = GroundPlacement.Try(groundHeight, groundNormal, world, lease, NoSupport,
                    high, axis, Vector2.Zero, seat, footRadius, tuning, cosMaxSlope, Budget(tuning, dt));
                if (!placement.Valid) continue;
                Vector3 push = new(placement.Total.X, 0f, placement.Total.Y);
                return new(placement.Feet, 0f, Achieved(feet, start, moved + push), remaining, AirOutcome.Landed,
                    placement.Support, 0f - current.Y, blocked || placement.Pushed);
            }
            // Steep support never lifts the body: it starts a slide only within the span the feet travelled.
            if (support.Status == SupportStatus.Steep &&
                (double)support.Height - support.HeightError <= high &&
                (double)support.Height + support.HeightError >= low)
                return new(at with { Y = support.Height }, current.Y, Achieved(feet, start, moved), remaining,
                    AirOutcome.Sliding, support, 0f - current.Y, blocked);
        }
        return new(start + moved, current.Y, Achieved(feet, start, moved), Vector2.Zero, AirOutcome.Airborne,
            NoSupport, 0f, blocked);
    }

    static double Budget(in MoveTuning tuning, float dt) =>
        tuning.MaxStepClimbSpeed > 0 ? (double)tuning.MaxStepClimbSpeed * dt : double.PositiveInfinity;

    // The horizontal wall normal of analytic terrain more than StepHeight above the substep's highest feet at the
    // flight's new axis: the delegate's normal made horizontal, else the reverse of the flight's horizontal move.
    // Null without a wall.
    static Vector2? TerrainWall(Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal,
        Vector3 from, in ShellFlight flight, in MoveTuning tuning)
    {
        if (groundHeight is null || (flight.Achieved.X == 0f && flight.Achieved.Z == 0f)) return null;
        float x = from.X + flight.Achieved.X, z = from.Z + flight.Achieved.Z;
        if (!(groundHeight(x, z) > from.Y + flight.Highest + tuning.StepHeight)) return null;
        Vector3 normal = groundNormal?.Invoke(x, z) ?? Vector3.Zero;
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
        ShellGeometry.Validate(tuning);
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
