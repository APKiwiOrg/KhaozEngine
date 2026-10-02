using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Ground movement providers in absolute world metres, adapted to the physics world's current origin.
/// The caller owns the physics world and calls this context sequentially. Providers must not rebase the world
/// or recursively step the context. Rebase only between steps.</summary>
public sealed partial class GroundMoveContext
{
    public GroundMoveContext(Func<float, float, float> groundHeight,
        Func<float, float, Vector3>? groundNormal = null, IPhysicsWorld? physics = null,
        Func<float, float, Vector2>? clampXz = null,
        Func<float, float, float, MovementMedium>? medium = null)
        : this(groundHeight, groundNormal, physics, clampXz, medium, movementQueries: null)
    {
    }

    public GroundMoveContext(Func<float, float, float> groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? physics,
        Func<float, float, Vector2>? clampXz,
        Func<float, float, float, MovementMedium>? medium,
        IPhysicsWorldQueryView? movementQueries)
    {
        GroundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        if (movementQueries is not null && (physics is null || !ReferenceEquals(movementQueries.SourceWorld, physics)))
            throw new ArgumentException("Movement queries must belong to the complete physics world.", nameof(movementQueries));
        GroundNormal = groundNormal;
        Physics = physics;
        MovementQueries = movementQueries;
        ClampXz = clampXz;
        Medium = medium;
        _localHeight = LocalHeight;
        _localNormal = groundNormal is null ? null : LocalNormal;
        _localClamp = clampXz is null ? null : LocalClamp;
        _localMedium = medium is null ? null : LocalMedium;
    }

    /// <summary>Absolute ground height at absolute XZ.</summary>
    public Func<float, float, float> GroundHeight { get; }

    /// <summary>Ground normal at absolute XZ, unchanged by translation.</summary>
    public Func<float, float, Vector3>? GroundNormal { get; }

    /// <summary>Caller-owned physics world whose poses and queries are local to its Origin.</summary>
    public IPhysicsWorld? Physics { get; }

    /// <summary>Optional non-owning query selection for movement through the complete physics world.</summary>
    public IPhysicsWorldQueryView? MovementQueries { get; }

    /// <summary>Absolute XZ bounds applied to an absolute XZ candidate.</summary>
    public Func<float, float, Vector2>? ClampXz { get; }

    /// <summary>Medium at absolute XZ and feet Y, including an absolute water surface Y.</summary>
    public Func<float, float, float, MovementMedium>? Medium { get; }

    internal MoveState Step(in MoveState body, Vector2 worldDirection, bool run, float dt, in MoveTuning tuning)
    {
        ValidateTuning(tuning);
        if (_stepping) throw new InvalidOperationException("Ground movement steps cannot overlap or recurse.");
        _origin = Physics?.Origin ?? Vector3.Zero;
        if (!float.IsFinite(_origin.X) || !float.IsFinite(_origin.Y) || !float.IsFinite(_origin.Z))
            throw new InvalidOperationException("The physics origin must be finite.");

        _stepping = true;
        try
        {
            EnsureOrigin();
            MoveState local = body;
            local.Position -= _origin;
            MoveState result = CharacterMovement.StepTowards(local, worldDirection, run, dt, _localHeight,
                tuning, preserveSmallMagnitude: true, _localNormal, MovementQueries ?? Physics, _localClamp, _localMedium);
            EnsureOrigin();
            result.Position += _origin;
            return result;
        }
        finally
        {
            _stepping = false;
        }
    }

    internal void ValidateTuning(in MoveTuning tuning)
    {
        Nonnegative(tuning.WalkSpeed, nameof(tuning.WalkSpeed));
        Nonnegative(tuning.RunSpeed, nameof(tuning.RunSpeed));
        if (!float.IsFinite(tuning.CapsuleRadius) || tuning.CapsuleRadius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tuning), "Capsule radius must be finite and positive.");
        float minimumHalfHeight = MathF.Max(0.1f, tuning.CapsuleRadius + 0.005f);
        if (!float.IsFinite(tuning.CapsuleHalfHeight) || tuning.CapsuleHalfHeight < minimumHalfHeight ||
            !float.IsFinite(2f * tuning.CapsuleHalfHeight - 2f * tuning.CapsuleRadius))
            throw new ArgumentOutOfRangeException(nameof(tuning), "Capsule half-height must fit the collision capsule.");
        Nonnegative(tuning.MaxSlopeRadians, nameof(tuning.MaxSlopeRadians));
        if (tuning.MaxSlopeRadians >= MathF.PI / 2f)
            throw new ArgumentOutOfRangeException(nameof(tuning), "Ground slope must be below a vertical surface.");

        Nonnegative(tuning.Gravity, nameof(tuning.Gravity));
        Nonnegative(tuning.JumpSpeed, nameof(tuning.JumpSpeed));
        Nonnegative(tuning.MaxFallSpeed, nameof(tuning.MaxFallSpeed));
        Nonnegative(tuning.CoyoteTime, nameof(tuning.CoyoteTime));
        Nonnegative(tuning.JumpBuffer, nameof(tuning.JumpBuffer));
        Fraction(tuning.AirControl, nameof(tuning.AirControl));
        Nonnegative(tuning.GroundedEpsilon, nameof(tuning.GroundedEpsilon));
        Nonnegative(tuning.StepHeight, nameof(tuning.StepHeight));
        Nonnegative(tuning.MaxStepClimbSpeed, nameof(tuning.MaxStepClimbSpeed));
        Nonnegative(tuning.AirBrakeAccel, nameof(tuning.AirBrakeAccel));
        Nonnegative(tuning.TractionHysteresisRadians, nameof(tuning.TractionHysteresisRadians));
        Nonnegative(tuning.SlideFrictionRampRadians, nameof(tuning.SlideFrictionRampRadians));
        if (!float.IsPositiveInfinity(tuning.FacingTurnSpeed))
            Nonnegative(tuning.FacingTurnSpeed, nameof(tuning.FacingTurnSpeed));

        Fraction(tuning.WadeStartDepthFraction, nameof(tuning.WadeStartDepthFraction));
        Fraction(tuning.WadeEndDepthFraction, nameof(tuning.WadeEndDepthFraction));
        Fraction(tuning.WadeMinSpeedScale, nameof(tuning.WadeMinSpeedScale));
        Fraction(tuning.SwimEnterDepthFraction, nameof(tuning.SwimEnterDepthFraction));
        Fraction(tuning.SwimExitDepthFraction, nameof(tuning.SwimExitDepthFraction));
        Fraction(tuning.SwimSurfaceSubmersionFraction, nameof(tuning.SwimSurfaceSubmersionFraction));
        if (tuning.WadeEndDepthFraction <= tuning.WadeStartDepthFraction ||
            tuning.SwimExitDepthFraction > tuning.SwimEnterDepthFraction)
            throw new ArgumentOutOfRangeException(nameof(tuning), "Medium depth thresholds must be ordered.");
        Nonnegative(tuning.SwimSpeed, nameof(tuning.SwimSpeed));
        Nonnegative(tuning.SwimBuoyancyStiffness, nameof(tuning.SwimBuoyancyStiffness));
    }

    private static void Nonnegative(float value, string control)
    {
        if (!float.IsFinite(value) || value < 0f)
            throw new ArgumentOutOfRangeException("tuning", $"{control} must be finite and nonnegative.");
    }

    private static void Fraction(float value, string control)
    {
        if (!float.IsFinite(value) || value < 0f || value > 1f)
            throw new ArgumentOutOfRangeException("tuning", $"{control} must be between zero and one.");
    }
}
