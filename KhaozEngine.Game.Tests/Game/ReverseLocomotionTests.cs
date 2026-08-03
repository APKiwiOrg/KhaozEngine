using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    /// <summary>Reverse locomotion playback (#485): a backpedal plays its move clip BACKWARDS at the speed-matched
    /// rate instead of moonwalking. Opt-in end to end, so the off path is pinned byte-identical, and the sign reaches
    /// the playhead only, so the state selection is pinned unchanged.</summary>
    public class ReverseLocomotionTests
    {
        static Skeleton OneBone() =>
            new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });

        // A clip whose bone0 X translation ramps X(t) = t over the whole duration, so the composed pose's X reads the
        // clip PLAYHEAD back directly (the AnimatedCharacterTests idiom). Duration is short enough here that a reverse
        // playhead wraps through zero within a test, which is the wrap this feature depends on.
        static AnimationClip Ramp(string name, float duration)
        {
            var jt = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, duration },
                    new[] { new Vector3(0, 0, 0), new Vector3(duration, 0, 0) }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, duration, new List<JointTrack> { jt });
        }

        static Dictionary<LocomotionState, AnimationClip> RampClips(float duration = 100f) => new()
        {
            [LocomotionState.Idle] = Ramp("idle", duration),
            [LocomotionState.Walk] = Ramp("walk", duration),
            [LocomotionState.Run] = Ramp("run", duration),
            [LocomotionState.Jump] = Ramp("jump", duration),
            [LocomotionState.Fall] = Ramp("fall", duration),
            [LocomotionState.SwimIdle] = Ramp("swimIdle", duration),
            [LocomotionState.Swim] = Ramp("swim", duration),
        };

        static LocomotionSpeedSync Reversing() =>
            LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f, reverseOnReverseSector: true);

        static float Playhead(AnimatedCharacter c) => c.Pose[0].Translation.X;

        // ---- LocomotionSpeedSync: the sign is applied to the clamped magnitude ------------------------------------

        [Fact]
        public void RateFor_ReverseSector_NegatesTheClampedMagnitude()
        {
            var s = Reversing();
            // Walk clip authored for 2 m/s, driven at 4 m/s: magnitude 2, sign flipped by the reverse sector.
            Assert.Equal(2f, s.RateFor(LocomotionState.Walk, 4f, MoveSector.Forward), 4);
            Assert.Equal(-2f, s.RateFor(LocomotionState.Walk, 4f, MoveSector.Reverse), 4);
            // Strafe is not reverse: it plays forwards like any other non-reverse sector.
            Assert.Equal(2f, s.RateFor(LocomotionState.Walk, 4f, MoveSector.Strafe), 4);
        }

        [Fact]
        public void RateFor_ReverseSector_ClampsTheMagnitudeThenSigns()
        {
            var s = Reversing();
            // The min floor bounds the MAGNITUDE, so a near-stationary backpedal crawls backwards at -0.25x rather
            // than freezing at 0 (which is what clamping the signed value into [0.25, 3] would do).
            Assert.Equal(-0.25f, s.RateFor(LocomotionState.Walk, 0.01f, MoveSector.Reverse), 4);
            // The max ceiling likewise: 200x raw -> -3x, not -200x.
            Assert.Equal(-3f, s.RateFor(LocomotionState.Run, 1000f, MoveSector.Reverse), 4);
        }

        [Fact]
        public void RateFor_ReverseSector_LeavesTheOneRateStatesAtPlusOne()
        {
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f, swimClipSpeed: 2.5f,
                reverseOnReverseSector: true);
            // Idle, the tread, and the air states have no direction to reverse: they stay at +1, never -1.
            Assert.Equal(1f, s.RateFor(LocomotionState.Idle, 4f, MoveSector.Reverse));
            Assert.Equal(1f, s.RateFor(LocomotionState.SwimIdle, 4f, MoveSector.Reverse));
            Assert.Equal(1f, s.RateFor(LocomotionState.Jump, 4f, MoveSector.Reverse));
            Assert.Equal(1f, s.RateFor(LocomotionState.Fall, 4f, MoveSector.Reverse));
            // A state whose reference speed is unset also plays at +1x, sector or not (no divide, nothing to sign).
            var noWalkRef = LocomotionSpeedSync.Enable(walkClipSpeed: 0f, runClipSpeed: 5f, reverseOnReverseSector: true);
            Assert.Equal(1f, noWalkRef.RateFor(LocomotionState.Walk, 4f, MoveSector.Reverse));
            // The forward swim stroke DOES reverse (a backstroke), since it is a syncing move state.
            Assert.Equal(-2f, s.RateFor(LocomotionState.Swim, 5f, MoveSector.Reverse), 4);
        }

        [Fact]
        public void RateFor_WithoutTheOptIn_IgnoresTheReverseSector()
        {
            // Speed sync on, reverse opt-in OFF: the reverse sector must change nothing.
            var s = LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f);
            Assert.Equal(2f, s.RateFor(LocomotionState.Walk, 4f, MoveSector.Reverse), 4);
            // And with the whole sync disabled it is 1x whatever the sector, as before.
            Assert.Equal(1f, LocomotionSpeedSync.Disabled.RateFor(LocomotionState.Walk, 4f, MoveSector.Reverse));
        }

        [Fact]
        public void RateFor_SectorFreeOverload_IsTheForwardSector()
        {
            var s = Reversing();
            foreach (LocomotionState st in new[] { LocomotionState.Walk, LocomotionState.Run, LocomotionState.Idle })
                Assert.Equal(s.RateFor(st, 4f, MoveSector.Forward), s.RateFor(st, 4f));
        }

        // ---- AnimatedCharacter: the playhead runs backwards, the state does not move ------------------------------

        [Fact]
        public void ReverseSector_RunsThePlayheadBackwards_AtTheSpeedMatchedRate()
        {
            var c = new AnimatedCharacter(OneBone(), RampClips(), crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: Reversing());
            const float dt = 1f / 60f;

            // Walk forwards for 1 s at 4 m/s (walk clip authored for 2 m/s -> 2x): the playhead lands near 2.
            for (int i = 0; i < 60; i++) c.Update(4f, true, 0f, swimming: false, MoveSector.Forward, dt);
            float forwardEnd = Playhead(c);
            Assert.True(MathF.Abs(forwardEnd - 2f) < 2e-2f, forwardEnd.ToString());

            // Now backpedal at the same speed for half a second: the playhead must run BACK toward zero at 2x.
            float prev = forwardEnd;
            for (int i = 0; i < 30; i++)
            {
                c.Update(4f, true, 0f, swimming: false, MoveSector.Reverse, dt);
                float now = Playhead(c);
                Assert.True(now < prev, $"playhead did not decrease: {prev} -> {now}");
                prev = now;
            }
            // 0.5 s at 2x backwards from ~2.0 -> ~1.0.
            Assert.True(MathF.Abs(prev - 1f) < 3e-2f, prev.ToString());
        }

        [Fact]
        public void ReverseSector_WrapsThePlayheadThroughZero_OntoTheClipTail()
        {
            // A short clip so the reverse playhead crosses zero within the test. Reverse from the very first frame:
            // the playhead starts at 0, so the first backward step must wrap onto the clip's TAIL, not clamp or go
            // negative (AnimationSampler.Wrap's negative branch is what carries this).
            var c = new AnimatedCharacter(OneBone(), RampClips(duration: 1f), crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: Reversing());
            const float dt = 1f / 60f;

            c.Update(4f, true, 0f, swimming: false, MoveSector.Reverse, dt);   // one backward frame from 0
            float wrapped = Playhead(c);
            // 2x backwards for one 60 Hz frame = -1/30 s, wrapping into a 1 s clip -> ~0.9667.
            Assert.True(wrapped > 0.9f && wrapped < 1f, wrapped.ToString());

            // Keep going: it walks down the tail, never leaving [0, duration).
            for (int i = 0; i < 120; i++)
            {
                c.Update(4f, true, 0f, swimming: false, MoveSector.Reverse, dt);
                float now = Playhead(c);
                Assert.InRange(now, 0f, 1f);
            }
        }

        [Fact]
        public void ReverseSector_DoesNotChangeTheLocomotionState()
        {
            // The sign reaches the playhead only: a reverse walk is still Walk and a reverse run is still Run. A
            // negative speed reaching LocomotionStateMachine.Evaluate would read as Idle, which is exactly what this
            // pins against.
            var c = new AnimatedCharacter(OneBone(), RampClips(), crossfade: 0f, stateDebounceSeconds: 0f,
                speedSync: Reversing());
            const float dt = 1f / 60f;

            for (int i = 0; i < 30; i++) c.Update(4f, true, 0f, swimming: false, MoveSector.Reverse, dt);
            Assert.Equal(LocomotionState.Walk, c.State);

            for (int i = 0; i < 30; i++) c.Update(12f, true, 0f, swimming: false, MoveSector.Reverse, dt);
            Assert.Equal(LocomotionState.Run, c.State);

            // Airborne while reversing is still the air state, and a genuine stop is still Idle.
            c.Update(4f, false, 5f, swimming: false, MoveSector.Reverse, dt);
            Assert.Equal(LocomotionState.Jump, c.State);
            for (int i = 0; i < 30; i++) c.Update(0f, true, 0f, swimming: false, MoveSector.Reverse, dt);
            Assert.Equal(LocomotionState.Idle, c.State);
        }

        [Fact]
        public void ReverseSector_WithoutTheOptIn_IsByteIdenticalToForward()
        {
            // Compat pin: speed sync on, reverse opt-in OFF. Driving the reverse sector must produce the identical
            // pose, frame for frame, to driving the forward one.
            var reverse = new AnimatedCharacter(OneBone(), RampClips(duration: 1f),
                speedSync: LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f));
            var forward = new AnimatedCharacter(OneBone(), RampClips(duration: 1f),
                speedSync: LocomotionSpeedSync.Enable(walkClipSpeed: 2f, runClipSpeed: 5f));
            const float dt = 1f / 60f;
            for (int i = 0; i < 90; i++)
            {
                reverse.Update(4f, true, 0f, swimming: false, MoveSector.Reverse, dt);
                forward.Update(4f, true, 0f, swimming: false, MoveSector.Forward, dt);
            }
            Assert.Equal(forward.State, reverse.State);
            Assert.Equal(forward.Pose[0], reverse.Pose[0]);   // byte-identical matrix
        }

        [Fact]
        public void SectorOverload_ForwardSector_IsByteIdenticalToTheSectorFreeOverload()
        {
            // Compat pin: the pre-sector Update path must be exactly the new one at MoveSector.Forward, opt-in or not.
            var sectored = new AnimatedCharacter(OneBone(), RampClips(duration: 1f), speedSync: Reversing());
            var legacy = new AnimatedCharacter(OneBone(), RampClips(duration: 1f), speedSync: Reversing());
            const float dt = 1f / 60f;
            (float sp, bool gr, float vv)[] seq =
            {
                (0f, true, 0f), (3f, true, 0f), (6f, true, 0f), (0f, false, 5f), (0f, false, -5f), (0f, true, 0f),
            };
            for (int rep = 0; rep < 20; rep++)
                foreach ((float sp, bool gr, float vv) in seq)
                {
                    sectored.Update(sp, gr, vv, swimming: false, MoveSector.Forward, dt);
                    legacy.Update(sp, gr, vv, dt);
                }
            Assert.Equal(legacy.State, sectored.State);
            Assert.Equal(legacy.Pose[0], sectored.Pose[0]);   // byte-identical matrix
        }

        // ---- CharacterSample + the tuning: the defaults, and the sector riding the bridge ------------------------

        [Fact]
        public void TuningDefault_LeavesReverseOff_AndTheSyncItBuildsAgrees()
        {
            Assert.False(CharacterAnimatorTuning.Default.ReverseLocomotionOnReverseSector);
            Assert.False(default(CharacterAnimatorTuning).ReverseLocomotionOnReverseSector);
            // Off in the LocomotionSpeedSync the tuning builds, even with the speed sync itself enabled.
            var synced = CharacterAnimatorTuning.Default;
            synced.SyncLocomotionToSpeed = true;
            synced.WalkClipSpeed = 2f;
            synced.RunClipSpeed = 5f;
            Assert.False(synced.SpeedSync().ReverseOnReverseSector);
            synced.ReverseLocomotionOnReverseSector = true;
            Assert.True(synced.SpeedSync().ReverseOnReverseSector);
            // And the field never survives a disabled sync (there is no rate to sign).
            var unsynced = CharacterAnimatorTuning.Default;
            unsynced.ReverseLocomotionOnReverseSector = true;
            Assert.False(unsynced.SpeedSync().ReverseOnReverseSector);
        }

        [Fact]
        public void SampleDefaultsToForward_AndWithSectorPreservesEveryOtherField()
        {
            Assert.Equal(MoveSector.Forward, new CharacterSample(1, Vector3.One).Sector);
            Assert.Equal(MoveSector.Forward, new CharacterSample(1, Vector3.One, true, true, -2f, 3f).Sector);
            Assert.Equal(MoveSector.Forward, default(CharacterSample).Sector);

            var full = new CharacterSample(7, new Vector3(1, 2, 3), isLocal: true, grounded: true,
                    verticalVelocity: -1.5f, planarSpeed: 4f, swimming: true, climbRate: 0.5f, stepCumulativeY: 2.5f)
                .WithFacingYaw(1.25f).WithDowned(true);
            CharacterSample reversed = full.WithSector(MoveSector.Reverse);

            Assert.Equal(MoveSector.Reverse, reversed.Sector);
            Assert.Equal(full.Id, reversed.Id);
            Assert.Equal(full.Position, reversed.Position);
            Assert.Equal(full.IsLocal, reversed.IsLocal);
            Assert.Equal(full.HasMovement, reversed.HasMovement);
            Assert.Equal(full.Grounded, reversed.Grounded);
            Assert.Equal(full.VerticalVelocity, reversed.VerticalVelocity);
            Assert.Equal(full.Swimming, reversed.Swimming);
            Assert.Equal(full.ClimbRate, reversed.ClimbRate);
            Assert.Equal(full.HasPlanarSpeed, reversed.HasPlanarSpeed);
            Assert.Equal(full.PlanarSpeed, reversed.PlanarSpeed);
            Assert.Equal(full.FacingYaw, reversed.FacingYaw);
            Assert.Equal(full.StepCumulativeY, reversed.StepCumulativeY);
            Assert.Equal(full.Downed, reversed.Downed);

            // The other two With* builders carry the sector through rather than resetting it.
            Assert.Equal(MoveSector.Reverse, reversed.WithFacingYaw(0.5f).Sector);
            Assert.Equal(MoveSector.Reverse, reversed.WithDowned(false).Sector);
        }

        [Fact]
        public void Bridge_CarriesTheSampleSectorEndToEnd_WithoutDisturbingThePlanarSpeedClamp()
        {
            // End to end through ReplicatedCharacterAnimators: the sample's sector must reach the brain's playhead.
            // Both sets are built from the skeleton+clips ctor so the tuning's SpeedSync() is what configures them.
            var tuning = CharacterAnimatorTuning.Default;
            tuning.SyncLocomotionToSpeed = true;
            tuning.WalkClipSpeed = 2f;
            tuning.RunClipSpeed = 5f;
            tuning.Crossfade = 0f;
            tuning.StateDebounceSeconds = 0f;
            tuning.ReverseLocomotionOnReverseSector = true;

            var reversing = new ReplicatedCharacterAnimators(OneBone(), RampClips(), tuning);
            CharacterAnimatorTuning off = tuning;
            off.ReverseLocomotionOnReverseSector = false;
            var control = new ReplicatedCharacterAnimators(OneBone(), RampClips(), off);

            const float dt = 1f / 60f;
            // The exact-speed (local player) sample shape, backpedaling at 4 m/s. Position stands still so nothing but
            // the exact planar speed and the sector drive the brain.
            CharacterSample Backpedal() =>
                new CharacterSample(1, Vector3.Zero, isLocal: true, grounded: true, verticalVelocity: 0f, planarSpeed: 4f)
                    .WithSector(MoveSector.Reverse);

            for (int i = 0; i < 30; i++)
            {
                reversing.Update(new[] { Backpedal() }, dt);
                control.Update(new[] { Backpedal() }, dt);
            }

            // Same state on both (the sign never reaches the state machine), opposite playheads.
            Assert.Equal(LocomotionState.Walk, reversing.Live[0].State);
            Assert.Equal(LocomotionState.Walk, control.Live[0].State);

            float reversed = reversing.BrainFor(1)!.Pose[0].Translation.X;
            float forward = control.BrainFor(1)!.Pose[0].Translation.X;
            // 0.5 s at 2x forwards -> ~1.0; backwards from 0 it wraps to the tail of the 100 s clip -> ~99.0.
            Assert.True(MathF.Abs(forward - 1f) < 3e-2f, forward.ToString());
            Assert.True(MathF.Abs(reversed - 99f) < 3e-2f, reversed.ToString());

            // The ingest's non-negative PlanarSpeed clamp is untouched: a negative exact speed still reads as 0 (Idle),
            // whatever the sector. The sign travels as the sector, never as the speed.
            var negative = new ReplicatedCharacterAnimators(OneBone(), RampClips(), tuning);
            for (int i = 0; i < 30; i++)
                negative.Update(new[]
                {
                    new CharacterSample(1, Vector3.Zero, isLocal: true, grounded: true, verticalVelocity: 0f,
                        planarSpeed: -4f).WithSector(MoveSector.Reverse),
                }, dt);
            Assert.Equal(LocomotionState.Idle, negative.Live[0].State);
        }
    }
}
