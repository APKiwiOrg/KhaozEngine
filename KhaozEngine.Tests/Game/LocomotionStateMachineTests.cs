using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    public class LocomotionStateMachineTests
    {
        static readonly LocomotionThresholds T = LocomotionThresholds.Default;   // WalkSpeed 0.1, RunSpeed 4.5

        [Fact]
        public void GroundedStill_IsIdle()
        {
            Assert.Equal(LocomotionState.Idle, LocomotionStateMachine.Evaluate(0f, grounded: true, verticalVelocity: 0f, T));
        }

        [Fact]
        public void GroundedSlow_IsWalk()
        {
            Assert.Equal(LocomotionState.Walk, LocomotionStateMachine.Evaluate(2f, grounded: true, verticalVelocity: 0f, T));
        }

        [Fact]
        public void GroundedFast_IsRun()
        {
            Assert.Equal(LocomotionState.Run, LocomotionStateMachine.Evaluate(6f, grounded: true, verticalVelocity: 0f, T));
        }

        [Fact]
        public void Airborne_Rising_IsJump()
        {
            Assert.Equal(LocomotionState.Jump, LocomotionStateMachine.Evaluate(0f, grounded: false, verticalVelocity: 4f, T));
        }

        [Fact]
        public void Airborne_Falling_IsFall()
        {
            Assert.Equal(LocomotionState.Fall, LocomotionStateMachine.Evaluate(0f, grounded: false, verticalVelocity: -4f, T));
        }

        [Fact]
        public void Airborne_ZeroVertical_IsFall()
        {
            Assert.Equal(LocomotionState.Fall, LocomotionStateMachine.Evaluate(0f, grounded: false, verticalVelocity: 0f, T));
        }

        [Fact]
        public void Airborne_OverridesSpeed()
        {
            // Fast AND airborne+rising -> Jump (the air state wins over the run threshold).
            Assert.Equal(LocomotionState.Jump, LocomotionStateMachine.Evaluate(8f, grounded: false, verticalVelocity: 5f, T));
        }
    }
}
