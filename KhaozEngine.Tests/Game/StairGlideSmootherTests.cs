using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // E3: the SIGNAL-GATED render-height glide in ReplicatedCharacterAnimators. The glide is driven ENTIRELY by the
    // sim's exported climb rate (CharacterSample.ClimbRate) - never estimated from position deltas. The estimator (grade
    // windows, clamps, the ballistic threshold, the horizontal-motion gate) is deleted: the sim already knows when it is
    // climbing and how fast, so falls / jumps / teleports / platforms carry ClimbRate == 0 and are raw BY CONSTRUCTION.
    // These drive synthetic per-tick streams (a per-riser Y bob shaped like the measured stair profile) through the
    // bridge and assert on the baked render-Y. All GPU-free.
    public class StairGlideSmootherTests
    {
        const float Dt = 1f / 30f;   // the investigation's tick
        const float ClimbCap = 3.5f; // MoveTuning.MaxStepClimbSpeed -> a rise tick is 3.5 * 1/30 = 0.1167 m
        const float Walk = 3f;
        // Mean climb rate of the synthetic 7-tick riser cycle: one ClimbCap*Dt rise per 7 ticks -> ClimbCap/7 m/s. This
        // is the STEADY rate the sim exports (0 = not climbing), the exact value the smoother feeds forward.
        const float MeanClimbRate = ClimbCap / 7f;

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

        static CharacterAnimatorTuning SmootherOff()
        {
            CharacterAnimatorTuning t = CharacterAnimatorTuning.Default;
            t.SlopeGlideRate = 0f;
            return t;
        }

        // Exact-movement grounded sample carrying the sim's signed climb rate (what Ruinborne feeds for the local player
        // AND remotes): feet position, grounded, vertical velocity ~0, planar speed, and ClimbRate (0 = not climbing).
        static CharacterSample Ground(Vector3 feet, float planarSpeed, float climbRate) =>
            new CharacterSample(1, feet, isLocal: true, grounded: true, verticalVelocity: 0f, planarSpeed: planarSpeed, swimming: false, climbRate: climbRate);

        // ---- Synthetic stair profile (the investigation's "7-tick riser cycle: one 0.1167 rise + flat treads") --------
        // Horizontal advances every tick at `speed`, except it PAUSES on the single rise tick of each cycle; one
        // ClimbCap-capped rise per 7-tick cycle. Feet run along +X. `climbSign` = +1 ascent, -1 descent. Every CLIMB
        // tick carries the steady mean climb rate (the sim's exported signal); the flat approach/runout carry 0.
        static List<(Vector3 pos, float climbRate)> StairStream(float speed, int cycles = 16, float climbSign = 1f)
        {
            float hpt = speed * Dt, rise = ClimbCap * Dt * climbSign;
            var pts = new List<(Vector3, float)>();
            float x = 0f, y = 0f;
            for (int i = 0; i < 5; i++) { x += hpt; pts.Add((new Vector3(x, y, 0f), 0f)); }         // flat approach (not climbing)
            for (int c = 0; c < cycles; c++)
                for (int k = 0; k < 7; k++)
                {
                    if (k != 3) x += hpt;    // horizontal pauses on the rise tick
                    if (k == 3) y += rise;   // one riser rise per cycle
                    pts.Add((new Vector3(x, y, 0f), MeanClimbRate * climbSign));   // the sim stamps the steady rate every climb tick
                }
            for (int i = 0; i < 5; i++) { x += hpt; pts.Add((new Vector3(x, y, 0f), 0f)); }         // flat runout (not climbing)
            return pts;
        }

        static float[] DriveRenderY(ReplicatedCharacterAnimators a, IReadOnlyList<(Vector3 pos, float climbRate)> stream, float speed)
        {
            var outY = new float[stream.Count];
            for (int i = 0; i < stream.Count; i++)
            {
                a.Update(new[] { Ground(stream[i].pos, speed, stream[i].climbRate) }, Dt);
                outY[i] = a.Live[0].RenderPosition.Y;
            }
            return outY;
        }

        static float RampResidualPeakToPeak(IReadOnlyList<(Vector3 pos, float climbRate)> pts, IReadOnlyList<float> vals, int lo, int hi)
        {
            int n = hi - lo;
            double mx = 0, mv = 0;
            for (int i = lo; i < hi; i++) { mx += pts[i].pos.X; mv += vals[i]; }
            mx /= n; mv /= n;
            double sxx = 0, sxy = 0;
            for (int i = lo; i < hi; i++) { double dx = pts[i].pos.X - mx; sxx += dx * dx; sxy += dx * (vals[i] - mv); }
            double slope = sxy / sxx, icpt = mv - slope * mx;
            double max = double.NegativeInfinity, min = double.PositiveInfinity;
            for (int i = lo; i < hi; i++)
            {
                double r = vals[i] - (slope * pts[i].pos.X + icpt);
                if (r > max) max = r; if (r < min) min = r;
            }
            return (float)(max - min);
        }

        static float MeanAbsLagFromTrueRamp(IReadOnlyList<(Vector3 pos, float climbRate)> pts, IReadOnlyList<float> vals, int lo, int hi)
        {
            int n = hi - lo;
            double mx = 0, my = 0;
            for (int i = lo; i < hi; i++) { mx += pts[i].pos.X; my += pts[i].pos.Y; }
            mx /= n; my /= n;
            double sxx = 0, sxy = 0;
            for (int i = lo; i < hi; i++) { double dx = pts[i].pos.X - mx; sxx += dx * dx; sxy += dx * (pts[i].pos.Y - my); }
            double slope = sxy / sxx, icpt = my - slope * mx;
            double sum = 0;
            for (int i = lo; i < hi; i++) sum += Math.Abs(vals[i] - (slope * pts[i].pos.X + icpt));
            return (float)(sum / n);
        }

        [Fact]
        public void StairClimb_RenderY_TracksRampLine_UnderFiftyMillimetres()
        {
            List<(Vector3 pos, float climbRate)> stream = StairStream(Walk);
            int lo = stream.Count * 15 / 100, hi = stream.Count * 85 / 100;

            var off = NewAnimators(SmootherOff());
            float[] rawY = DriveRenderY(off, stream, Walk);
            float rawPp = RampResidualPeakToPeak(stream, rawY, lo, hi);

            var on = NewAnimators();   // default tuning -> glide ON
            float[] smY = DriveRenderY(on, stream, Walk);
            float smPp = RampResidualPeakToPeak(stream, smY, lo, hi);

            Assert.True(rawPp > 0.09f, $"the synthetic raw stair bob should be a real sawtooth (got {rawPp * 1000:F0} mm)");
            Assert.True(smPp < 0.050f, $"glided render-Y bob {smPp * 1000:F1} mm should be under 50 mm (raw {rawPp * 1000:F0} mm)");
            Assert.True(smPp < 0.5f * rawPp, $"glided {smPp * 1000:F1} mm should be well under half the raw {rawPp * 1000:F0} mm");

            // Feed-forward at the EXACT sim rate rides the true climb ramp, so the mean distance from render-Y to that
            // ramp line is small (a plain low-pass would lag ~90 mm below it). This pins the feed-forward specifically.
            float meanLag = MeanAbsLagFromTrueRamp(stream, smY, lo, hi);
            Assert.True(meanLag < 0.020f, $"mean render-Y lag off the true ramp {meanLag * 1000:F1} mm should be under 20 mm (a plain low-pass ~90 mm)");

            float worstDrop = 0f;
            for (int i = lo + 1; i < hi; i++) worstDrop = MathF.Min(worstDrop, smY[i] - smY[i - 1]);
            Assert.True(worstDrop > -0.001f, $"render-Y dropped {worstDrop * 1000:F2} mm during the ascent (not monotone)");
        }

        [Fact]
        public void StairDescent_RenderY_TracksRampLine_AndIsMonotoneDown()
        {
            List<(Vector3 pos, float climbRate)> stream = StairStream(Walk, climbSign: -1f);
            int lo = stream.Count * 15 / 100, hi = stream.Count * 85 / 100;

            var off = NewAnimators(SmootherOff());
            float rawPp = RampResidualPeakToPeak(stream, DriveRenderY(off, stream, Walk), lo, hi);
            var on = NewAnimators();
            float[] smY = DriveRenderY(on, stream, Walk);
            float smPp = RampResidualPeakToPeak(stream, smY, lo, hi);

            Assert.True(smPp < 0.050f, $"descent: glided render-Y bob {smPp * 1000:F1} mm should be under 50 mm (raw {rawPp * 1000:F0} mm)");
            float worstRise = 0f;
            for (int i = lo + 1; i < hi; i++) worstRise = MathF.Max(worstRise, smY[i] - smY[i - 1]);
            Assert.True(worstRise < 0.001f, $"descent: render-Y rose {worstRise * 1000:F2} mm (not monotone down)");
        }

        [Fact]
        public void FlatGround_RenderY_EqualsTrueY_ByteClose()
        {
            // Flat ground: ClimbRate == 0 -> raw branch -> render-Y == the sample Y exactly (identity, correct by
            // construction, no behaviour change vs the pre-feature bridge).
            var a = NewAnimators();
            float y = 12.5f;
            var x = 0f;
            for (int i = 0; i < 200; i++)
            {
                x += Walk * Dt;
                a.Update(new[] { Ground(new Vector3(x, y, 0f), Walk, climbRate: 0f) }, Dt);
                Assert.Equal(y, a.Live[0].RenderPosition.Y, 6);
            }
        }

        [Fact]
        public void Disabled_RenderY_IsAlwaysTrueY()
        {
            // SlopeGlideRate <= 0 disables the glide: render-Y is the raw feet-Y even with a climb signal present.
            var a = NewAnimators(SmootherOff());
            foreach ((Vector3 pos, float climbRate) p in StairStream(Walk))
            {
                a.Update(new[] { Ground(p.pos, Walk, p.climbRate) }, Dt);
                Assert.Equal(p.pos.Y, a.Live[0].RenderPosition.Y, 6);
            }
        }

        [Fact]
        public void StopMidStair_RenderY_SnapsToTrueTread_WhenSignalGoesToZero()
        {
            // Climb, then STOP mid-stair: the sim stops stamping a climb rate (ClimbRate -> 0), so the glide disengages
            // and render-Y renders raw - the drawn feet sit on the true tread immediately, no persistent feet-float.
            var a = NewAnimators();
            List<(Vector3 pos, float climbRate)> climb = StairStream(Walk, cycles: 6);
            DriveRenderY(a, climb, Walk);
            Vector3 stopAt = climb[^1].pos;
            float tread = stopAt.Y;

            // First stopped frame (ClimbRate 0): render-Y is the true tread, not the mid-ramp glided height.
            a.Update(new[] { Ground(stopAt, 0f, climbRate: 0f) }, Dt);
            Assert.Equal(tread, a.Live[0].RenderPosition.Y, 5);
            // And it stays there.
            for (int i = 0; i < 30; i++)
            {
                a.Update(new[] { Ground(stopAt, 0f, climbRate: 0f) }, Dt);
                Assert.Equal(tread, a.Live[0].RenderPosition.Y, 5);
            }
        }

        [Fact]
        public void TeleportSizedGap_SnapsSameFrame()
        {
            // A large gap (beyond SlopeGlideSnapDistance) snaps render-Y to true on the SAME frame - and it is not
            // climbing anyway (ClimbRate 0), so it is raw twice over.
            var a = NewAnimators();
            var x = 0f;
            for (int i = 0; i < 30; i++) { x += Walk * Dt; a.Update(new[] { Ground(new Vector3(x, 0f, 0f), Walk, 0f) }, Dt); }
            x += Walk * Dt;
            a.Update(new[] { Ground(new Vector3(x, 50f, 0f), Walk, 0f) }, Dt);   // +50 m teleport
            Assert.Equal(50f, a.Live[0].RenderPosition.Y, 5);
        }

        [Fact]
        public void ShortTeleport_OntoAClimb_GlidesWithoutReset_CutsWithSnapRenderHeight()
        {
            // Belt-and-braces for SnapRenderHeight: a SHORT teleport (under SlopeGlideSnapDistance) onto a position that
            // itself carries a climb signal would GLIDE (height-identical to a stair riser). SnapRenderHeight, wired to
            // the teleport epoch, forces the raw cut that frame. (A short teleport onto NON-climbing ground is raw by
            // construction and needs no hook - that is why this is belt-and-braces.)
            const float dest = 1.0f;   // 1.0 m < 1.5 m snap distance

            // Without the reset: mid-climb, then a short jump to a still-climbing destination -> glides (render-Y lags).
            var noReset = NewAnimators();
            DriveRenderY(noReset, StairStream(Walk, cycles: 4), Walk);
            noReset.Update(new[] { Ground(new Vector3(99f, dest, 0f), Walk, MeanClimbRate) }, Dt);
            Assert.True(noReset.Live[0].RenderPosition.Y < dest - 0.2f,
                $"a short teleport onto a climb should GLIDE without the reset (render-Y {noReset.Live[0].RenderPosition.Y:F3} m, true {dest} m)");

            // With SnapRenderHeight before the same destination frame: hard-cut to the true height same-frame.
            var reset = NewAnimators();
            DriveRenderY(reset, StairStream(Walk, cycles: 4), Walk);
            reset.SnapRenderHeight(1);
            reset.Update(new[] { Ground(new Vector3(99f, dest, 0f), Walk, MeanClimbRate) }, Dt);
            Assert.Equal(dest, reset.Live[0].RenderPosition.Y, 5);
        }

        [Fact]
        public void SnapRenderHeight_UnknownId_IsNoOp()
        {
            var a = NewAnimators();
            a.SnapRenderHeight(999);
            a.Update(new[] { Ground(new Vector3(0f, 0f, 0f), Walk, 0f) }, Dt);
            Assert.Single(a.Live);
        }

        [Fact]
        public void SnapRenderHeight_IsOneShot_ClimbAfterASnapStillGlides()
        {
            var a = NewAnimators();
            a.SnapRenderHeight(1);
            List<(Vector3 pos, float climbRate)> stream = StairStream(Walk);
            int lo = stream.Count * 15 / 100, hi = stream.Count * 85 / 100;
            float[] smY = DriveRenderY(a, stream, Walk);
            float smPp = RampResidualPeakToPeak(stream, smY, lo, hi);
            Assert.True(smPp < 0.050f, $"the climb after a snap should still glide (bob {smPp * 1000:F1} mm, want < 50 mm)");
        }

        [Fact]
        public void Airborne_JumpArc_BypassesGlide_TracksTrueExactly()
        {
            // A genuine jump/fall carries ClimbRate 0 (never a step climb), so it is raw: render-Y == physics Y exactly.
            var a = NewAnimators();
            var x = 0f; float y = 0f;
            for (int i = 0; i < 20; i++) { x += Walk * Dt; a.Update(new[] { Ground(new Vector3(x, 0f, 0f), Walk, 0f) }, Dt); }
            float[] arc = { 0.2f, 0.4f, 0.55f, 0.62f, 0.6f, 0.5f, 0.32f, 0.1f };
            foreach (float h in arc)
            {
                x += Walk * Dt; y = h;
                a.Update(new[] { new CharacterSample(1, new Vector3(x, y, 0f), isLocal: true, grounded: false, verticalVelocity: 4f) }, Dt);
                Assert.Equal(y, a.Live[0].RenderPosition.Y, 5);
            }
        }

        [Fact]
        public void LandingFromFall_RenderY_NeverDipsBelowTrueFeet_TheProdFallSink()
        {
            // THE 1.2 m fall-sink, turned into a standing guard. Climb (so SmoothedY is glided above the true feet), then
            // fall ballistically onto the floor. The fall carries ClimbRate 0, so the glide disengages the instant the
            // fall begins - render-Y snaps to and TRACKS the true feet through the fall, and at touchdown it is exactly
            // the floor. It can NEVER be driven below the floor, because nothing was fed forward during the fall.
            var a = NewAnimators();
            DriveRenderY(a, StairStream(Walk, cycles: 6), Walk);   // build a glided-above-true SmoothedY

            // Now fall from ~2 m onto the floor at Y=0, ClimbRate 0 throughout (a fall is never a climb).
            float x = 100f, y = 2f, vy = 0f;
            float worstDipBelowTrue = 0f;
            for (int i = 0; i < 40; i++)
            {
                vy -= 9.8f * Dt;
                y = MathF.Max(0f, y + vy * Dt);
                bool grounded = y <= 0f;
                if (grounded) vy = 0f;
                x += Walk * Dt;
                a.Update(new[] { new CharacterSample(1, new Vector3(x, y, 0f), isLocal: true, grounded: grounded, verticalVelocity: vy, planarSpeed: Walk, swimming: false, climbRate: 0f) }, Dt);
                float renderY = a.Live[0].RenderPosition.Y;
                worstDipBelowTrue = MathF.Min(worstDipBelowTrue, renderY - y);
                Assert.Equal(y, renderY, 5);   // raw: render-Y tracks the true feet exactly through the whole fall + landing
            }
            // The whole point: the drawn feet never went below the true feet by any margin (the 1.2 m sink is impossible).
            Assert.True(worstDipBelowTrue > -0.001f,
                $"render-Y dipped {worstDipBelowTrue * 1000:F1} mm below the true feet during a fall/landing (the fall-sink must be impossible by construction)");
        }

        [Fact]
        public void Swimming_BypassesGlide()
        {
            // A swimmer carries ClimbRate 0 (never a step climb), so its surface bob is drawn as-is (raw).
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
