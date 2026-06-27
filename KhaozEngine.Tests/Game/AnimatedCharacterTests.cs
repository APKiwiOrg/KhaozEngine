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
