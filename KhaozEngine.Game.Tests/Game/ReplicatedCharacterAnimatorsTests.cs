using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Netcode;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    public class ReplicatedCharacterAnimatorsTests
    {
        // Minimal predicted state + simulator: integrate a constant velocity command. Isolates
        // ClientPrediction's rendered-position behaviour (the bridge's real input) from movement physics.
        readonly struct WalkState : IPredictedState<WalkState>
        {
            public WalkState(Vector2 pos) { Position = pos; }
            public Vector2 Position { get; }
            public WalkState WithPosition(Vector2 position) => new WalkState(position);
        }

        sealed class ConstVelSim : ITickSimulator<WalkState, Vector2>
        {
            public WalkState Step(in WalkState state, in Vector2 cmd, float dt) => new WalkState(state.Position + cmd * dt);
        }

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
            [LocomotionState.SwimIdle] = Park("swimIdle", 6f),
            [LocomotionState.Swim] = Park("swim", 7f),
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

            // 3 m/s along +X (between the 0.1 walk and 9 run thresholds) -> Walk.
            for (int i = 0; i < 4; i++) { pos += new Vector3(3f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Assert.Equal(LocomotionState.Walk, a.Live[0].State);

            // 12 m/s along +X (>= the 9 run threshold) -> Run.
            for (int i = 0; i < 4; i++) { pos += new Vector3(12f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
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

        // Fullest local-player sample: exact grounded + vertical + planar speed.
        static CharacterSample LocalSpeed(int id, Vector3 p, float planarSpeed) =>
            new CharacterSample(id, p, isLocal: true, grounded: true, verticalVelocity: 0f, planarSpeed: planarSpeed);

        [Fact]
        public void ExplicitPlanarSpeed_DrivesLocomotionState_OverThePositionDelta()
        {
            // The animation half of the decel-to-stop fix. The locomotion state must follow the EXACT planar speed
            // (the clean commanded speed) instead of the finite-differenced render position, in BOTH directions:
            var a = NewAnimators();

            // (1) Speed up from a STATIONARY position: exact speed in the Walk band -> Walk, even though the position
            //     never moves (the derived path would read 0 -> Idle).
            var pos = Vector3.Zero;
            for (int i = 0; i < 8; i++) a.Update(new[] { LocalSpeed(1, pos, 3f) }, Dt);
            Assert.Equal(LocomotionState.Walk, a.Live[0].State);

            // (2) Stop while the position JITTERS: exact speed 0 -> Idle, even though the position wobbles +/- a few cm
            //     each frame (the residual settle). The derived path would read ~1 m/s off that jitter and stay "walking
            //     in place"; the exact speed pins it to Idle with no walk<->idle flicker.
            var states = new List<LocomotionState>();
            for (int i = 0; i < 30; i++)
            {
                pos += new Vector3(i % 2 == 0 ? 0.04f : -0.04f, 0, 0);
                a.Update(new[] { LocalSpeed(1, pos, 0f) }, Dt);
                states.Add(a.Live[0].State);
            }
            for (int i = 10; i < states.Count; i++)   // after the debounce commits
                Assert.Equal(LocomotionState.Idle, states[i]);
        }

        [Fact]
        public void ExplicitPlanarSpeed_Zero_HoldsFacing_ThroughTheStopSettleWobble()
        {
            // The decel-to-stop FACING glitch (distinct from the walk<->idle state flicker). After the stop the
            // rendered position still settles with a tiny residual sag - backward for a few frames, then forward. If
            // facing follows the finite-differenced heading, that sag reads as motion reversing direction and the model
            // spins to chase it (a rapid glitch to face backward / around) before correcting. With an exact planar
            // speed of 0 the facing gate must stay CLOSED for the whole settle, so the yaw holds its last heading.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 120; i++) { pos += new Vector3(6f * Dt, 0, 0); a.Update(new[] { LocalSpeed(1, pos, 6f) }, Dt); }
            Vector3 fwd0 = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd0.X > 0.95f, $"expected facing +X after moving, got {fwd0}");

            // Stop (exact speed 0) while the position sags backward then recovers - the residual settle.
            float[] sag = { -0.03f, -0.03f, -0.02f, -0.01f, 0.01f, 0.02f, 0.03f, 0.02f, 0f, 0f, 0f, 0f };
            foreach (float step in sag)
            {
                pos += new Vector3(step, 0, 0);
                a.Update(new[] { LocalSpeed(1, pos, 0f) }, Dt);
                Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
                Assert.True(fwd.X > 0.9f, $"facing spun during the stop settle (should hold +X): {fwd}");
            }
        }

        [Fact]
        public void ExplicitPlanarSpeed_DerivedPathWouldFlicker_ProvingTheOverrideMatters()
        {
            // Same jittering-position stop, but position-ONLY samples: the derived speed reads the jitter and the state
            // is NOT the steady Idle it should be. This is the flicker the exact-speed override removes.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            var states = new List<LocomotionState>();
            for (int i = 0; i < 30; i++)
            {
                pos += new Vector3(i % 2 == 0 ? 0.04f : -0.04f, 0, 0);
                a.Update(new[] { Pos(1, pos) }, Dt);
                states.Add(a.Live[0].State);
            }
            Assert.Contains(LocomotionState.Walk, states);   // the derived path surfaces the jitter as movement
        }

        [Fact]
        public void ExactSwimmingFlag_DrivesSwimState_OverGroundAndAir()
        {
            // A remote's swim animation rides the replicated MovementState.Swimming bit carried on the sample. A
            // moving swimmer glides horizontally exactly like a walker, so ONLY the exact flag can select the swim
            // clip. Feed a swimming sample with a walk-band planar speed -> Swim, not Walk.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 8; i++)
            {
                pos += new Vector3(3f * Dt, 0, 0);   // 3 m/s planar (Walk band on a land character)
                a.Update(new[] { new CharacterSample(1, pos, isLocal: false, grounded: false, verticalVelocity: 0f, planarSpeed: 3f, swimming: true) }, Dt);
            }
            Assert.Equal(LocomotionState.Swim, a.Live[0].State);

            // Stop swimming forward (planar speed 0, still swimming) -> tread water.
            for (int i = 0; i < 8; i++)
                a.Update(new[] { new CharacterSample(1, pos, isLocal: false, grounded: false, verticalVelocity: 0f, planarSpeed: 0f, swimming: true) }, Dt);
            Assert.Equal(LocomotionState.SwimIdle, a.Live[0].State);
        }

        [Fact]
        public void PositionOnlySample_NeverSwims()
        {
            // A position-only sample (no exact movement) cannot swim: the flag defaults false and the bridge never
            // derives it. Even a fast horizontal glide reads Walk/Run, never Swim.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 8; i++) { pos += new Vector3(3f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Assert.Equal(LocomotionState.Walk, a.Live[0].State);
            Assert.NotEqual(LocomotionState.Swim, a.Live[0].State);
            Assert.NotEqual(LocomotionState.SwimIdle, a.Live[0].State);
        }

        [Fact]
        public void ExactMovement_NotSwimming_StillPicksGroundOrAir()
        {
            // An exact-movement sample with swimming:false must behave exactly as before (ground/air selection). Pin
            // that the default swimming argument on the exact-movement ctors does not perturb the non-swim path.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 6; i++)
            {
                pos += new Vector3(0, 0.1f, 0);
                a.Update(new[] { new CharacterSample(1, pos, isLocal: true, grounded: false, verticalVelocity: 3f) }, Dt);
            }
            Assert.Equal(LocomotionState.Jump, a.Live[0].State);   // airborne rising, not swimming
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

            // After the first window warms the velocity up and the debounce commits Walk, the state is steady Walk
            // on every frame, including the zero-delta hold frames - no Idle strobe.
            for (int i = 20; i < states.Count; i++)
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

            // Hold position for well over one window + the debounce -> windowed speed 0 -> Idle.
            for (int i = 0; i < 24; i++) a.Update(new[] { Pos(1, pos) }, renderDt);
            Assert.Equal(LocomotionState.Idle, a.Live[0].State);
        }

        [Fact]
        public void Plateau_SpeedBands_WindowedSpeedPicksWalkVsRun()
        {
            // The Walk/Run threshold is crossed by the WINDOWED speed, not a single frame's delta (which on a
            // plateau stream spikes high on move frames and is 0 on hold frames).
            const float renderDt = 1f / 90f;

            // Run band: 12 m/s >= the 9 run threshold.
            var run = NewAnimators();
            var rp = Vector3.Zero;
            for (int i = 0; i < 30; i++) { if (i > 0 && i % 3 == 0) rp += new Vector3(12f / 30f, 0, 0); run.Update(new[] { Pos(1, rp) }, renderDt); }
            Assert.Equal(LocomotionState.Run, run.Live[0].State);

            // Walk band: 3 m/s between the 0.1 walk and 9 run thresholds.
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
            for (int i = 0; i < 30; i++) { pos += new Vector3(3f * dt, 0, 0); a.Update(new[] { Pos(1, pos) }, dt); }
            Assert.Equal(LocomotionState.Walk, a.Live[0].State);

            // Hold for 10 frames (~0.17 s < 0.25 s window): the held velocity keeps it Walk, not Idle.
            for (int i = 0; i < 10; i++)
            {
                a.Update(new[] { Pos(1, pos) }, dt);
                Assert.Equal(LocomotionState.Walk, a.Live[0].State);
            }
        }

        [Fact]
        public void RealPrediction_SteadyWalk_StaysWalk_NoPeriodicReset()
        {
            // Faithful repro of the in-engine NetworkedWalkSample path: drive a real ClientPrediction at a
            // non-integer frames-per-tick ratio (144 fps render / 30 Hz tick = 4.8) with a steady 3 m/s walk,
            // sample RenderedState each frame, and feed it through the bridge exactly as the sample does.
            // A steady walk must read a steady Walk - it must never flip to Run (or Idle), which would restart
            // the clip ("resets the animation to frame 0 every few seconds").
            var settings = new PredictionSettings(TickSeconds: 1f / 30f, MaxPendingCommands: 256,
                HardSnapDistance: 100f, CorrectionRate: 8f, CorrectionDeadZone: 0.03f);
            var pred = new ClientPrediction<WalkState, Vector2>(new ConstVelSim(), settings);
            pred.Reset(new WalkState(Vector2.Zero));

            var tuning = CharacterAnimatorTuning.Default;
            tuning.Locomotion = new LocomotionThresholds(0.1f, 4.5f);   // the sample's split (walk 3 / run 6)
            var a = NewAnimators(tuning);

            const float renderDt = 1f / 144f;
            var vel = new Vector2(3f, 0f);   // steady 3 m/s walk, mid Walk band
            float tickAccum = 0f;
            var states = new List<LocomotionState>();
            for (int frame = 0; frame < 2000; frame++)
            {
                tickAccum += renderDt;
                while (tickAccum >= settings.TickSeconds) { pred.Predict(vel); tickAccum -= settings.TickSeconds; }
                pred.AdvancePresentation(renderDt);
                Vector2 p = pred.RenderedState.Position;
                a.Update(new[] { Pos(1, new Vector3(p.X, 0f, p.Y)) }, renderDt);
                states.Add(a.Live[0].State);
            }

            // After warm-up (a few windows) the state must be steady Walk for the rest of the run.
            var after = states.GetRange(40, states.Count - 40);
            var distinct = after.Distinct().ToArray();
            Assert.True(distinct.Length == 1 && distinct[0] == LocomotionState.Walk,
                $"steady walk should stay Walk; saw {string.Join("/", distinct)} " +
                $"(Run frames: {after.Count(s => s == LocomotionState.Run)}, Idle frames: {after.Count(s => s == LocomotionState.Idle)})");
        }

        [Fact]
        public void RealPrediction_WalkWithReconcileBeat_StaysWalk_NoPeriodicReset()
        {
            // The in-engine symptom: every few seconds the avatar's clip resets to frame 0. The local player's
            // rendered position comes from ClientPrediction; SendInput Predicts once per client tick (frac->0, ramps)
            // while Poll Reconciles once per server snapshot (frac->1, collapses the interpolation). The server and
            // client tick on INDEPENDENT clocks, so those two ~30 Hz events beat slowly: a ~1-tick window then
            // occasionally captures ~2 steps (speed -> Run) or ~0 steps (speed -> Idle), flipping the locomotion
            // state and restarting the clip. Reconcile against the exact predicted basis (matching physics, localhost)
            // so ONLY the frac collapse + the beat are exercised, not a real correction.
            var settings = new PredictionSettings(TickSeconds: 1f / 30f, MaxPendingCommands: 256,
                HardSnapDistance: 100f, CorrectionRate: 8f, CorrectionDeadZone: 0.03f);
            var pred = new ClientPrediction<WalkState, Vector2>(new ConstVelSim(), settings);
            pred.Reset(new WalkState(Vector2.Zero));

            var tuning = CharacterAnimatorTuning.Default;
            tuning.Locomotion = new LocomotionThresholds(0.1f, 4.5f);
            var a = NewAnimators(tuning);

            const float renderDt = 1f / 144f;
            var vel = new Vector2(3f, 0f);
            float clientTick = 0f, serverTick = 0f;
            const float clientPeriod = 1f / 30f;
            const float serverPeriod = 1f / 30.05f;   // independent crystal: a slow beat against the client tick
            int seq = 0, recTick = 0;
            var states = new List<LocomotionState>();
            for (int frame = 0; frame < 4000; frame++)   // ~28 s at 144 fps - several beat periods
            {
                serverTick += renderDt;
                while (serverTick >= serverPeriod) { pred.Reconcile(recTick++, pred.PredictedState, seq - 1); serverTick -= serverPeriod; }
                clientTick += renderDt;
                while (clientTick >= clientPeriod) { seq = pred.Predict(vel) + 1; clientTick -= clientPeriod; }
                pred.AdvancePresentation(renderDt);
                Vector2 p = pred.RenderedState.Position;
                a.Update(new[] { Pos(1, new Vector3(p.X, 0f, p.Y)) }, renderDt);
                states.Add(a.Live[0].State);
            }

            var after = states.GetRange(40, states.Count - 40);
            var distinct = after.Distinct().ToArray();
            Assert.True(distinct.Length == 1 && distinct[0] == LocomotionState.Walk,
                $"steady walk should stay Walk through the reconcile beat; saw {string.Join("/", distinct)} " +
                $"(Run frames: {after.Count(s => s == LocomotionState.Run)}, Idle frames: {after.Count(s => s == LocomotionState.Idle)})");
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

        // A ramp clip (X(t) = t) so the composed pose's X reads back the clip playhead - how far it has advanced.
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
        };

        [Fact]
        public void ConvenienceCtor_SyncLocomotionToSpeed_AdvancesRunClipFaster()
        {
            // The convenience ctor must thread the speed-sync tuning fields into the brains it builds: a set with
            // SyncLocomotionToSpeed on (Run clip authored for 5 m/s) driven at 10 m/s advances the Run clip ~2x the
            // playback of an otherwise identical sync-off set. Proves CharacterAnimatorTuning -> LocomotionSpeedSync
            // wiring end-to-end through the position-derived bridge.
            Skeleton skeleton = OneBone();
            Dictionary<LocomotionState, AnimationClip> clips = RampClips();

            CharacterAnimatorTuning tuningOn = CharacterAnimatorTuning.Default;
            tuningOn.Crossfade = 0f;
            tuningOn.StateDebounceSeconds = 0f;
            tuningOn.VelocityWindowSeconds = 0f;   // per-frame derivation: exact 10 m/s from a steady position ramp
            tuningOn.SyncLocomotionToSpeed = true;
            tuningOn.WalkClipSpeed = 2f;
            tuningOn.RunClipSpeed = 5f;

            CharacterAnimatorTuning tuningOff = tuningOn;
            tuningOff.SyncLocomotionToSpeed = false;

            var setOn = new ReplicatedCharacterAnimators(skeleton, clips, tuningOn);
            var setOff = new ReplicatedCharacterAnimators(skeleton, clips, tuningOff);

            const float dt = 1f / 60f;
            const float v = 10f;   // Run band, 2x the 5 m/s run clip speed
            float x = 0f;
            for (int i = 0; i < 120; i++)
            {
                x += v * dt;
                var s = new[] { Pos(1, new Vector3(x, 0, 0)) };
                setOn.Update(s, dt);
                setOff.Update(s, dt);
            }

            Assert.Equal(LocomotionState.Run, setOn.Live[0].State);
            Assert.Equal(LocomotionState.Run, setOff.Live[0].State);

            float on = setOn.Live[0].Pose[0].Translation.X;
            float off = setOff.Live[0].Pose[0].Translation.X;
            Assert.True(off > 0.1f, $"sync-off playhead should have advanced, got {off}");
            Assert.InRange(on / off, 1.8f, 2.2f);   // sync-on ran the Run clip ~2x faster
        }

        static bool HasNaN(Matrix4x4 m) =>
            float.IsNaN(m.M11 + m.M12 + m.M13 + m.M14 + m.M21 + m.M22 + m.M23 + m.M24 +
                        m.M31 + m.M32 + m.M33 + m.M34 + m.M41 + m.M42 + m.M43 + m.M44);

        // --- Explicit facing (server-authoritative facing yaw on the sample) --------------------------------------

        // The world yaw baked into CharacterPose.World: forward = RotationY(yaw) * +Z = (sin yaw, 0, cos yaw).
        static float YawOf(Matrix4x4 world)
        {
            Vector3 f = Vector3.TransformNormal(new Vector3(0, 0, 1), world);
            return MathF.Atan2(f.X, f.Z);
        }

        // Wrap into [-pi, pi], mirroring the source's private WrapPi so the wrap assertions are self-contained.
        static float WrapPi(float a)
        {
            const float twoPi = MathF.PI * 2f;
            a %= twoPi;
            if (a > MathF.PI) a -= twoPi;
            else if (a < -MathF.PI) a += twoPi;
            return a;
        }

        static bool AngleClose(float a, float b, float tol) => MathF.Abs(WrapPi(a - b)) < tol;

        [Fact]
        public void ExplicitFacing_Stationary_TurnsInPlaceTowardIt()
        {
            // The core gap this seam closes: a stationary character (zero position delta -> derived speed 0) can never
            // turn under the derived path (facing holds below MinPlanarSpeedForFacing). With an explicit facing yaw it
            // converges in place via the same LerpAngle smoothing, from the default yaw 0 (facing +Z) to +X.
            var a = NewAnimators();
            var pos = new Vector3(2, 0, -3);   // arbitrary fixed position: it never moves
            float target = MathF.PI / 2f;      // face +X
            for (int i = 0; i < 200; i++)
                a.Update(new[] { new CharacterSample(1, pos).WithFacingYaw(target) }, Dt);

            Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd.X > 0.99f, $"stationary character should turn in place to +X, got {fwd}");
            Assert.True(MathF.Abs(fwd.Z) < 0.05f, $"expected ~0 Z, got {fwd}");
        }

        [Fact]
        public void ExplicitFacing_PositionCtor_TurnsInPlace_SameAsWither()
        {
            // The position+facing convenience constructor is equivalent to a position-only sample + WithFacingYaw.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 200; i++)
                a.Update(new[] { new CharacterSample(1, pos, facingYaw: -MathF.PI / 2f) }, Dt);   // face -X

            Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd.X < -0.99f, $"position+facing ctor should face -X, got {fwd}");
        }

        [Fact]
        public void ExplicitFacing_ConvergesToArbitraryYaw_AndRespectsWrapAcrossPi()
        {
            // Converge to a yaw just under +pi, then flip the target just past -pi (its short path crosses the +/-pi
            // seam). LerpAngle+WrapPi must take the SHORT way (a tiny step over the seam), not unwind the long -6.1 rad
            // way back through 0, and must converge to the new yaw.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 400; i++) a.Update(new[] { new CharacterSample(1, pos).WithFacingYaw(3.05f) }, Dt);
            float yawBefore = YawOf(a.Live[0].World);
            Assert.True(AngleClose(yawBefore, 3.05f, 0.02f), $"should have converged to +3.05, got {yawBefore}");

            // One step toward -3.05: the wrapped delta is ~ +0.18 (short, across the seam), so the first step is small
            // and positive-wrapped - NOT a big negative swing toward 0 (which the long way would produce).
            a.Update(new[] { new CharacterSample(1, pos).WithFacingYaw(-3.05f) }, Dt);
            float step = WrapPi(YawOf(a.Live[0].World) - yawBefore);
            Assert.True(step > 0f && step < 0.1f, $"first step should be a small short-path step across pi, got {step}");

            // And it converges to the new yaw (mod 2pi).
            for (int i = 0; i < 400; i++) a.Update(new[] { new CharacterSample(1, pos).WithFacingYaw(-3.05f) }, Dt);
            Assert.True(AngleClose(YawOf(a.Live[0].World), -3.05f, 0.02f), $"should converge to -3.05, got {YawOf(a.Live[0].World)}");
        }

        [Fact]
        public void ExplicitFacing_WhileMoving_WinsOverTravelDirection()
        {
            // Server authority over derivation: an entity travelling +X (derived heading would face +X) but carrying an
            // explicit facing of +Z must face +Z, while its locomotion state still derives from the motion. Uses the
            // exact-movement (wolf) sample shape + WithFacingYaw, the motivating consumer path.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 200; i++)
            {
                pos += new Vector3(6f * Dt, 0, 0);   // travel +X at 6 m/s
                a.Update(new[]
                {
                    new CharacterSample(1, pos, isLocal: false, grounded: true, verticalVelocity: 0f, swimming: false)
                        .WithFacingYaw(0f)   // explicit facing = +Z (yaw 0)
                }, Dt);
            }

            Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd.Z > 0.99f, $"explicit facing +Z must win over +X travel, got {fwd}");
            Assert.True(MathF.Abs(fwd.X) < 0.05f, $"expected ~0 X, got {fwd}");
            Assert.NotEqual(LocomotionState.Idle, a.Live[0].State);   // locomotion still derives from the motion
        }

        [Fact]
        public void ExplicitFacing_ComposesFacingYawOffset()
        {
            // FacingYawOffset composes on top of the explicit yaw exactly as it does the derived yaw: an explicit facing
            // of 0 with a +pi asset offset points the model -Z.
            var tuning = CharacterAnimatorTuning.Default;
            tuning.FacingYawOffset = MathF.PI;   // asset authored facing -Z
            var a = NewAnimators(tuning);
            var pos = Vector3.Zero;
            for (int i = 0; i < 200; i++) a.Update(new[] { new CharacterSample(1, pos).WithFacingYaw(0f) }, Dt);

            Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd.Z < -0.99f, $"offset must compose on explicit facing (forward -Z), got {fwd}");
        }

        [Fact]
        public void NoExplicitFacing_Moving_DerivesFacingFromTravel_Unchanged()
        {
            // Regression pin: with no explicit facing (null), a moving sample derives facing from travel exactly as
            // before - the else branch is byte-for-byte the old path. Travel +Z -> face +Z.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 200; i++) { pos += new Vector3(0, 0, 6f * Dt); a.Update(new[] { Pos(1, pos) }, Dt); }

            Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd.Z > 0.99f, $"derived facing must follow +Z travel, got {fwd}");
            Assert.True(MathF.Abs(fwd.X) < 0.05f, $"expected ~0 X, got {fwd}");
        }

        [Fact]
        public void NoExplicitFacing_SubThresholdSpeed_StillHoldsYaw()
        {
            // Regression pin: below MinPlanarSpeedForFacing with NO explicit facing the yaw still holds (today's
            // behavior). Converge facing +X by moving, then feed sub-threshold jitter - the yaw must not spin or snap.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 120; i++) { pos += new Vector3(6f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Vector3 moved = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(moved.X > 0.95f, $"expected facing +X after moving, got {moved}");

            for (int i = 0; i < 30; i++)
            {
                pos += new Vector3(i % 2 == 0 ? 0.001f : -0.001f, 0, 0);   // planar speed well below 0.05 m/s
                a.Update(new[] { Pos(1, pos) }, Dt);
            }
            Vector3 held = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(held.X > 0.95f, $"yaw should hold below threshold with no explicit facing, got {held}");
        }

        [Fact]
        public void ExplicitFacing_SeedsSpawnYaw_FacesCorrectlyOnTheFirstFrame()
        {
            // Spawn seeding: a first-observation sample carrying explicit facing renders already facing that yaw on the
            // FIRST frame, instead of turning in from the default yaw 0 over several frames. Without the seed, frame one
            // is only LerpAngle(0, target, YawSmoothing) - a fraction of the way - and here YawSmoothing < 1 (the
            // stationary convergence test above loops to settle), so the seedless first frame would face nowhere near
            // +X. A single Update proves the seed.
            var a = NewAnimators();
            float target = MathF.PI / 2f;   // face +X
            a.Update(new[] { new CharacterSample(1, new Vector3(2, 0, -3)).WithFacingYaw(target) }, Dt);

            Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd.X > 0.999f, $"spawn should already face +X on frame one, got {fwd}");
            Assert.True(MathF.Abs(fwd.Z) < 0.01f, $"expected ~0 Z on frame one, got {fwd}");
        }

        [Fact]
        public void ExplicitFacing_SpawnSeed_ComposesFacingYawOffset_OnTheFirstFrame()
        {
            // The spawn seed composes FacingYawOffset exactly as the running facing target does: an explicit facing of 0
            // with a +pi asset offset spawns facing -Z on frame one (not the seedless partial turn from +Z).
            var tuning = CharacterAnimatorTuning.Default;
            tuning.FacingYawOffset = MathF.PI;   // asset authored facing -Z
            var a = NewAnimators(tuning);
            a.Update(new[] { new CharacterSample(1, Vector3.Zero).WithFacingYaw(0f) }, Dt);

            Vector3 fwd = Vector3.TransformNormal(new Vector3(0, 0, 1), a.Live[0].World);
            Assert.True(fwd.Z < -0.999f, $"spawn seed must compose the offset, facing -Z on frame one, got {fwd}");
        }

        // --- Downed / death pose override -------------------------------------------------------------------------

        // The drawn model's UP axis (local +Y) in world space. 1 == upright, 0 == fully prone (lying flat).
        static float UpY(Matrix4x4 world) => Vector3.TransformNormal(new Vector3(0, 1, 0), world).Y;

        // A brain WITH a baked Downed clip (a ramp so the composed pose X reads back the clamped playhead), so the
        // clip-path (play-once-hold-final-frame) branch is exercised. crossfade 0 so the pose is the downed clip alone.
        static ReplicatedCharacterAnimators NewAnimatorsWithDownedClip(CharacterAnimatorTuning? tuning = null)
        {
            return new ReplicatedCharacterAnimators(() =>
            {
                var clips = Clips();
                clips[LocomotionState.Downed] = Ramp("downed", duration: 0.5f);   // X(t) = t, up to 0.5
                return new AnimatedCharacter(OneBone(), clips, LocomotionThresholds.Default, crossfade: 0f);
            }, tuning);
        }

        [Fact]
        public void Downed_SetClearReset_TransitionsState()
        {
            // The core state machine: not-downed -> downed -> cleared -> downed again. State reads Downed only while
            // the flag is set, and returns to normal locomotion (Idle at rest) when cleared.
            var a = NewAnimators();
            var pos = Vector3.Zero;

            a.Update(new[] { Pos(1, pos) }, Dt);
            Assert.NotEqual(LocomotionState.Downed, a.Live[0].State);

            a.Update(new[] { Pos(1, pos).WithDowned(true) }, Dt);
            Assert.Equal(LocomotionState.Downed, a.Live[0].State);

            a.Update(new[] { Pos(1, pos) }, Dt);   // clear
            Assert.Equal(LocomotionState.Idle, a.Live[0].State);

            a.Update(new[] { Pos(1, pos).WithDowned(true) }, Dt);   // re-set
            Assert.Equal(LocomotionState.Downed, a.Live[0].State);
        }

        [Fact]
        public void Downed_SuppressesLocomotion_EvenWhileMoving()
        {
            // While downed, locomotion selection is suppressed: a moving position stream that would read Walk/Run stays
            // in the Downed pose. Establish Walk first to prove the override wins over an active locomotion state.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 6; i++) { pos += new Vector3(3f * Dt, 0, 0); a.Update(new[] { Pos(1, pos) }, Dt); }
            Assert.Equal(LocomotionState.Walk, a.Live[0].State);

            for (int i = 0; i < 10; i++)
            {
                pos += new Vector3(3f * Dt, 0, 0);   // still "moving"
                a.Update(new[] { Pos(1, pos).WithDowned(true) }, Dt);
                Assert.Equal(LocomotionState.Downed, a.Live[0].State);
            }
        }

        [Fact]
        public void Downed_ProceduralCollapse_Progresses_ThenHoldsProne()
        {
            // No Downed clip -> procedural collapse. The drawn up-axis tips monotonically from upright (~1) to fully
            // prone (~0) over DownedCollapseSeconds, then HOLDS prone. It must read as a body on the floor, not a
            // leaning statue, so the up-axis Y must reach ~0.
            var a = NewAnimators();   // no Downed clip
            var pos = new Vector3(0, 0, 0);
            a.Update(new[] { Pos(1, pos) }, Dt);
            Assert.True(UpY(a.Live[0].World) > 0.99f, "upright before downed");

            var ups = new List<float>();
            for (int i = 0; i < 40; i++)   // 40 * (1/30) ~= 1.33 s, well past the 0.5 s collapse
            {
                a.Update(new[] { Pos(1, pos).WithDowned(true) }, Dt);
                ups.Add(UpY(a.Live[0].World));
            }

            Assert.Equal(LocomotionState.Downed, a.Live[0].State);
            for (int i = 1; i < ups.Count; i++)   // smoothstep ramp: never tips back up
                Assert.True(ups[i] <= ups[i - 1] + 1e-4f, $"collapse must be monotonic, frame {i}: {ups[i - 1]} -> {ups[i]}");
            Assert.True(ups[^1] < 0.02f, $"body must end fully prone (up-axis ~0), got {ups[^1]}");
            for (int i = ups.Count - 5; i < ups.Count; i++)   // and HOLDS prone
                Assert.True(ups[i] < 0.02f, $"prone must hold, frame {i}: {ups[i]}");
        }

        [Fact]
        public void Downed_ProceduralCollapse_SettlesAtGroundLevel_NotFloating()
        {
            // The collapse settles the render height at the true feet-Y (ground), not floating at capsule centre. The
            // sample is feet-anchored, so the drawn translation Y must equal the feet-Y throughout the collapse.
            var a = NewAnimators();
            var feet = new Vector3(2f, 3f, -4f);   // feet at Y = 3
            for (int i = 0; i < 30; i++) a.Update(new[] { Pos(1, feet).WithDowned(true) }, Dt);
            Assert.Equal(3f, a.Live[0].World.Translation.Y, 3);
            Assert.Equal(3f, a.Live[0].RenderPosition.Y, 3);
        }

        [Fact]
        public void Downed_ProceduralCollapse_TopplesInFacingDirection()
        {
            // The body topples FORWARD in its facing direction (not a fixed world axis). Face +X (explicit yaw), go
            // down, and the drawn up-axis must lie toward +X (up.X > 0) once prone - the lateral tip rides the yaw.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            float faceX = MathF.PI / 2f;   // face +X
            for (int i = 0; i < 40; i++)
                a.Update(new[] { new CharacterSample(1, pos, facingYaw: faceX).WithDowned(true) }, Dt);

            Vector3 up = Vector3.TransformNormal(new Vector3(0, 1, 0), a.Live[0].World);
            Assert.True(up.Y < 0.02f, $"should be prone, up.Y = {up.Y}");
            Assert.True(up.X > 0.99f, $"prone body should lie toward its +X facing, up = {up}");
        }

        [Fact]
        public void Downed_ClipPath_PlaysOnce_AndHoldsFinalFrame_WorldUpright()
        {
            // With a Downed clip, the clip plays ONCE and holds its final frame (the clamped ramp reads back its end
            // value 0.5, NOT a wrapped ~0), while the WORLD stays upright (the clip lays the body down in skeleton
            // space, not via a world tip).
            var a = NewAnimatorsWithDownedClip();
            var pos = Vector3.Zero;
            a.Update(new[] { Pos(1, pos) }, Dt);   // establish idle
            for (int i = 0; i < 30; i++) a.Update(new[] { Pos(1, pos).WithDowned(true) }, Dt);   // 1 s > 0.5 s clip

            Assert.Equal(LocomotionState.Downed, a.Live[0].State);
            Assert.Equal(0.5f, a.Live[0].Pose[0].Translation.X, 3);   // held on the final frame, not looped
            Assert.True(UpY(a.Live[0].World) > 0.99f, "clip path keeps the world upright (clip poses the collapse)");
        }

        [Fact]
        public void Downed_ClearWithTeleportSnap_ReturnsToLocomotion_NoResidualNoGlide()
        {
            // Respawn: a downed entity teleports to a new spawn and clears the flag. Wired to SnapRenderHeight (as a
            // teleport respawn is), the clear composes cleanly - upright (no prone residual), at the spawn position and
            // height (no glide from the corpse position), and Idle (the teleport delta does not spin it into Run).
            var a = NewAnimators();
            var down = new Vector3(0f, 0.5f, 0f);   // small gap so only SnapRenderHeight (not the gap snap) makes it crisp
            for (int i = 0; i < 40; i++) a.Update(new[] { Pos(1, down).WithDowned(true) }, Dt);
            Assert.True(UpY(a.Live[0].World) < 0.02f, "prone before respawn");

            var spawn = new Vector3(50f, 0f, -30f);
            a.SnapRenderHeight(1);                              // the respawn teleport signal
            a.Update(new[] { Pos(1, spawn) }, Dt);             // cleared + teleported same frame

            Assert.Equal(LocomotionState.Idle, a.Live[0].State);   // not Run from the teleport delta
            Assert.True(UpY(a.Live[0].World) > 0.99f, "no prone residual after respawn");
            Assert.Equal(spawn.X, a.Live[0].World.Translation.X, 3);
            Assert.Equal(spawn.Z, a.Live[0].World.Translation.Z, 3);
            Assert.Equal(spawn.Y, a.Live[0].RenderPosition.Y, 3);   // snapped to spawn, no glide from Y=0.5
        }

        [Fact]
        public void Downed_ReSet_RestartsCollapseFromUpright()
        {
            // Re-downing after a respawn starts a FRESH collapse, not from the previous prone hold: the first frame of
            // the second downing is near-upright again.
            var a = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 40; i++) a.Update(new[] { Pos(1, pos).WithDowned(true) }, Dt);
            Assert.True(UpY(a.Live[0].World) < 0.02f, "prone after first downing");

            a.SnapRenderHeight(1);
            a.Update(new[] { Pos(1, pos) }, Dt);   // respawn (clear)
            Assert.True(UpY(a.Live[0].World) > 0.99f, "upright after clear");

            a.Update(new[] { Pos(1, pos).WithDowned(true) }, Dt);   // re-down: fresh collapse
            Assert.True(UpY(a.Live[0].World) > 0.98f, "re-down restarts collapse from upright, not the held prone");
        }

        [Fact]
        public void NeverDowned_IsByteIdenticalToWithDownedFalse()
        {
            // Defaults preserve behaviour: WithDowned(false) is byte-for-byte the same as never touching the flag. Feed
            // the same varied stream to two sets and assert the drawn transform and state match every frame.
            var a = NewAnimators();
            var b = NewAnimators();
            var pos = Vector3.Zero;
            for (int i = 0; i < 60; i++)
            {
                pos += new Vector3(4f * Dt, i % 4 == 0 ? 0.05f : -0.01f, 2f * Dt);
                a.Update(new[] { Pos(1, pos) }, Dt);
                b.Update(new[] { Pos(1, pos).WithDowned(false) }, Dt);
                Assert.Equal(a.Live[0].World, b.Live[0].World);
                Assert.Equal(a.Live[0].State, b.Live[0].State);
                Assert.Equal(a.Live[0].Pose[0], b.Live[0].Pose[0]);
            }
        }
    }
}
