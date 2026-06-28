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
