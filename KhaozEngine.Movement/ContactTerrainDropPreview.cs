using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;
using static KhaozEngine.Movement.ContactDropFollower;

namespace KhaozEngine.Movement;

internal static class ContactTerrainDropPreview
{
    internal static RangeMoveStatus Prove(GroundNavigation navigation, NavDropTraversal traversal,
        in MoveState body, in MoveTuning tuning, bool run, float dt, GroundMoveContext context,
        ContactDropPredictionBasis basis, PlayerPathInput? input)
    {
        if (navigation.AreaExclusionSource is not null &&
            !ContactTerrainAreaExclusions.Allows(navigation, traversal.Link)) return RangeMoveStatus.Blocked;
        if (!navigation.Terrain!.TryGet(traversal.Link, out var certificate)) return RangeMoveStatus.Blocked;
        int steps = ContactDropControl.PreviewSteps(navigation.Footprint.Options, dt);
        if (steps < 3) return RangeMoveStatus.Blocked;
        var attempt = new ContactTerrainDropAttempt(certificate!, context.NavigationFrame, tuning, navigation.MaxDropMetres);
        if (!attempt.Predict(body, Vector2.Zero, run, dt, tuning, context, basis, input,
                initial: true, hold: true, out var result)) return Status(result, RangeMoveStatus.Blocked);
        // The Hold authenticates source support. The actual momentum remains the launch state.
        MoveState state = body;
        for (int slice = 1; slice < steps; slice++)
        {
            var water = context.SampleWaterForPacing(state, tuning, out float scale);
            if (water.Swimming) return RangeMoveStatus.Blocked;
            bool hold = attempt.Policy.Phase == ContactTerrainDropPhase.TerminalHold;
            Vector2 command = hold ? Vector2.Zero : ContactDropControl.Input(state, traversal.LandingFeet, tuning, run, scale, dt);
            if (!attempt.Predict(state, command, run, dt, tuning, context, basis, input,
                    initial: false, hold, out result)) return Status(result, RangeMoveStatus.Blocked);
            if (attempt.Policy.Complete) return RangeMoveStatus.Dropping;
            attempt.Policy.ReadyToHold(AbsoluteFeet(result, tuning));
            state = result.State.State with { Position = result.State.State.Position + result.State.Frame.PhysicsOrigin };
        }
        return RangeMoveStatus.Blocked;
    }
}
