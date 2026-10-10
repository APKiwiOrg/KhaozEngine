using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>How a slide tick ended. <see cref="Wedged"/> is a slide held in a crease, which counts as grounded for
/// the tick. <see cref="Landed"/> reached walkable support. <see cref="Airborne"/> lost support or failed the slide
/// seat's clearance.</summary>
internal enum SlideOutcome : byte { Sliding, Wedged, Landed, Airborne }

/// <summary>One slide tick. <see cref="HorizontalVelocity"/> is the slide's carry, without the steer.
/// <see cref="VerticalVelocity"/> is the seated rise over dt while sliding or wedged, zero after a landing, and the
/// slide's own vertical speed when airborne. <see cref="ImpactSpeed"/> is the downward vertical speed of a landing,
/// else zero.</summary>
internal readonly record struct SlideStepResult(Vector3 Feet, Vector2 HorizontalVelocity, float VerticalVelocity,
    Vector2 Achieved, SlideOutcome Outcome, SupportSample Support, float ImpactSpeed);

/// <summary>Slides a body on certified steep support, analytic terrain and physics statics alike. The dynamics come
/// from the support's plane and the move runs through <see cref="GroundCore.Slide"/>.</summary>
internal static class SlideCore
{
    // How far from the feet steep support counts as contact.
    const float ContactReach = 0.001f;
    // Below this the plane's horizontal normal carries no fall line.
    const float FlatNormalSquared = 1e-12f;
    // The smallest gate the terminal reads, so a degenerate gate cannot make the terminal unbounded.
    const float MinTerminalGateRadians = 1e-3f;
    // Two fall lines oppose when they are more than 120 degrees apart, so their unit sum is no longer than either.
    const float OpposingCos = -0.5f;

