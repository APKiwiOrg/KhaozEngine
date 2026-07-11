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
            [LocomotionState.SwimIdle] = Park("swimIdle", 6f),
            [LocomotionState.Swim] = Park("swim", 7f),
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
            Settle(c, 12f, true, 0f);
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
            Settle(c, 12f, true, 0f);   // wants Run, only Idle exists
            Assert.Equal(LocomotionState.Run, c.State);
            Assert.True(System.MathF.Abs(c.Pose[0].Translation.X - 1f) < 1e-2f);   // posed by the idle clip
        }

        // Drive with the swimming flag long enough for any crossfade to settle.
        static void SettleSwim(AnimatedCharacter c, float speed)
        {
            for (int i = 0; i < 60; i++) c.Update(speed, grounded: false, verticalVelocity: 0f, swimming: true, 1f / 60f);
        }

        [Fact]
        public void SwimIdle_WhenSwimmingAndStill()
        {
            var c = new AnimatedCharacter(OneBone(), Clips());
            SettleSwim(c, 0f);
            Assert.Equal(LocomotionState.SwimIdle, c.State);
            Assert.True(System.MathF.Abs(c.Pose[0].Translation.X - 6f) < 1e-2f);   // posed by the swimIdle clip
        }

        [Fact]
        public void Swim_WhenSwimmingAndMoving()
        {
            var c = new AnimatedCharacter(OneBone(), Clips());
            SettleSwim(c, 2f);
            Assert.Equal(LocomotionState.Swim, c.State);
            Assert.True(System.MathF.Abs(c.Pose[0].Translation.X - 7f) < 1e-2f);   // posed by the swim clip
        }

        [Fact]
        public void SwimState_CommitsImmediately_EvenWithDebounce()
        {
            // Entering the water must read instantly - swim states are exempt from the ground-state debounce (the
            // enter/exit is already hysteresis-debounced in the movement sim). One swimming frame flips the state,
            // and switching tread<->forward while swimming is also immediate.
            var c = new AnimatedCharacter(OneBone(), Clips());   // default debounce
            Settle(c, 3f, true, 0f);
            Assert.Equal(LocomotionState.Walk, c.State);
            c.Update(2f, grounded: false, verticalVelocity: 0f, swimming: true, 1f / 60f);   // one swimming frame
            Assert.Equal(LocomotionState.Swim, c.State);
            c.Update(0f, grounded: false, verticalVelocity: 0f, swimming: true, 1f / 60f);   // slow to a tread
            Assert.Equal(LocomotionState.SwimIdle, c.State);                                 // immediate, no debounce
            // Exiting swim TO air is also immediate (air states are exempt too): one airborne non-swim frame -> Fall.
            c.Update(0f, grounded: false, verticalVelocity: -3f, swimming: false, 1f / 60f);
            Assert.Equal(LocomotionState.Fall, c.State);
        }

        [Fact]
        public void MissingSwimClips_DegradeToIdle_NoThrow()
        {
            // A consumer that has not baked the water clips yet: swimming must fall back to Idle, not throw.
            var clips = new Dictionary<LocomotionState, AnimationClip>
            {
                [LocomotionState.Idle] = Park("idle", 1f),
                [LocomotionState.Walk] = Park("walk", 2f),
                [LocomotionState.Run] = Park("run", 3f),
            };
            var c = new AnimatedCharacter(OneBone(), clips);
            SettleSwim(c, 2f);   // wants Swim, no Swim/SwimIdle clip exists
            Assert.Equal(LocomotionState.Swim, c.State);                          // the STATE is Swim
            Assert.True(System.MathF.Abs(c.Pose[0].Translation.X - 1f) < 1e-2f);  // but posed by the idle fallback clip
        }

        [Fact]
        public void NonSwimming_ByteIdentical_ToPreSwimOverload()
        {
            // Byte-stability pin: the swimming:false path must be bit-identical to the pre-swim overload that takes no
            // swimming argument. Drive the exact same walk/run/jump/idle sequence through both and compare poses.
            var flagged = new AnimatedCharacter(OneBone(), Clips());
            var legacy = new AnimatedCharacter(OneBone(), Clips());
            const float dt = 1f / 60f;
            (float sp, bool gr, float vv)[] seq =
            {
                (0f, true, 0f), (3f, true, 0f), (6f, true, 0f), (0f, false, 5f), (0f, false, -5f), (0f, true, 0f),
            };
            for (int rep = 0; rep < 20; rep++)
                foreach ((float sp, bool gr, float vv) in seq)
                {
                    flagged.Update(sp, gr, vv, swimming: false, dt);   // explicit false
                    legacy.Update(sp, gr, vv, dt);                     // pre-swim overload
                }
            Assert.Equal(legacy.State, flagged.State);
            Assert.Equal(legacy.Pose[0], flagged.Pose[0]);   // byte-identical matrix
        }

        // A clip where bone0's X translation ramps as X(t) = t (slope 1, long duration so it never wraps in a test),
        // so the composed pose's X reads back the clip PLAYHEAD directly - how far the clip has advanced.
        static AnimationClip Ramp(string name, float duration = 100f)
        {
            var jt = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, duration }, new[] { new Vector3(0, 0, 0), new Vector3(duration, 0, 0) }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, duration, new List<JointTrack> { jt });
        }

        static Dictionary<LocomotionState, AnimationClip> RampClips() => new()
        {
            [LocomotionState.Idle] = Ramp("idle"),
            [LocomotionState.Walk] = Ramp("walk"),
            [LocomotionState.Run] = Ramp("run"),
            [LocomotionState.Jump] = Ramp("jump"),
            [LocomotionState.Fall] = Ramp("fall"),
            [LocomotionState.SwimIdle] = Ramp("swimIdle"),
            [LocomotionState.Swim] = Ramp("swim"),
        };

        [Fact]
        public void SpeedSync_Enabled_AdvancesSwimClipProportionalToSpeed_TreadStays1x()
        {
            var clips = RampClips();
            // Swim clip authored for 2.5 m/s; drive at 5 m/s -> the forward Swim clip should advance at ~2x.
            var synced = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f, swimClipSpeed: 2.5f));
            var tread = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f, swimClipSpeed: 2.5f));

            const float dt = 1f / 60f;
            for (int i = 0; i < 60; i++)
            {
                synced.Update(5f, grounded: false, verticalVelocity: 0f, swimming: true, dt);   // forward stroke, 2x
                tread.Update(0f, grounded: false, verticalVelocity: 0f, swimming: true, dt);    // tread, always 1x
            }

            Assert.Equal(LocomotionState.Swim, synced.State);
            Assert.Equal(LocomotionState.SwimIdle, tread.State);
            // Over 1 s: forward Swim playhead ~2.0 (2x), tread SwimIdle ~1.0 (1x, unaffected by speed-sync).
            Assert.True(System.MathF.Abs(synced.Pose[0].Translation.X - 2.0f) < 2e-2f, synced.Pose[0].Translation.X.ToString());
            Assert.True(System.MathF.Abs(tread.Pose[0].Translation.X - 1.0f) < 2e-2f, tread.Pose[0].Translation.X.ToString());
        }

        [Fact]
        public void SpeedSync_Enabled_AdvancesGroundClipProportionalToSpeed()
        {
            var clips = RampClips();
            // Run clip authored for 5 m/s; drive at 10 m/s -> clip should advance at ~2x.
            var synced = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f));
            var control = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f);   // sync OFF

            const float dt = 1f / 60f;
            for (int i = 0; i < 60; i++) { synced.Update(10f, true, 0f, dt); control.Update(10f, true, 0f, dt); }

            Assert.Equal(LocomotionState.Run, synced.State);
            Assert.Equal(LocomotionState.Run, control.State);
            // Over 1 s: control playhead ~1.0 (1x), synced ~2.0 (2x).
            Assert.True(System.MathF.Abs(control.Pose[0].Translation.X - 1.0f) < 2e-2f, control.Pose[0].Translation.X.ToString());
            Assert.True(System.MathF.Abs(synced.Pose[0].Translation.X - 2.0f) < 2e-2f, synced.Pose[0].Translation.X.ToString());
        }

        [Fact]
        public void SpeedSync_ClampsClipRateAtMax()
        {
            var clips = RampClips();
            var synced = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f));   // default max 3.0
            const float dt = 1f / 60f;
            for (int i = 0; i < 60; i++) synced.Update(1000f, true, 0f, dt);   // 200x raw -> clamp 3x
            Assert.Equal(LocomotionState.Run, synced.State);
            Assert.True(System.MathF.Abs(synced.Pose[0].Translation.X - 3.0f) < 3e-2f, synced.Pose[0].Translation.X.ToString());
        }

        [Fact]
        public void SpeedSync_IdleUnaffected()
        {
            var clips = RampClips();
            var synced = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f));
            var control = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f);

            const float dt = 1f / 60f;
            for (int i = 0; i < 60; i++) { synced.Update(0f, true, 0f, dt); control.Update(0f, true, 0f, dt); }

            Assert.Equal(LocomotionState.Idle, synced.State);
            // Idle plays at 1x under sync -> identical playhead to the no-sync control.
            Assert.Equal(control.Pose[0].Translation.X, synced.Pose[0].Translation.X, 4);
        }

        [Fact]
        public void SpeedSync_AirStatesUnaffected()
        {
            var clips = RampClips();
            var synced = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f));
            var control = new AnimatedCharacter(OneBone(), clips, crossfade: 0f, stateDebounceSeconds: 0f);

            const float dt = 1f / 60f;
            // Airborne AND moving fast horizontally: the horizontal speed must NOT scale the air clip.
            for (int i = 0; i < 60; i++) { synced.Update(10f, false, 5f, dt); control.Update(10f, false, 5f, dt); }

            Assert.Equal(LocomotionState.Jump, synced.State);
            Assert.Equal(control.Pose[0].Translation.X, synced.Pose[0].Translation.X, 4);
        }

        [Fact]
        public void SpeedSync_Disabled_ByteIdenticalToNoSync()
        {
            var clips = RampClips();
            // Reference speeds set but Enabled=false: must play exactly like a character with no sync config at all.
            var disabled = new AnimatedCharacter(OneBone(), clips,
                speedSync: new LocomotionSpeedSync { Enabled = false, WalkClipSpeed = 2f, RunClipSpeed = 5f });
            var control = new AnimatedCharacter(OneBone(), clips);   // default (no sync)

            const float dt = 1f / 60f;
            for (int i = 0; i < 90; i++) { disabled.Update(10f, true, 0f, dt); control.Update(10f, true, 0f, dt); }

            Assert.Equal(control.State, disabled.State);
            Assert.Equal(control.Pose[0], disabled.Pose[0]);   // byte-identical matrix
        }
    }
}
