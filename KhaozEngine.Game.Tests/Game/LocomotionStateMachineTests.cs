using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    public class LocomotionStateMachineTests
    {
        static readonly LocomotionThresholds T = LocomotionThresholds.Default;   // WalkSpeed 0.1, RunSpeed 9

        [Theory]
        [InlineData(0f, true, 0f, LocomotionState.Idle)]
        [InlineData(2f, true, 0f, LocomotionState.Walk)]
        [InlineData(12f, true, 0f, LocomotionState.Run)]
        [InlineData(0f, false, 4f, LocomotionState.Jump)]
        [InlineData(0f, false, -4f, LocomotionState.Fall)]
        [InlineData(0f, false, 0f, LocomotionState.Fall)]
        [InlineData(8f, false, 5f, LocomotionState.Jump)]
        [InlineData(12f, false, 5f, LocomotionState.Jump)] // Air overrides even the run band.
        public void GroundAndAirStatesRespectMovement(float speed, bool grounded, float verticalVelocity, LocomotionState expected)
        {
            Assert.Equal(expected, LocomotionStateMachine.Evaluate(speed, grounded, verticalVelocity, T));
        }

        [Fact]
        public void PreSwimOverload_NeverSwims_MatchesFlagFalse()
        {
            // The pre-swim overload (no swimming param) must equal the flagged overload with swimming: false.
            Assert.Equal(LocomotionState.Walk,
                LocomotionStateMachine.Evaluate(3f, grounded: true, verticalVelocity: 0f, T));
            Assert.Equal(LocomotionState.Walk,
                LocomotionStateMachine.Evaluate(3f, grounded: true, verticalVelocity: 0f, swimming: false, T));
        }

        [Fact]
        public void Swimming_Still_IsSwimIdle()
        {
            // Swimming with near-zero planar speed (below the SwimForwardThreshold dead-zone) -> tread water.
            Assert.Equal(LocomotionState.SwimIdle,
                LocomotionStateMachine.Evaluate(0f, grounded: false, verticalVelocity: 0f, swimming: true, T));
        }

        [Fact]
        public void Swimming_Moving_IsSwim()
        {
            // Swimming above the SwimForwardThreshold dead-zone -> forward stroke.
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(2f, grounded: false, verticalVelocity: 0f, swimming: true, T));
        }

        [Fact]
        public void Swimming_AtSwimForwardThresholdBoundary_IsSwim()
        {
            // The boundary is inclusive (>=), mirroring the Walk/Run thresholds: exactly at SwimForwardThreshold -> Swim.
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(T.SwimForwardThreshold, grounded: false, verticalVelocity: 0f, swimming: true, T));
            // Just below -> tread.
            Assert.Equal(LocomotionState.SwimIdle,
                LocomotionStateMachine.Evaluate(T.SwimForwardThreshold - 1e-4f, grounded: false, verticalVelocity: 0f, swimming: true, T));
        }

        [Fact]
        public void Swimming_OverridesGroundAndAir()
        {
            // The swim flag wins regardless of grounded / vertical / a run-band planar speed: a swimming character is
            // neither running nor falling. Grounded+moving+swimming and airborne+rising+swimming both yield Swim.
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(8f, grounded: true, verticalVelocity: 0f, swimming: true, T));
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(8f, grounded: false, verticalVelocity: 5f, swimming: true, T));
            // Swimming also wins over the run band (the default run threshold is 9 m/s).
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(12f, grounded: true, verticalVelocity: 0f, swimming: true, T));
            Assert.Equal(LocomotionState.Swim,
                LocomotionStateMachine.Evaluate(12f, grounded: false, verticalVelocity: 5f, swimming: true, T));
            // A treading swimmer that happens to be airborne+falling still treads, not Fall.
            Assert.Equal(LocomotionState.SwimIdle,
                LocomotionStateMachine.Evaluate(0f, grounded: false, verticalVelocity: -4f, swimming: true, T));
        }
    }
}
