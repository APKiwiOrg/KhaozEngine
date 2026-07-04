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
    }
}
