using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // The slope-fed render-height smoother in ReplicatedCharacterAnimators: the paced stair-climb SIM is unchanged (it
    // deliberately produces a per-riser vertical sawtooth - a ~120-140 mm peak-to-peak render-Y bob at 4-9 Hz on a
    // 0.30/0.40 staircase), and this bridge turns that raw bob into a smooth glide up the stair slope for the drawn model
    // AND a follow camera (CharacterPose.RenderPosition), WITHOUT the feet-float lag a plain low-pass would cost. These
    // drive synthetic per-tick position streams shaped like the measured stair profile through the bridge and assert on
    // the baked render-Y. All GPU-free.
    public class StairGlideSmootherTests
    {
        const float Dt = 1f / 30f;   // the investigation's tick
        const float ClimbCap = 3.5f; // MoveTuning.MaxStepClimbSpeed -> a rise tick is 3.5 * 1/30 = 0.1167 m
        const float Walk = 3f, Run = 6f;

        static Skeleton OneBone() => new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });

        static AnimationClip Park(string name)
        {
            var jt = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, 1f, new List<JointTrack> { jt });
        }

        static Dictionary<LocomotionState, AnimationClip> Clips() => new()
        {
            [LocomotionState.Idle] = Park("idle"),
            [LocomotionState.Walk] = Park("walk"),
            [LocomotionState.Run] = Park("run"),
            [LocomotionState.Jump] = Park("jump"),
            [LocomotionState.Fall] = Park("fall"),
        };

        static ReplicatedCharacterAnimators NewAnimators(CharacterAnimatorTuning? tuning = null) =>
            new ReplicatedCharacterAnimators(() => new AnimatedCharacter(OneBone(), Clips(), LocomotionThresholds.Default), tuning);

        // Default tuning with the smoother turned OFF (the escape hatch / pre-feature raw path).
        static CharacterAnimatorTuning SmootherOff()
        {
            CharacterAnimatorTuning t = CharacterAnimatorTuning.Default;
            t.SlopeGlideRate = 0f;
            return t;
        }

        // Exact-movement grounded sample (what Ruinborne feeds for the local player AND remotes): feet position, grounded,
        // vertical velocity ~0 on a paced stair climb (the rise is a position adjustment, not a ballistic velocity).
        static CharacterSample Ground(Vector3 feet, float planarSpeed) =>
            new CharacterSample(1, feet, isLocal: true, grounded: true, verticalVelocity: 0f, planarSpeed: planarSpeed);

        // ---- Synthetic stair profile (the investigation's "7-tick riser cycle: one 0.1167 rise + flat treads") --------
        // Horizontal advances every tick at `speed`, except it PAUSES on the single rise tick of each cycle (the paced
        // step-up holds horizontal while the feet clear the riser); one 3.5 m/s-capped rise per 7-tick cycle. Feet run
        // along +X. `climbSign` = +1 ascent, -1 descent.
        static List<Vector3> StairStream(float speed, int cycles = 16, float climbSign = 1f)
        {
            float hpt = speed * Dt, rise = ClimbCap * Dt * climbSign;
            var pts = new List<Vector3>();
            float x = 0f, y = 0f;
            for (int i = 0; i < 5; i++) { x += hpt; pts.Add(new Vector3(x, y, 0f)); }         // flat approach
            for (int c = 0; c < cycles; c++)
                for (int k = 0; k < 7; k++)
                {
                    if (k != 3) x += hpt;    // horizontal pauses on the rise tick
                    if (k == 3) y += rise;   // one riser rise per cycle
                    pts.Add(new Vector3(x, y, 0f));
                }
            for (int i = 0; i < 5; i++) { x += hpt; pts.Add(new Vector3(x, y, 0f)); }         // flat runout
            return pts;
        }

        // Drive a position stream through the bridge; return the baked render-Y per frame (CharacterPose.RenderPosition.Y).
        static float[] DriveRenderY(ReplicatedCharacterAnimators a, IReadOnlyList<Vector3> stream, float speed)
        {
            var outY = new float[stream.Count];
            for (int i = 0; i < stream.Count; i++)
            {
                a.Update(new[] { Ground(stream[i], speed) }, Dt);
                outY[i] = a.Live[0].RenderPosition.Y;
            }
            return outY;
        }

        // Peak-to-peak of `vals` minus the least-squares line fit of vals-vs-X, over [lo,hi): the deviation from the
        // straight "ramp line" the climb should read as.
        static float RampResidualPeakToPeak(IReadOnlyList<Vector3> pts, IReadOnlyList<float> vals, int lo, int hi)
        {
            int n = hi - lo;
            double mx = 0, mv = 0;
            for (int i = lo; i < hi; i++) { mx += pts[i].X; mv += vals[i]; }
            mx /= n; mv /= n;
            double sxx = 0, sxy = 0;
            for (int i = lo; i < hi; i++) { double dx = pts[i].X - mx; sxx += dx * dx; sxy += dx * (vals[i] - mv); }
            double slope = sxy / sxx, icpt = mv - slope * mx;
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            for (int i = lo; i < hi; i++)
            {
                double r = vals[i] - (slope * pts[i].X + icpt);
                if (r > max) max = r; if (r < min) min = r;
            }
            return (float)(max - min);
        }

        // Mean |vals - trueRamp| over [lo,hi), where trueRamp is the least-squares line fit through the TRUE feet
        // positions (pts.X vs pts.Y). This isolates how far the smoothed height sits from the ideal climb ramp - the
        // feed-forward LAG - independent of the deliberate per-riser sawtooth (which is symmetric about that same ramp).
        static float MeanAbsLagFromTrueRamp(IReadOnlyList<Vector3> pts, IReadOnlyList<float> vals, int lo, int hi)
        {
            int n = hi - lo;
            double mx = 0, my = 0;
            for (int i = lo; i < hi; i++) { mx += pts[i].X; my += pts[i].Y; }
            mx /= n; my /= n;
            double sxx = 0, sxy = 0;
            for (int i = lo; i < hi; i++) { double dx = pts[i].X - mx; sxx += dx * dx; sxy += dx * (pts[i].Y - my); }
            double slope = sxy / sxx, icpt = my - slope * mx;   // the TRUE feet ramp line
            double sum = 0;
            for (int i = lo; i < hi; i++) sum += Math.Abs(vals[i] - (slope * pts[i].X + icpt));
            return (float)(sum / n);
        }

        [Theory]
        [InlineData(Walk)]
        [InlineData(Run)]
        public void StairClimb_RenderY_TracksRampLine_UnderFiftyMillimetres(float speed)
        {
            List<Vector3> stream = StairStream(speed);
            int lo = stream.Count * 15 / 100, hi = stream.Count * 85 / 100;

            // Raw (smoother OFF) is the deliberate sim bob; smoothed (default ON) tracks the ramp.
            var off = NewAnimators(SmootherOff());
            float[] rawY = DriveRenderY(off, stream, speed);
            float rawPp = RampResidualPeakToPeak(stream, rawY, lo, hi);

            var on = NewAnimators();   // default tuning -> smoother ON
            float[] smY = DriveRenderY(on, stream, speed);
            float smPp = RampResidualPeakToPeak(stream, smY, lo, hi);

            Assert.True(rawPp > 0.09f, $"the synthetic raw stair bob should be a real sawtooth (got {rawPp * 1000:F0} mm)");
            Assert.True(smPp < 0.050f, $"speed {speed}: smoothed render-Y bob {smPp * 1000:F1} mm should be under 50 mm (raw {rawPp * 1000:F0} mm)");
            Assert.True(smPp < 0.5f * rawPp, $"speed {speed}: smoothed {smPp * 1000:F1} mm should be well under half the raw {rawPp * 1000:F0} mm");

            // LAG assertion - this is what pins the FEED-FORWARD specifically, not just the smoothness. The feed-forward
            // rides the true climb ramp, so the mean distance from render-Y to that ramp line is under 10 mm (~9 mm).
            // A plain low-pass CANNOT get here: damping the 4-9 Hz bob hard enough to hit the p2p bound above lags a
            // rising signal, so the feet sit ~90 mm BELOW the ramp on a moving climb (measured ~91 mm at 5 rad/s with
            // grade forced to 0). The 20 mm bound sits cleanly between the two, so this goes RED if feed-forward is cut.
            float meanLag = MeanAbsLagFromTrueRamp(stream, smY, lo, hi);
            Assert.True(meanLag < 0.020f, $"speed {speed}: mean render-Y lag off the true ramp {meanLag * 1000:F1} mm should be under 20 mm (feed-forward ~9 mm; a plain low-pass ~91 mm)");

            // Monotone while climbing: the drawn height never dips during the ascent (no downward pop).
            float worstDrop = 0f;
            for (int i = lo + 1; i < hi; i++) worstDrop = MathF.Min(worstDrop, smY[i] - smY[i - 1]);
            Assert.True(worstDrop > -0.001f, $"speed {speed}: render-Y dropped {worstDrop * 1000:F2} mm during the ascent (not monotone)");
        }

        [Fact]
        public void StairDescent_RenderY_TracksRampLine_AndIsMonotoneDown()
        {
            List<Vector3> stream = StairStream(Walk, climbSign: -1f);
            int lo = stream.Count * 15 / 100, hi = stream.Count * 85 / 100;

            var off = NewAnimators(SmootherOff());
            float rawPp = RampResidualPeakToPeak(stream, DriveRenderY(off, stream, Walk), lo, hi);
            var on = NewAnimators();
            float[] smY = DriveRenderY(on, stream, Walk);
            float smPp = RampResidualPeakToPeak(stream, smY, lo, hi);

            Assert.True(smPp < 0.050f, $"descent: smoothed render-Y bob {smPp * 1000:F1} mm should be under 50 mm (raw {rawPp * 1000:F0} mm)");
            float worstRise = 0f;
            for (int i = lo + 1; i < hi; i++) worstRise = MathF.Max(worstRise, smY[i] - smY[i - 1]);
            Assert.True(worstRise < 0.001f, $"descent: render-Y rose {worstRise * 1000:F2} mm (not monotone down)");
        }

        [Fact]
        public void FlatGround_RenderY_EqualsTrueY_ByteClose()
        {
            // On flat ground the grade reads ~0, the feed-forward term is off, and the damp-toward-true is a no-op from the
            // seeded state: render-Y must equal the sample Y exactly (identity, no behaviour change vs the pre-feature bridge).
            var a = NewAnimators();
            float y = 12.5f;   // an arbitrary non-zero flat height
            var x = 0f;
            for (int i = 0; i < 200; i++)
            {
                x += Walk * Dt;
                a.Update(new[] { Ground(new Vector3(x, y, 0f), Walk) }, Dt);
                Assert.Equal(y, a.Live[0].RenderPosition.Y, 6);   // 6 decimal places == byte-close for these magnitudes
            }
        }

        [Fact]
        public void Disabled_RenderY_IsAlwaysTrueY()
        {
            // SlopeGlideRate <= 0 disables the smoother: render-Y is the raw feet-Y even mid-stair (an escape hatch that
            // reproduces the pre-feature bridge exactly).
            var a = NewAnimators(SmootherOff());
            foreach (Vector3 p in StairStream(Walk))
            {
                a.Update(new[] { Ground(p, Walk) }, Dt);
                Assert.Equal(p.Y, a.Live[0].RenderPosition.Y, 6);
            }
        }

        [Fact]
        public void StopMidStair_RenderY_SettlesToTrueTread_NoPersistentFloat()
        {
            // Climb, then stop mid-stair (hold the position). The smoothed height is mid-ramp when the stop lands, then
            // must settle DOWN onto the true tread within a bounded time and stay there (no persistent feet-float).
            var a = NewAnimators();
            List<Vector3> climb = StairStream(Walk, cycles: 6);
            float[] climbY = DriveRenderY(a, climb, Walk);
            Vector3 stopAt = climb[^1];
            float tread = stopAt.Y;

            float offsetAtStop = MathF.Abs(climbY[^1] - tread);
            Assert.True(offsetAtStop > 0.02f, $"expected a real mid-ramp offset at the stop, got {offsetAtStop * 1000:F0} mm");

            float? settleSeconds = null;
            for (int i = 0; i < 90; i++)   // hold up to 3 s
            {
                a.Update(new[] { Ground(stopAt, 0f) }, Dt);
                if (settleSeconds is null && MathF.Abs(a.Live[0].RenderPosition.Y - tread) < 0.003f)
                    settleSeconds = (i + 1) * Dt;
            }
            Assert.NotNull(settleSeconds);
            Assert.True(settleSeconds < 1.5f, $"render-Y should settle onto the tread within ~1.5 s, took {settleSeconds:F2} s");
            Assert.Equal(tread, a.Live[0].RenderPosition.Y, 3);   // no persistent float after the hold
        }

        [Fact]
        public void TeleportSizedGap_SnapsSameFrame()
        {
            // A teleport (a gap beyond SlopeGlideSnapDistance) snaps the render-Y to true on the SAME frame - the epoch
            // teleport hard-cut standard: an authoritative teleport is never smoothed.
            var a = NewAnimators();
            var x = 0f;
            for (int i = 0; i < 30; i++) { x += Walk * Dt; a.Update(new[] { Ground(new Vector3(x, 0f, 0f), Walk) }, Dt); }
            x += Walk * Dt;
            a.Update(new[] { Ground(new Vector3(x, 50f, 0f), Walk) }, Dt);   // +50 m teleport
            Assert.Equal(50f, a.Live[0].RenderPosition.Y, 5);                // snapped same-frame, no crawl
        }

        [Fact]
        public void ShortTeleport_GlidesWithoutReset_CutsWithSnapRenderHeight()
        {
            // A SHORT teleport (a vertical gap UNDER SlopeGlideSnapDistance) is height-identical to a stair riser, so the
            // gap snap never fires and the smoother GLIDES it - the hole SnapRenderHeight closes. Called before the
            // destination frame (as a consumer wires it to the netcode teleport epoch), it hard-cuts that frame instead.
            const float dest = 1.0f;   // 1.0 m < 1.5 m snap distance: a short blink, indistinguishable from a riser by height

            // Without the reset: the destination frame glides, so render-Y is still well below the true height.
            var noReset = NewAnimators();
            var x = 0f;
            for (int i = 0; i < 30; i++) { x += Walk * Dt; noReset.Update(new[] { Ground(new Vector3(x, 0f, 0f), Walk) }, Dt); }
            x += Walk * Dt;
            noReset.Update(new[] { Ground(new Vector3(x, dest, 0f), Walk) }, Dt);
            Assert.True(noReset.Live[0].RenderPosition.Y < dest - 0.3f,
                $"a short teleport should GLIDE without the reset (render-Y {noReset.Live[0].RenderPosition.Y:F3} m, true {dest} m)");

            // With SnapRenderHeight before the same destination frame: hard-cut to the true height same-frame.
            var reset = NewAnimators();
            x = 0f;
            for (int i = 0; i < 30; i++) { x += Walk * Dt; reset.Update(new[] { Ground(new Vector3(x, 0f, 0f), Walk) }, Dt); }
            x += Walk * Dt;
            reset.SnapRenderHeight(1);   // consumer wires this to WorldClient.LocalTeleportEpoch / RemoteTeleports
            reset.Update(new[] { Ground(new Vector3(x, dest, 0f), Walk) }, Dt);
            Assert.Equal(dest, reset.Live[0].RenderPosition.Y, 5);   // cut, no glide
        }

        [Fact]
        public void SnapRenderHeight_UnknownId_IsNoOp()
        {
            // No-op for an id that was never sampled (not yet tracked, or dropped on disconnect): no throw, no effect.
            var a = NewAnimators();
            a.SnapRenderHeight(999);
            a.Update(new[] { Ground(new Vector3(0f, 0f, 0f), Walk) }, Dt);
            Assert.Single(a.Live);
        }

        [Fact]
        public void SnapRenderHeight_IsOneShot_ClimbAfterASnapStillGlides()
        {
            // The reset applies to exactly the next Update; it does not disable the smoother. After a snapped teleport
            // a subsequent stair climb glides again (SmoothedY tracks the ramp, not the raw sawtooth).
            var a = NewAnimators();
            a.SnapRenderHeight(1);   // pending, but there is no entry yet -> no-op; the first Update just seeds
            List<Vector3> stream = StairStream(Walk);
            int lo = stream.Count * 15 / 100, hi = stream.Count * 85 / 100;
            float[] smY = DriveRenderY(a, stream, Walk);
            float smPp = RampResidualPeakToPeak(stream, smY, lo, hi);
            Assert.True(smPp < 0.050f, $"the climb after a snap should still glide (bob {smPp * 1000:F1} mm, want < 50 mm)");
        }

        [Fact]
        public void Airborne_JumpArc_BypassesSmoothing_TracksTrueExactly()
        {
            // A genuine jump/fall (!grounded) bypasses the smoother: the drawn height is the physics height EXACTLY every
            // airborne frame, so the arc stays crisp (never eased like a stair).
            var a = NewAnimators();
            var x = 0f; float y = 0f;
            for (int i = 0; i < 20; i++) { x += Walk * Dt; a.Update(new[] { Ground(new Vector3(x, 0f, 0f), Walk) }, Dt); }
            // Rise then fall, airborne throughout, with a fast vertical velocity.
            float[] arc = { 0.2f, 0.4f, 0.55f, 0.62f, 0.6f, 0.5f, 0.32f, 0.1f };
            foreach (float h in arc)
            {
                x += Walk * Dt; y = h;
                a.Update(new[] { new CharacterSample(1, new Vector3(x, y, 0f), isLocal: true, grounded: false, verticalVelocity: 4f) }, Dt);
                Assert.Equal(y, a.Live[0].RenderPosition.Y, 5);   // airborne -> render-Y == physics Y exactly
            }
        }

        [Fact]
        public void BallisticTakeoff_WhileStillGrounded_SnapsNotSmoothed()
        {
            // The jump-takeoff frame can still read grounded for one tick while the vertical velocity spikes. The ballistic
            // gate (|vertical velocity| over the threshold) snaps that frame so the launch stays crisp.
            var a = NewAnimators();
            var x = 0f;
            for (int i = 0; i < 20; i++) { x += Walk * Dt; a.Update(new[] { Ground(new Vector3(x, 0f, 0f), Walk) }, Dt); }
            x += Walk * Dt;
            // Grounded true but vertical velocity 6 m/s (a launch): must snap to the true height, not ease.
            a.Update(new[] { new CharacterSample(1, new Vector3(x, 0.2f, 0f), isLocal: true, grounded: true, verticalVelocity: 6f) }, Dt);
            Assert.Equal(0.2f, a.Live[0].RenderPosition.Y, 5);
        }

        [Fact]
        public void LedgeWalkOff_Drop_DoesNotFloat()
        {
            // Walk off a ledge: the character goes airborne and the true feet-Y drops fast. The render-Y must follow the
            // drop (bypassed while airborne), never hang in the air above the fall.
            var a = NewAnimators();
            var x = 0f;
            for (int i = 0; i < 20; i++) { x += Walk * Dt; a.Update(new[] { Ground(new Vector3(x, 2f, 0f), Walk) }, Dt); }
            float y = 2f, vy = 0f;
            for (int i = 0; i < 15; i++)   // free fall off the ledge
            {
                vy -= 9.8f * Dt; y += vy * Dt; x += Walk * Dt;
                a.Update(new[] { new CharacterSample(1, new Vector3(x, y, 0f), isLocal: true, grounded: false, verticalVelocity: vy) }, Dt);
                Assert.Equal(y, a.Live[0].RenderPosition.Y, 5);   // tracks the fall exactly, no float
            }
        }

        [Fact]
        public void Swimming_BypassesSmoothing()
        {
            // A swimmer bobs on the surface (vertical medium motion), which is not a stair climb: the smoother bypasses so
            // the swim vertical is drawn as-is.
            var a = NewAnimators();
            var x = 0f;
            float[] bob = { 0.05f, 0.1f, 0.08f, 0.12f, 0.06f, 0.11f };
            foreach (float h in bob)
            {
                x += Walk * Dt;
                a.Update(new[] { new CharacterSample(1, new Vector3(x, h, 0f), isLocal: false, grounded: false, verticalVelocity: 0.3f, planarSpeed: Walk, swimming: true) }, Dt);
                Assert.Equal(h, a.Live[0].RenderPosition.Y, 5);
            }
        }
    }
}
