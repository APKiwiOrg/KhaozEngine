using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    public class LocomotionSpeedSyncTests
    {
        [Fact]
        public void Disabled_AlwaysReturnsOne()
        {
            var s = LocomotionSpeedSync.Disabled;
            Assert.Equal(1f, s.RateFor(LocomotionState.Walk, 10f));
            Assert.Equal(1f, s.RateFor(LocomotionState.Run, 10f));
            // default(struct) is also disabled.
            Assert.Equal(1f, default(LocomotionSpeedSync).RateFor(LocomotionState.Run, 10f));
        }

        [Fact]
        public void Walk_AdvancesProportionalToSpeed()
        {
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f);
            Assert.Equal(2f, s.RateFor(LocomotionState.Walk, 4f), 4);   // 4 / 2
            Assert.Equal(1f, s.RateFor(LocomotionState.Walk, 2f), 4);   // authored speed -> 1x
        }

        [Fact]
        public void Run_AdvancesProportionalToSpeed()
        {
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f);
            Assert.Equal(2f, s.RateFor(LocomotionState.Run, 10f), 4);   // 10 / 5
        }

        [Fact]
        public void ClampsAtMaxMultiplier()
        {
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f);   // default max 3.0
            Assert.Equal(3f, s.RateFor(LocomotionState.Run, 1000f), 4);   // 200 raw -> clamp 3
        }

        [Fact]
        public void ClampsAtMinMultiplier()
        {
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 10f, runClipSpeed: 5f);   // default min 0.25
            Assert.Equal(0.25f, s.RateFor(LocomotionState.Walk, 1f), 4);   // 0.1 raw -> clamp 0.25
        }

        [Fact]
        public void CustomBounds_AreHonoured()
        {
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f, minMultiplier: 0.5f, maxMultiplier: 1.5f);
            Assert.Equal(1.5f, s.RateFor(LocomotionState.Run, 100f), 4);   // clamp to custom max
            Assert.Equal(0.5f, s.RateFor(LocomotionState.Walk, 0.1f), 4);  // clamp to custom min
        }

        [Fact]
        public void IdleAndAirStates_AlwaysOne_EvenWhenEnabled()
        {
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f);
            Assert.Equal(1f, s.RateFor(LocomotionState.Idle, 10f));
            Assert.Equal(1f, s.RateFor(LocomotionState.Jump, 10f));
            Assert.Equal(1f, s.RateFor(LocomotionState.Fall, 10f));
        }

        [Fact]
        public void Swim_AdvancesProportionalToSpeed_TreadAlwaysOne()
        {
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f, swimClipSpeed: 2.5f);
            Assert.Equal(2f, s.RateFor(LocomotionState.Swim, 5f), 4);       // 5 / 2.5
            Assert.Equal(1f, s.RateFor(LocomotionState.Swim, 2.5f), 4);     // authored speed -> 1x
            Assert.Equal(1f, s.RateFor(LocomotionState.SwimIdle, 10f));     // tread always 1x
        }

        [Fact]
        public void Swim_UnsetReference_PlaysAtOne()
        {
            // The default Enable (no swimClipSpeed) leaves Swim at 0 -> 1x, so a pre-swim consumer is unchanged.
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f);
            Assert.Equal(1f, s.RateFor(LocomotionState.Swim, 10f));
        }

        [Fact]
        public void UnsetReferenceSpeed_PlaysAtOne()
        {
            // Enabled but the state's reference speed is 0 -> avoid divide-by-zero, play at 1x.
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 0f, runClipSpeed: 5f);
            Assert.Equal(1f, s.RateFor(LocomotionState.Walk, 4f));
            Assert.Equal(2f, s.RateFor(LocomotionState.Run, 10f), 4);   // Run still syncs
        }

        [Fact]
        public void ZeroBounds_FallBackToDefaults()
        {
            // Field-init (not via Enable) leaving bounds at 0 must not clamp everything to zero.
            var s = new LocomotionSpeedSync { Enabled = true, WalkClipSpeed = 2f, RunClipSpeed = 5f };
            Assert.Equal(2f, s.RateFor(LocomotionState.Run, 10f), 4);      // within default [0.25, 3]
            Assert.Equal(3f, s.RateFor(LocomotionState.Run, 1000f), 4);    // default max 3
            Assert.Equal(0.25f, s.RateFor(LocomotionState.Walk, 0.1f), 4); // default min 0.25 (0.05 raw)
        }
    }
}
