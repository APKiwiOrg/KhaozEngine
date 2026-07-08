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

        [Fact]
        public void PreSwimOverload_NeverSwims_MatchesFlagFalse()
        {
            // The pre-swim overload (no swimming param) must equal the flagged overload with swimming: false.
            Assert.Equal(
                LocomotionStateMachine.Evaluate(3f, grounded: true, verticalVelocity: 0f, T),
                LocomotionStateMachine.Evaluate(3f, grounded: true, verticalVelocity: 0f, swimming: false, T));
        }

        [Fact]
        public void Swimming_Still_IsSwimIdle()
        {
            // Swimming with near-zero planar speed (below the SwimSpeed dead-zone) -> tread water.
            Assert.Equal(LocomotionState.SwimIdle,
                LocomotionStateMachine.Evaluate(0f, grounded: false, verticalVelocity: 0f, swimming: true, T));
        }

        [Fact]
        public void Swimming_Moving_IsSwim()
        {
            // Swimming above the SwimSpeed dead-zone -> forward stroke.
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(2f, grounded: false, verticalVelocity: 0f, swimming: true, T));
        }

        [Fact]
        public void Swimming_AtSwimSpeedBoundary_IsSwim()
        {
            // The boundary is inclusive (>=), mirroring the Walk/Run thresholds: exactly at SwimSpeed -> Swim.
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(T.SwimSpeed, grounded: false, verticalVelocity: 0f, swimming: true, T));
            // Just below -> tread.
            Assert.Equal(LocomotionState.SwimIdle,
                LocomotionStateMachine.Evaluate(T.SwimSpeed - 1e-4f, grounded: false, verticalVelocity: 0f, swimming: true, T));
        }

        [Fact]
        public void Swimming_OverridesGroundAndAir()
        {
            // The swim flag wins regardless of grounded / vertical / a run-band planar speed: a swimming character is
            // neither running nor falling. Grounded+fast+swimming -> Swim (not Run); airborne+rising+swimming -> Swim.
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(8f, grounded: true, verticalVelocity: 0f, swimming: true, T));
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(8f, grounded: false, verticalVelocity: 5f, swimming: true, T));
            // A treading swimmer that happens to be airborne+falling still treads, not Fall.
            Assert.Equal(LocomotionState.SwimIdle,
                LocomotionStateMachine.Evaluate(0f, grounded: false, verticalVelocity: -4f, swimming: true, T));
        }
    }
}
