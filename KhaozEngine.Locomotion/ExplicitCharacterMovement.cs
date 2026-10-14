using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Locomotion;

/// <summary>Explicit movement under a caller-owned read lease. The caller publishes the returned
/// pure state before releasing that lease. No physical mutation occurs in this kernel.</summary>
public static partial class ExplicitCharacterMovement
{
    internal static MovementStepResult Step(in FramedMovementState state, in MoveCommand command,
        float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries,
        in ContactComposition composition, MovementBoundary? boundary = null)
    {
        if (!float.IsFinite(command.Move.X) || !float.IsFinite(command.Move.Y) || !float.IsFinite(command.CameraYaw))
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        var (direction, fraction, run) = CharacterMovement.ResolveCameraCommand(command, tuning);
        return ContactController.StepLeased(state, direction, fraction, run, command.Jump,
            deltaSeconds, tuning, water, queries, composition, boundary, command.FaceCamera ? command.CameraYaw : null);
    }

    internal static MovementStepResult StepTowards(in FramedMovementState state, Vector2 direction, bool run,
        float deltaSeconds, in MoveTuning tuning, in WaterTraversalPolicy water, MovementQueryLease queries,
        in ContactComposition composition, MovementBoundary? boundary = null)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y))
            return Hold(state, MovementStepOutcome.EnvironmentInvalid);
        var (resolved, fraction) = CharacterMovement.ResolveWorldDir(direction, preserveSmallMagnitude: true);
        return ContactController.StepLeased(state, resolved, fraction, run, false, deltaSeconds, tuning, water, queries, composition, boundary);
    }

}
