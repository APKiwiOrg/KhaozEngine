using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.ContactDropFollower;

namespace KhaozEngine.Movement;

/// <summary>A bounded launch proof from the actual body, using one authenticated prediction basis.
/// Both Holds count against the baked duration. No preview state is published to the real body.</summary>
internal static class ContactDropPreview
{
    internal static RangeMoveStatus Prove(GroundNavigation navigation, NavDropTraversal traversal,
        in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context,
        ContactDropPredictionBasis basis, PlayerPathInput? playerInput = null)
    {
        var corridor = new NavDropFootprint(navigation.Footprint, tuning, traversal.StartFeet,
            traversal.LandingFeet, navigation.MaxDropMetres);
        if (!corridor.Certify()) return RangeMoveStatus.Blocked;
        int steps = ContactDropControl.PreviewSteps(navigation.Footprint.Options, dt);
        if (steps < 3) return RangeMoveStatus.Blocked;
        MovementFrameDescriptor frame = context.NavigationFrame;
        var state = new FramedMovementState(body with { Position = body.Position - frame.PhysicsOrigin }, frame, null);
        MovementStepResult held = context.PredictContactStep(state, Vector2.Zero, run, dt, tuning, basis, corridor, playerInput);
        Vector3 feet = AbsoluteFeet(held, tuning);
        if (!Accepted(held, tuning, context) || !Supported(held) || !GroundTraversalProbe.Near(feet, traversal.StartFeet))
            return Status(held, RangeMoveStatus.Blocked);
        // Hold proves support, while the actual initial velocities remain the launch basis.
        bool flew = false, landed = false;
        float highest = feet.Y;
        for (int slice = 1; slice < steps; slice++)
        {
            MoveState absolute = state.State with { Position = state.State.Position + frame.PhysicsOrigin };
            var water = context.SampleWaterForPacing(absolute, tuning, out float landScale);
            if (water.Swimming) return RangeMoveStatus.Blocked;
            Vector2 input = ContactDropControl.Input(absolute, traversal.LandingFeet, tuning, run, landScale, dt);
            MovementStepResult result = context.PredictContactStep(state, input, run, dt, tuning, basis, corridor, playerInput);
            feet = AbsoluteFeet(result, tuning);
            if (!Accepted(result, tuning, context) || !corridor.Contains(feet) ||
                (double)highest - feet.Y > navigation.MaxDropMetres ||
                result.ContactFirstAirLandingFeet is { } first && !Target(navigation.Space, traversal, first + frame.PhysicsOrigin))
                return Status(result, RangeMoveStatus.Blocked);
            bool supported = Supported(result);
            flew |= !result.State.State.Grounded || result.ContactAirLanded;
            if (supported && !flew) highest = MathF.Max(highest, feet.Y);
            else if (supported)
            {
                if (!Target(navigation.Space, traversal, feet)) return RangeMoveStatus.Blocked;
                landed = true;
            }
            else if (result.State.State.Grounded || landed) return RangeMoveStatus.Blocked;
            if (supported && flew && GroundTraversalProbe.Near(feet, traversal.LandingFeet))
            {
                if (slice + 1 >= steps) return RangeMoveStatus.Blocked;
                held = context.PredictContactStep(result.State, Vector2.Zero, run, dt, tuning, basis, corridor, playerInput);
                feet = AbsoluteFeet(held, tuning);
                return Accepted(held, tuning, context) && Supported(held) && Target(navigation.Space, traversal, feet) &&
                    corridor.Contains(feet) && GroundTraversalProbe.Near(feet, traversal.LandingFeet) &&
                    (double)highest - feet.Y <= navigation.MaxDropMetres
                    ? RangeMoveStatus.Dropping : Status(held, RangeMoveStatus.Blocked);
            }
            state = result.State;
        }
        return RangeMoveStatus.Blocked;
    }

    internal static bool Target(NavSpace space, NavDropTraversal traversal, Vector3 feet)
    {
        NavLink link = traversal.Link;
        var (x, z) = space.Layers[link.ToLayer].CellOf(feet.X, feet.Z);
        return x == link.ToX && z == link.ToZ && space.LayerAt(feet) == link.ToLayer &&
            GroundTraversalProbe.Near(new(traversal.LandingFeet.X, feet.Y, traversal.LandingFeet.Z), traversal.LandingFeet);
    }
}
