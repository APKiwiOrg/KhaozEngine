using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    // #1756. A cached rotational Boolean must leave the complete carried movement state unchanged.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProposalAdmissionReuseKeepsEveryMovementStateBit(bool bank)
    {
        using var geometry = bank ? FootSupportScenes.Bank(SceneVariant.Box) : FootSupportScenes.Floor(SceneVariant.Box);
        using var scene = new QueryScene(geometry);
        var state = new FramedMovementState(new MoveState
        {
            Position = new(-0.5f, 0.751f, 0),
            Grounded = true,
            FacingYaw = -0f,
            SpeedScale = 0.75f,
            Commitment = new MovementCommitment(3, Vector2.UnitX, 6, 5, 1f / 30, 4)
        }, scene.Lease.Frame, new(Room, new MovementSupportKey("world", "bank"), scene.Lease.Identity));
        bool prior = SupportProposalAdmission.BypassForTests;
        try
        {
            SupportProposalAdmission.BypassForTests = false;
            SupportProposalAdmission.ResetForTests();
            using var capture = new SupportProposalAdmissionCapture();
            for (int tick = 0; tick < 8; tick++)
            {
                MovementStepResult actual = Step(state);
                SupportProposalAdmission.BypassForTests = true;
                MovementStepResult expected = Step(state);
                SupportProposalAdmission.BypassForTests = false;
                Assert.Equal(expected.Outcome, actual.Outcome);
                Assert.Equal(expected.State.Frame, actual.State.Frame);
                Assert.Equal(expected.State.Selection, actual.State.Selection);
                AssertMoveBits(expected.State.State, actual.State.State);
                state = actual.State;
            }
            Assert.True(capture.Hits > 0);
            Assert.True(capture.Bypasses > 0);
        }
        finally
        {
            SupportProposalAdmission.BypassForTests = prior;
            SupportProposalAdmission.ResetForTests();
        }

        MovementStepResult Step(FramedMovementState basis) => ContactController.StepLeased(basis, Vector2.UnitX,
            1, false, false, 1f / 30, Tuning, new(WaterTraversalMode.DryOnly), scene.Lease);
    }

    static void AssertMoveBits(MoveState expected, MoveState actual)
    {
        Assert.Equal(expected.Grounded, actual.Grounded);
        Assert.Equal(expected.Swimming, actual.Swimming);
        Assert.Equal(expected.WaterExcursion, actual.WaterExcursion);
        Assert.Equal(expected.SupportGranted, actual.SupportGranted);
        Assert.Equal(expected.SpeedScaleStorageBits, actual.SpeedScaleStorageBits);
        Assert.Equal(expected.Commitment.Sequence, actual.Commitment.Sequence);
        Assert.Equal(expected.Commitment.Phase, actual.Commitment.Phase);
        Assert.Equal(expected.Commitment.EndReason, actual.Commitment.EndReason);
        Assert.Equal(expected.Commitment.Kind, actual.Commitment.Kind);
        Assert.Equal(expected.Commitment.ConsecutiveBlockedTicks, actual.Commitment.ConsecutiveBlockedTicks);
        Assert.Equal(Floats(expected).Select(BitConverter.SingleToInt32Bits), Floats(actual).Select(BitConverter.SingleToInt32Bits));

        static float[] Floats(MoveState state) => [state.Position.X, state.Position.Y, state.Position.Z,
            state.VerticalVelocity, state.TimeSinceGrounded, state.JumpBufferRemaining,
            state.HorizontalVelocity.X, state.HorizontalVelocity.Y, state.CommandedVelocity.X, state.CommandedVelocity.Y,
            state.ClimbRate, state.ClimbRateEwma, state.StepDeltaY, state.LandingImpactSpeed, state.FacingYaw,
            state.Commitment.Direction.X, state.Commitment.Direction.Y, state.Commitment.HorizontalSpeed,
            state.Commitment.VerticalSpeed, state.Commitment.Gravity, state.Commitment.PreparationRemaining,
            state.Commitment.RecoveryRemaining, state.Commitment.TimeoutRemaining, state.Commitment.DistanceRemaining];
    }
}
