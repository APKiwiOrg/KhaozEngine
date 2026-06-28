using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    public class ReplicatedCharacterAnimatorsTests
    {
        const float Dt = 1f / 30f;

        // One-bone skeleton + per-state parked clips, mirroring AnimatedCharacterTests so the same brain drives here.
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

        // Each new entity gets a fresh brain off a shared (immutable) skeleton + clip map.
        static ReplicatedCharacterAnimators NewAnimators(CharacterAnimatorTuning? tuning = null) =>
            new ReplicatedCharacterAnimators(() => new AnimatedCharacter(OneBone(), Clips(), LocomotionThresholds.Default), tuning);

        static CharacterSample Pos(int id, Vector3 p) => new CharacterSample(id, p);

        [Fact]
        public void Lifecycle_CreatesAndRemovesPerSampleSet()
        {
            var a = NewAnimators();
            a.Update(new[] { Pos(1, Vector3.Zero), Pos(2, new Vector3(5, 0, 0)) }, Dt);
            Assert.Equal(2, a.Live.Count);

            // Entity 2 absent next frame -> its brain is dropped; 1 persists. No leak, no throw.
            a.Update(new[] { Pos(1, Vector3.Zero) }, Dt);
            Assert.Single(a.Live);
            Assert.Equal(1, a.Live[0].Id);
        }

        [Fact]
        public void Locomotion_DerivedFromPositionDelta_IdleWalkRun()
        {
            var a = NewAnimators();
            var pos = Vector3.Zero;

            // Stationary -> zero derived speed -> Idle.
            for (int i = 0; i < 4; i++) a.Update(new[] { Pos(1, pos) }, Dt);
            Assert.Equal(LocomotionState.Idle, a.Live[0].State);

            // 3 m/s along +X (between the 0.1 walk and 4.5 run thresholds) -> Walk.
            for (int i = 0; i < 4; i++) { pos += new Vector3(3f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Assert.Equal(LocomotionState.Walk, a.Live[0].State);

            // 6 m/s along +X (>= the 4.5 run threshold) -> Run.
            for (int i = 0; i < 4; i++) { pos += new Vector3(6f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Assert.Equal(LocomotionState.Run, a.Live[0].State);
        }

        [Fact]
        public void AirState_DerivedVerticalArc_JumpThenFallThenGround()
        {
            var a = NewAnimators();
            var pos = Vector3.Zero;

            // First sample: no previous -> zero derived velocity -> Idle.
            a.Update(new[] { Pos(1, pos) }, Dt);
            Assert.Equal(LocomotionState.Idle, a.Live[0].State);

            // Rising fast (vy = 3 m/s > 0.5 epsilon) -> airborne, Jump.
            for (int i = 0; i < 3; i++) { pos += new Vector3(0, 0.1f, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Assert.Equal(LocomotionState.Jump, a.Live[0].State);

            // Falling fast (vy = -3 m/s) -> airborne, Fall.
            for (int i = 0; i < 3; i++) { pos += new Vector3(0, -0.1f, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Assert.Equal(LocomotionState.Fall, a.Live[0].State);

            // Level (vy ~ 0 < epsilon) -> grounded -> Idle.
            for (int i = 0; i < 3; i++) a.Update(new[] { Pos(1, pos) }, Dt);
            Assert.Equal(LocomotionState.Idle, a.Live[0].State);
        }

        [Fact]
        public void AirState_ExactMovement_HonorsGroundedFlagOverHeuristic()
        {
            var a = NewAnimators();
            var pos = Vector3.Zero;

            // Establish a previous sample (exact movement: grounded).
            a.Update(new[] { new CharacterSample(1, pos, isLocal: true, grounded: true, verticalVelocity: 0f) }, Dt);

            // y rises fast (the heuristic would read Jump) but HasMovement says grounded -> ground state (Idle, speed 0).
            for (int i = 0; i < 3; i++)
            {
                pos += new Vector3(0, 0.1f, 0);
                a.Update(new[] { new CharacterSample(1, pos, isLocal: true, grounded: true, verticalVelocity: 3f) }, Dt);
            }
            Assert.Equal(LocomotionState.Idle, a.Live[0].State);
        }

        [Fact]
        public void Facing_AimsAlongHeading_AndHoldsBelowThreshold()
        {
            var a = NewAnimators();
            var pos = Vector3.Zero;

            // Move +X long enough for the smoothed yaw to converge to atan2(+X, 0) = +pi/2.
            for (int i = 0; i < 120; i++) { pos += new Vector3(6f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            // Model rest-forward is +Z; after RotationY(yaw) it must point +X.
            Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd.X > 0.95f, $"expected facing +X, got {fwd}");
            Assert.True(MathF.Abs(fwd.Z) < 0.2f, $"expected little +Z component, got {fwd}");

            // Below MinPlanarSpeedForFacing the yaw must hold, not snap to zero.
            for (int i = 0; i < 10; i++) a.Update(new[] { Pos(1, pos) }, Dt);
            Vector3 held = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(held.X > 0.95f, $"yaw should hold while at rest, got {held}");
        }

        [Fact]
        public void FirstFrame_IsIdle_NoNaN()
        {
            var a = NewAnimators();
            a.Update(new[] { Pos(7, new Vector3(3, 1, -2)) }, Dt);
            CharacterPose p = a.Live[0];
            Assert.Equal(LocomotionState.Idle, p.State);
            Assert.Equal(7, p.Id);
            Assert.False(HasNaN(p.World));
            foreach (Matrix4x4 m in p.Pose) Assert.False(HasNaN(m));
        }

        [Fact]
        public void Plateau_WindowedVelocity_StaysWalk_NoIdleStrobe()
        {
            // 90 fps render over a 30 Hz tick: the position advances one tick-step on every 3rd render
            // frame and is IDENTICAL on the two in-between frames - exactly what ClientPrediction.RenderedState
            // produces once inter-tick interpolation saturates (frac clamps to 1, so the rendered position is
            // constant between Predicts). The windowed velocity (1 tick) must hold the speed across those
            // zero-delta frames so the locomotion state stays Walk. Before the fix the hold frames read speed 0
            // and strobed Idle, restarting the clip every frame.
            const float renderDt = 1f / 90f;
            const float tickStep = 3f / 30f;          // 3 m/s (Walk band) advanced once per 30 Hz tick
            var a = NewAnimators();

            var pos = Vector3.Zero;
            var states = new List<LocomotionState>();
            for (int i = 0; i < 90; i++)              // 30 ticks worth of render frames
            {
                if (i > 0 && i % 3 == 0) pos += new Vector3(tickStep, 0, 0);   // move on every 3rd frame, hold between
                a.Update(new[] { Pos(1, pos) }, renderDt);
                states.Add(a.Live[0].State);
            }

            // After the first window warms the velocity up, the state is steady Walk on every frame, including
            // the zero-delta hold frames - no Idle strobe.
            for (int i = 6; i < states.Count; i++)
                Assert.Equal(LocomotionState.Walk, states[i]);
        }

        [Fact]
        public void Plateau_RealStop_SettlesToIdle()
        {
            // Walking via the same plateau stream, then a GENUINE stop (position held longer than one window)
            // must settle to Idle - the fix must not make a stopped character keep "walking".
            const float renderDt = 1f / 90f;
            const float tickStep = 3f / 30f;
            var a = NewAnimators();

            var pos = Vector3.Zero;
            for (int i = 0; i < 30; i++)
            {
                if (i > 0 && i % 3 == 0) pos += new Vector3(tickStep, 0, 0);
                a.Update(new[] { Pos(1, pos) }, renderDt);
            }
            Assert.Equal(LocomotionState.Walk, a.Live[0].State);

            // Hold position for well over one window -> windowed speed 0 -> Idle.
            for (int i = 0; i < 12; i++) a.Update(new[] { Pos(1, pos) }, renderDt);
            Assert.Equal(LocomotionState.Idle, a.Live[0].State);
        }

        [Fact]
        public void Plateau_SpeedBands_WindowedSpeedPicksWalkVsRun()
        {
            // The Walk/Run threshold is crossed by the WINDOWED speed, not a single frame's delta (which on a
            // plateau stream spikes high on move frames and is 0 on hold frames).
            const float renderDt = 1f / 90f;

            // Run band: 6 m/s >= the 4.5 run threshold.
            var run = NewAnimators();
            var rp = Vector3.Zero;
            for (int i = 0; i < 30; i++) { if (i > 0 && i % 3 == 0) rp += new Vector3(6f / 30f, 0, 0); run.Update(new[] { Pos(1, rp) }, renderDt); }
            Assert.Equal(LocomotionState.Run, run.Live[0].State);

            // Walk band: 3 m/s between the 0.1 walk and 4.5 run thresholds.
            var walk = NewAnimators();
            var wp = Vector3.Zero;
            for (int i = 0; i < 30; i++) { if (i > 0 && i % 3 == 0) wp += new Vector3(3f / 30f, 0, 0); walk.Update(new[] { Pos(1, wp) }, renderDt); }
            Assert.Equal(LocomotionState.Walk, walk.Live[0].State);
        }

        [Fact]
        public void VelocityWindowSeconds_HoldsVelocityForTheConfiguredDuration()
        {
            // A longer window holds the last velocity across a longer plateau. Establish a walk velocity over a
            // full window, then hold the position for less than the window - the state must stay Walk.
            var tuning = CharacterAnimatorTuning.Default;
            tuning.VelocityWindowSeconds = 0.25f;
            var a = NewAnimators(tuning);

            const float dt = 1f / 60f;
            var pos = Vector3.Zero;
            for (int i = 0; i < 20; i++) { pos += new Vector3(3f * dt, 0, 0); a.Update(new[] { Pos(1, pos) }, dt); }
            Assert.Equal(LocomotionState.Walk, a.Live[0].State);

            // Hold for 10 frames (~0.17 s < 0.25 s window): the held velocity keeps it Walk, not Idle.
            for (int i = 0; i < 10; i++)
            {
                a.Update(new[] { Pos(1, pos) }, dt);
                Assert.Equal(LocomotionState.Walk, a.Live[0].State);
            }
        }

        [Fact]
        public void ConvenienceCtor_AppliesTuningThresholds()
        {
            // RunSpeed lowered to 1 m/s via tuning; the convenience ctor must thread it into the brains it builds.
            CharacterAnimatorTuning tuning = CharacterAnimatorTuning.Default;
            tuning.Locomotion = new LocomotionThresholds(0.1f, 1f);
            var a = new ReplicatedCharacterAnimators(OneBone(), Clips(), tuning);

            var pos = Vector3.Zero;
            for (int i = 0; i < 4; i++) { pos += new Vector3(2f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Assert.Equal(LocomotionState.Run, a.Live[0].State);   // 2 m/s >= the tuned 1 m/s run threshold
        }

        static bool HasNaN(Matrix4x4 m) =>
            float.IsNaN(m.M11 + m.M12 + m.M13 + m.M14 + m.M21 + m.M22 + m.M23 + m.M24 +
                        m.M31 + m.M32 + m.M33 + m.M34 + m.M41 + m.M42 + m.M43 + m.M44);
    }
}
