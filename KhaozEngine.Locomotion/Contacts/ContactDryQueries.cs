using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

// Existing dry pass entry points share the query seam without acquiring or disposing their caller's lease.
internal static partial class GroundCore
{
    internal static GroundStepResult Step(Vector3 feet, Vector2 displacement, float dt, in MoveTuning tuning,
        in GroundCoreSettings settings, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        float? tractionSlopeRadians = null, double? climbBudget = null,
        Func<float, float, Vector2>? clampXz = null, bool stepInProgress = false) =>
        Step(feet, displacement, dt, tuning, settings,
            new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning), tractionSlopeRadians,
            climbBudget, clampXz, stepInProgress);

    internal static GroundStepResult Slide(Vector3 feet, Vector2 displacement, float dt, in MoveTuning tuning,
        in GroundCoreSettings settings, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        float tractionSlopeRadians, double? climbBudget = null, Func<float, float, Vector2>? clampXz = null) =>
        Slide(feet, displacement, dt, tuning, settings,
            new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning), tractionSlopeRadians,
            climbBudget, clampXz);
}

internal static partial class GroundSeat
{
    internal static GroundSeatResult Resolve(Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in SupportSample start, Vector2 startAxis, float startFeetY, Vector2 axis, Vector2 moveDirection,
        float footRadius, in MoveTuning tuning, float cosMaxSlope) =>
        Resolve(new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning), start,
            startAxis, startFeetY, axis, moveDirection, footRadius, tuning, cosMaxSlope);
}

internal readonly partial record struct GroundPlacement
{
    internal static GroundPlacement Try(Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        in SupportSample start, float startFeetY, Vector2 startAxis, Vector2 total, in GroundSeatResult seat,
        float footRadius, in MoveTuning tuning, float cosMaxSlope, double budget, bool clearSteep = false) =>
        Try(new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning), start, startFeetY,
            startAxis, total, seat, footRadius, tuning, cosMaxSlope, budget, clearSteep);
}

internal static partial class AirPass
{
    internal static AirStepResult Step(Vector3 feet, Vector2 velocity, float verticalVelocity, float dt,
        float gravity, in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians,
        Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal,
        IPhysicsWorld? world, IPhysicsQueryLease? lease, double? climbBudget = null,
        Func<float, float, Vector2>? clampXz = null) =>
        Step(feet, velocity, verticalVelocity, dt, gravity, tuning, settings, tractionSlopeRadians,
            new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning), climbBudget, clampXz);
}

internal static partial class SlideCore
{
    internal static bool InContact(Vector3 feet, in MoveTuning tuning, in GroundCoreSettings settings,
        float tractionSlopeRadians, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        out SupportSample support) =>
        InContact(feet, tuning, settings, tractionSlopeRadians,
            new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning), out support);

    internal static SlideStepResult Step(Vector3 feet, Vector2 carry, float verticalVelocity, Vector2 steer,
        float dt, in MoveTuning tuning, in GroundCoreSettings settings, float tractionSlopeRadians,
        in SupportSample support, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease,
        double? climbBudget = null, Func<float, float, Vector2>? clampXz = null) =>
        Step(feet, carry, verticalVelocity, steer, dt, tuning, settings, tractionSlopeRadians, support,
            new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning), climbBudget, clampXz);
}

internal static partial class GroundSignals
{
    internal static (float ClimbRate, float Ewma, float StepDeltaY) Resolve(in MoveState input, float stepRise,
        float moveRise, bool riseSeen, bool dropSeen, float carriedLag, Vector2 groundTravel, Vector3 feet,
        in SupportSample axisSupport, bool walkableGround, float dt, in MoveTuning tuning, float footRadius,
        float cosMaxSlope, Func<float, float, float>? groundHeight, Func<float, float, Vector3>? groundNormal,
        IPhysicsWorld? world, IPhysicsQueryLease? lease) =>
        Resolve(input, stepRise, moveRise, riseSeen, dropSeen, carriedLag, groundTravel, feet, axisSupport,
            walkableGround, dt, tuning, footRadius, cosMaxSlope,
            new DryContactQueryContext(groundHeight, groundNormal, world, lease, tuning));
}

internal static partial class ShellMotion
{
    internal static Vector3 Recover(IPhysicsWorld? world, Vector3 feet, in MoveTuning tuning, out bool cleared) =>
        RecoverWithQueries(new DryContactQueryContext(null, null, world, null, tuning), feet, tuning, out cleared);
    internal static float Lift(IPhysicsWorld? world, Vector3 feet, in MoveTuning tuning) =>
        LiftWithQueries(new DryContactQueryContext(null, null, world, null, tuning), feet, tuning);
    internal static ShellSweep Sweep(IPhysicsWorld? world, Vector3 feet, float lift, Vector2 move,
        in MoveTuning tuning, float cosMaxSlope, Func<float, float, Vector2>? clampXz = null) =>
        SweepWithQueries(new DryContactQueryContext(null, null, world, null, tuning), feet, lift, move, tuning,
            cosMaxSlope, clampXz);
    internal static ShellFlight Fly(IPhysicsWorld? world, Vector3 feet, Vector3 move, ref Vector3 velocity,
        in MoveTuning tuning, float cosMaxSlope, Func<float, float, Vector2>? clampXz = null) =>
        FlyWithQueries(new DryContactQueryContext(null, null, world, null, tuning), feet, move, ref velocity, tuning,
            cosMaxSlope, clampXz);
}
