using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Authenticates continuation of one launch preview. A changed actual body, control or environment
/// cannot borrow that certificate. Presentation-only facing is not a fall input.</summary>
internal sealed class ContactDropExecutionBasis(ContactDropPredictionBasis preview, bool run, float dt,
    MovementFrameDescriptor frame)
{
    BodyBasis? _expected;

    internal bool Matches(in MoveState body, bool actualRun, float actualDt, MovementFrameDescriptor actualFrame)
        => run == actualRun && dt == actualDt && frame == actualFrame &&
            (_expected is null || _expected.Value == BodyBasis.From(body));

    internal bool SameEnvironment(ContactDropPredictionBasis current) => preview.Matches(current);

    internal void Expect(in MovementStepResult result)
    {
        MoveState absolute = result.State.State with { Position = result.State.State.Position + result.State.Frame.PhysicsOrigin };
        _expected = BodyBasis.From(absolute);
    }

    readonly record struct BodyBasis(Vector3 Position, Vector2 Horizontal, float Vertical, bool Grounded,
        bool Swimming, WaterExcursionState WaterExcursion, MovementCommitment Commitment, float JumpBuffer,
        float SinceGrounded, float SpeedScale)
    {
        internal static BodyBasis From(in MoveState body) => new(body.Position, body.HorizontalVelocity,
            body.VerticalVelocity, body.Grounded, body.Swimming, body.WaterExcursion, body.Commitment,
            body.JumpBufferRemaining, body.TimeSinceGrounded, body.SpeedScale);
    }
}
