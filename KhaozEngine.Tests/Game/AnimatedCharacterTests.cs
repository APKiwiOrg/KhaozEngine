using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    public class AnimatedCharacterTests
    {
        // One-bone skeleton; each state's clip parks bone0 at a distinct translation so the composed palette
        // identifies which clip is playing.
        static Skeleton OneBone() =>
            new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });

        static AnimationClip Park(string name, float x)
        {
            var jt = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(x, 0, 0), new Vector3(x, 0, 0) }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, 1f, new List<JointTrack> { jt });
        }

        static Dictionary<LocomotionState, AnimationClip> Clips() => new()
        {
            [LocomotionState.Idle] = Park("idle", 1f),
            [LocomotionState.Walk] = Park("walk", 2f),
            [LocomotionState.Run] = Park("run", 3f),
            [LocomotionState.Jump] = Park("jump", 4f),
            [LocomotionState.Fall] = Park("fall", 5f),
        };

        // Drive the same input long enough for any crossfade to settle.
        static void Settle(AnimatedCharacter c, float speed, bool grounded, float vVel)
        {
            for (int i = 0; i < 60; i++) c.Update(speed, grounded, vVel, 1f / 60f);
        }

        [Fact]
        public void Idle_WhenStill()
        {
            var c = new AnimatedCharacter(OneBone(), Clips());
            Settle(c, 0f, true, 0f);
            Assert.Equal(LocomotionState.Idle, c.State);
            Assert.True(System.MathF.Abs(c.Pose[0].Translation.X - 1f) < 1e-2f);
        }

        [Fact]
        public void Run_WhenFast()
        {
            var c = new AnimatedCharacter(OneBone(), Clips());
            Settle(c, 6f, true, 0f);
            Assert.Equal(LocomotionState.Run, c.State);
            Assert.True(System.MathF.Abs(c.Pose[0].Translation.X - 3f) < 1e-2f);
        }

        [Fact]
        public void Jump_WhenAirborneRising()
        {
            var c = new AnimatedCharacter(OneBone(), Clips());
            Settle(c, 0f, false, 5f);
            Assert.Equal(LocomotionState.Jump, c.State);
            Assert.True(System.MathF.Abs(c.Pose[0].Translation.X - 4f) < 1e-2f);
        }

        [Fact]
        public void Pose_LengthMatchesBoneCount()
        {
            var c = new AnimatedCharacter(OneBone(), Clips());
            c.Update(0f, true, 0f, 1f / 60f);
            Assert.Single(c.Pose);
        }

        [Fact]
        public void GroundState_BriefSpike_DoesNotSwitchClip_ButSustainedDoes()
        {
            // A one-frame excursion in the movement signal (e.g. a position-derived speed spike) must NOT switch the
            // ground state and restart the clip; a SUSTAINED change commits after the debounce window.
            var c = new AnimatedCharacter(OneBone(), Clips());   // default debounce
            const float dt = 1f / 60f;
            Settle(c, 3f, true, 0f);
            Assert.Equal(LocomotionState.Walk, c.State);

            c.Update(9f, true, 0f, dt);    // single-frame Run spike
            c.Update(3f, true, 0f, dt);    // back to walk
            c.Update(3f, true, 0f, dt);
            Assert.Equal(LocomotionState.Walk, c.State);   // the spike never committed

            Settle(c, 9f, true, 0f);       // sustained run
            Assert.Equal(LocomotionState.Run, c.State);
        }

        [Fact]
        public void StateDebounceZero_SwitchesGroundStateImmediately()
        {
            var c = new AnimatedCharacter(OneBone(), Clips(), stateDebounceSeconds: 0f);
            const float dt = 1f / 60f;
            for (int i = 0; i < 5; i++) c.Update(3f, true, 0f, dt);
            Assert.Equal(LocomotionState.Walk, c.State);
            c.Update(9f, true, 0f, dt);    // one frame -> immediate Run (pre-7.68.0 behaviour)
            Assert.Equal(LocomotionState.Run, c.State);
        }

        [Fact]
        public void AirState_CommitsImmediately_EvenWithDebounce()
        {
            // A real jump/fall must read instantly - air states are exempt from the ground-state debounce.
            var c = new AnimatedCharacter(OneBone(), Clips());   // default debounce
            Settle(c, 3f, true, 0f);
            Assert.Equal(LocomotionState.Walk, c.State);
            c.Update(3f, false, 5f, 1f / 60f);   // one airborne frame
            Assert.Equal(LocomotionState.Jump, c.State);
        }

        [Fact]
        public void MissingClip_FallsBackToIdle_NoThrow()
        {
            // Only Idle present: a Run state must fall back to Idle rather than throw.
            var clips = new Dictionary<LocomotionState, AnimationClip> { [LocomotionState.Idle] = Park("idle", 1f) };
            var c = new AnimatedCharacter(OneBone(), clips);
            Settle(c, 6f, true, 0f);   // wants Run, only Idle exists
            Assert.Equal(LocomotionState.Run, c.State);
            Assert.True(System.MathF.Abs(c.Pose[0].Translation.X - 1f) < 1e-2f);   // posed by the idle clip
        }
    }
}