    /// <summary>True when the footprint at <paramref name="feet"/> stands on steep support within 1 mm of the feet.
    /// The query reaches <see cref="MoveTuning.StepHeight"/> up, as every footing query does, because a foot probe
    /// meets a sloped plane above the plane at its axis. Contact needs the support itself within 1 mm.</summary>
    internal static bool InContact(Vector3 feet, in MoveTuning tuning, in GroundCoreSettings settings,
        float tractionSlopeRadians, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        out SupportSample support)
    {
        if (!float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
            throw new ArgumentOutOfRangeException(nameof(feet), "The feet must be finite.");
        if (!float.IsFinite(tractionSlopeRadians))
            throw new ArgumentOutOfRangeException(nameof(tractionSlopeRadians), "The traction gate must be finite.");
        float footRadius = settings.FootRadiusFraction * tuning.CapsuleRadius;
        support = FootSupport.Find(groundHeight, groundNormal, world, lease,
            new FootSupportQuery(new Vector2(feet.X, feet.Z), feet.Y, footRadius, tuning.StepHeight, ContactReach,
                MathF.Cos(tractionSlopeRadians)));
        return support.Status == SupportStatus.Steep &&
            (double)support.Height - support.HeightError <= (double)feet.Y + ContactReach;
    }

    /// <summary>One slide tick on the steep <paramref name="support"/> under <paramref name="feet"/>. The fall line
    /// and contour come from the support's plane. The fall-line speed takes gravity along the plane through the
    /// friction ramp at the gate, clamped to <c>MaxFallSpeed / max(sin(slope), sin(gate))</c>, and
    /// <paramref name="steer"/> adds its contour part to the move only. A tick that ends no lower than it started,
    /// where the fall lines oppose, is wedged.</summary>
    internal static SlideStepResult Step(Vector3 feet, Vector2 carry, float verticalVelocity, Vector2 steer,
        float dt, in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians,
        in SupportSample support, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease)
    {
        if (!float.IsFinite(carry.X) || !float.IsFinite(carry.Y) || !float.IsFinite(verticalVelocity) ||
            !float.IsFinite(steer.X) || !float.IsFinite(steer.Y))
            throw new ArgumentOutOfRangeException(nameof(carry), "The velocities must be finite.");
        if (!float.IsFinite(tractionSlopeRadians))
            throw new ArgumentOutOfRangeException(nameof(tractionSlopeRadians), "The traction gate must be finite.");
        if (support.Status != SupportStatus.Steep)
            throw new ArgumentException("A slide needs steep support.", nameof(support));

        // The plane frame: t down the fall line, c along the contour, both unit vectors in the plane.
        Vector3 n = support.Normal;
        float ny = Math.Clamp(n.Y, 0f, 1f);
        float h = MathF.Sqrt(MathF.Max(0f, 1f - ny * ny));
        Vector2 down = FallLine(n);
        float tx = ny * down.X, ty = -h, tz = ny * down.Y;
        float cx = -down.Y, cz = down.X;

        float fall = carry.X * tx + verticalVelocity * ty + carry.Y * tz;
        float contour = carry.X * cx + carry.Y * cz;
        fall = CharacterMovement.SlideFallLineStep(fall, tuning.Gravity * h,
            CharacterMovement.SlideFrictionScale(ny, tractionSlopeRadians, tuning), dt);
        float gate = Math.Clamp(tractionSlopeRadians, MinTerminalGateRadians, MathF.PI / 2);
        float terminal = tuning.MaxFallSpeed / MathF.Max(h, MathF.Sin(gate));
        fall = Math.Clamp(fall, -terminal, terminal);

        var velocity = new Vector2(fall * tx + contour * cx, fall * tz + contour * cz);
        float vertical = fall * ty;
        float steerAlong = steer.X * cx + steer.Y * cz;
        Vector2 move = (velocity + steerAlong * new Vector2(cx, cz)) * dt;

        GroundStepResult step = GroundCore.Slide(feet, move, dt, tuning, settings, groundHeight, groundNormal,
            world, lease, tractionSlopeRadians);
        switch (step.Footing)
        {
            case GroundFooting.Walkable:
                return new(step.Feet, velocity, 0f, step.Achieved, SlideOutcome.Landed, step.Support,
                    MathF.Max(0f, -vertical));
            case GroundFooting.None:
                return new(step.Feet, velocity, vertical, step.Achieved, SlideOutcome.Airborne, step.Support, 0f);
            case GroundFooting.Held:
                // Support that cannot be certified holds the body where it is, still sliding on what it had.
                return new(step.Feet, velocity, 0f, step.Achieved, SlideOutcome.Sliding, support, 0f);
        }
        float rise = step.Feet.Y - feet.Y;
        bool wedged = rise >= 0 && Opposed(step.Feet, step.Support, tuning, settings, tractionSlopeRadians,
            groundHeight, groundNormal, world, lease);
        return new(step.Feet, velocity, rise / dt, step.Achieved, wedged ? SlideOutcome.Wedged : SlideOutcome.Sliding,
            step.Support, 0f);
    }

    // The unit horizontal direction down the plane, or zero for a level or degenerate normal.
    static Vector2 FallLine(Vector3 normal)
    {
        var across = new Vector2(normal.X, normal.Z);
        float squared = across.LengthSquared();
        return squared > FlatNormalSquared && float.IsFinite(squared) ? across / MathF.Sqrt(squared) : Vector2.Zero;
    }

    // True when steep support half a capsule radius down the fall line, a stall's farthest distance from a crease,
    // falls back against it. The probe reaches up as far as a plane of the support's own slope rises over the probe's
    // footprint, capped at the body's height.
    static bool Opposed(Vector3 feet, in SupportSample support, in MoveTuning tuning, in GroundCoreSettings settings,
        float tractionSlopeRadians, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease)
    {
        Vector2 down = FallLine(support.Normal);
        if (down == Vector2.Zero) return false;
        float distance = 0.5f * tuning.CapsuleRadius;
        float footRadius = settings.FootRadiusFraction * tuning.CapsuleRadius;
        float height = 2f * tuning.CapsuleHalfHeight;
        float ny = support.Normal.Y;
        float reachUp = ny > 0f ? MathF.Min((distance + footRadius) / ny, height) : height;
        if (!(reachUp >= 0f)) reachUp = height;
        Vector2 axis = new Vector2(feet.X, feet.Z) + distance * down;
        SupportSample beyond = FootSupport.Find(groundHeight, groundNormal, world, lease,
            new FootSupportQuery(axis, feet.Y, footRadius, reachUp, tuning.StepHeight,
                MathF.Cos(tractionSlopeRadians)));
        return beyond.Status == SupportStatus.Steep && Vector2.Dot(FallLine(beyond.Normal), down) < OpposingCos;
    }
}
