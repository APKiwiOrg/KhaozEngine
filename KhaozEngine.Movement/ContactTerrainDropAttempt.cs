using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;

namespace KhaozEngine.Movement;

/// <summary>One preview or follower's policy, selected-support witness and mutable corridor.</summary>
internal sealed class ContactTerrainDropAttempt
{
    internal ContactTerrainDropPolicy Policy { get; }
    readonly ContactDescentEvidence _witness;

    internal ContactTerrainDropAttempt(ContactTerrainDropCertificate certificate, MovementFrameDescriptor frame,
        MoveTuning tuning, float allowance)
    {
        Policy = new(certificate, frame, tuning, allowance);
        _witness = new(Policy.Observe);
    }

    internal bool Predict(in MoveState body, Vector2 command, bool run, float dt, in MoveTuning tuning,
        GroundMoveContext context, ContactDropPredictionBasis basis, PlayerPathInput? input,
        bool initial, bool hold, out MovementStepResult result)
    {
        Policy.BeginTick();
        var frame = context.NavigationFrame;
        result = context.PredictContactStep(new(body with { Position = body.Position - frame.PhysicsOrigin }, frame, null),
            command, run, dt, tuning, basis, Policy.Corridor, input, _witness);
        return Policy.AcceptTick(result, _witness.Summary, initial, hold);
    }
}
