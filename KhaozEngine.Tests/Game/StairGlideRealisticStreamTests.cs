using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game;

// The slope-fed render-height smoother (ReplicatedCharacterAnimators) measured over REALISTIC streams: a real Bepu +
// CharacterMovement climb/descent of a TestStaircase-scale box staircase at 30 Hz, then presented to the animator two
// ways a client actually presents it - inter-tick-lerped to 120 fps (the client renders faster than the sim ticks) and
// tick-aligned (render == sim). This is stronger than the synthetic StairGlideSmootherTests profile: the real paced
// step-up co-paces the horizontal along the stair tangent, so per-tick horizontal is ANTICORRELATED with the rise on
// ASCENT (near-zero on rise ticks, a full tread on flat-tread ticks) while DESCENT advances steadily. The old
// horizontalDelta*grade feed-forward multiplied that uneven horizontal by the grade and read WORSE than the raw
// per-riser sawtooth on a run-up (a judder regression); the time-paced glide (advance by the windowed mean dY/dt) does
// not couple to the per-tick horizontal and beats raw on BOTH bob and judder across all four cases. These pin that.
// All GPU-free (a one-bone parked animator; the smoother is the unit under test).
public class StairGlideRealisticStreamTests
{
    const float Riser = 0.30f, Tread = 0.40f;   // grade 0.75, the consumer TestStaircase scale
    const int Risers = 33;
    const float Walk = 3f, Run = 6f;            // Ruinborne consumer tuning

    static MoveTuning Tuning() => MoveTuning.Default with { WalkSpeed = Walk, RunSpeed = Run, CapsuleRadius = 0.4f };

    // Solid-box staircase climbing in -Z from Z=0 (mirrors StairRunTangentPacingTests.AddStairs / the consumer TestStaircase).
    static void AddStairs(IPhysicsWorld world)
    {
        float backZ = -Tread * Risers - 2f;
        const float halfX = 20f;
        for (int i = 0; i < Risers; i++)
        {
            float treadTop = Riser * (i + 1);
            float centerZ = 0.5f * (-Tread * i + backZ);
            float depth = -Tread * i - backZ;
            world.AddStatic(new BoxShape(new Vector3(halfX, treadTop * 0.5f, depth * 0.5f)),
                Pose.At(new Vector3(0f, treadTop * 0.5f, centerZ)));
        }
    }

    readonly struct Frame
    {
        public Frame(Vector3 p, bool g, float vv) { Pos = p; Grounded = g; VVel = vv; }
        public Vector3 Pos { get; }
        public bool Grounded { get; }
        public float VVel { get; }
    }

    // Drive the real CharacterMovement sim (30 Hz) up or down the staircase; capture per-tick position/grounded/vVel.
    static List<Frame> DriveSim(bool run, bool descend)
    {
        MoveTuning tuning = Tuning();
        float speed = run ? Run : Walk;
        float dt = 1f / 30f, halfH = tuning.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world);
        world.Step(dt);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;

        MoveState state;
        MoveCommand cmd;
        int ticks;
        if (!descend)
        {
            state = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
            cmd = new MoveCommand(new Vector2(0f, 1f), run, cameraYaw: 0f, jump: false);     // forward -Z, into and up
            ticks = (int)(1.6f * (Tread * Risers + 3f) / (0.5f * speed * dt));
        }
        else
        {
            state = new MoveState { Position = new Vector3(0f, Riser * Risers + halfH, -Tread * (Risers - 1) - 0.6f), Grounded = true };
            cmd = new MoveCommand(new Vector2(0f, -1f), run, cameraYaw: 0f, jump: false);    // forward +Z, down
            ticks = (int)(1.6f * (Tread * Risers + 3f) / (0.5f * speed * dt)) + 40;
        }

        var outp = new List<Frame>();
        for (int i = 0; i < ticks; i++)
        {
            state = CharacterMovement.Step(state, cmd, dt, Ground, tuning, normal, world);
            outp.Add(new Frame(state.Position, state.Grounded, state.VerticalVelocity));
        }
        return outp;
    }

    // Inter-tick lerp the 30 Hz tick stream to `sub` render frames per tick (sub=4 -> 120 fps; sub=1 -> tick-aligned).
    // Grounded/vVel are taken from the target tick (the state the client last received), like a real interpolating client.
    static List<Frame> Present(List<Frame> ticks, int sub)
    {
        var outp = new List<Frame> { ticks[0] };
        for (int k = 1; k < ticks.Count; k++)
            for (int f = 1; f <= sub; f++)
                outp.Add(new Frame(Vector3.Lerp(ticks[k - 1].Pos, ticks[k].Pos, (float)f / sub), ticks[k].Grounded, ticks[k].VVel));
        return outp;
    }

    static ReplicatedCharacterAnimators NewAnimators(bool smootherOn)
    {
        var skel = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
        AnimationClip Park(string n) => new(n, 1f, new List<JointTrack>
        { new JointTrack(0) { Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear) } });
        var clips = new Dictionary<LocomotionState, AnimationClip>
        {
            [LocomotionState.Idle] = Park("i"), [LocomotionState.Walk] = Park("w"), [LocomotionState.Run] = Park("r"),
            [LocomotionState.Jump] = Park("j"), [LocomotionState.Fall] = Park("f"),
        };
        var t = CharacterAnimatorTuning.Default;
        if (!smootherOn) t.SlopeGlideRate = 0f;   // raw = escape hatch = the pre-feature bridge (render-Y == true feet-Y)
        return new ReplicatedCharacterAnimators(() => new AnimatedCharacter(skel, clips, LocomotionThresholds.Default), t);
    }

    // Present the frames to the real animator; return the baked render-Y (CharacterPose.RenderPosition.Y) per frame.
    static float[] RenderY(List<Frame> frames, float dtRender, bool smootherOn, float speed)
    {
        var a = NewAnimators(smootherOn);
        var y = new float[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            a.Update(new[] { new CharacterSample(1, frames[i].Pos, isLocal: true,
                grounded: frames[i].Grounded, verticalVelocity: frames[i].VVel, planarSpeed: speed) }, dtRender);
            y[i] = a.Live[0].RenderPosition.Y;
        }
        return y;
    }

    // Steady-ramp frame window: in-band frames (Y in [base+1.5r, top-1.5r]) on the FIRST monotonic pass only (up to the
    // Y peak for ascent / trough for descent), so a walk-off-the-back free fall past the run never contaminates the window.
    static (int lo, int hi) Window(List<Frame> frames, bool descend)
    {
        float halfH = 0.9f, yLo = halfH + 1.5f * Riser, yHi = Riser * Risers + halfH - 1.5f * Riser;
        int turn = 0; float best = descend ? 1e9f : -1e9f;
        for (int i = 0; i < frames.Count; i++)
        {
            float yy = frames[i].Pos.Y;
            if (descend ? yy < best : yy > best) { best = yy; turn = i; }
        }
        int first = -1, last = -1;
        for (int i = 0; i <= turn; i++)
            if (frames[i].Pos.Y > yLo && frames[i].Pos.Y < yHi) { if (first < 0) first = i; last = i; }
        Assert.True(first >= 0 && last > first + 10, "degenerate climb window - the sim did not traverse the ramp");
        return (first, last + 1);
    }

    // TIME-domain bob: peak-to-peak of render-Y minus a least-squares line fit in FRAME INDEX (uniform time). Frames are
    // uniform in time at a fixed render rate, so this is the honest "how far the drawn height deviates from a clean ramp
    // over time" - the perceptual bob. (The synthetic StairGlideSmootherTests fits vs the X advance instead, which only
    // matches the time domain when the horizontal is uniform; a co-paced run has uneven horizontal, so time is the honest axis.)
    static float TimeBob(float[] y, int lo, int hi)
    {
        int n = hi - lo;
        double mi = 0, mv = 0;
        for (int i = lo; i < hi; i++) { mi += i; mv += y[i]; }
        mi /= n; mv /= n;
        double sii = 0, siv = 0;
        for (int i = lo; i < hi; i++) { double di = i - mi; sii += di * di; siv += di * (y[i] - mv); }
        double slope = siv / sii, icpt = mv - slope * mi;
        double max = double.NegativeInfinity, min = double.PositiveInfinity;
        for (int i = lo; i < hi; i++) { double r = y[i] - (slope * i + icpt); if (r > max) max = r; if (r < min) min = r; }
        return (float)(max - min);
    }

    // JUDDER = P90(|per-frame dY|) / mean(|per-frame dY|): how much bigger the worst-decile vertical step is than the
    // typical one. A perfectly uniform glide is ~1; a stream that jumps then holds (the per-riser sawtooth, or a
    // horizontal-coupled feed-forward on a co-paced climb) has a big P90 and small typical step, so the ratio climbs.
    // Robust to zero-hold frames (unlike P90/P10, whose P10 collapses to 0 on any paused-tread stream). WORST = max |dY|.
    static (float judder, float worst) Judder(float[] y, int lo, int hi)
    {
        var d = new List<float>();
        float worst = 0f; double sum = 0;
        for (int i = lo + 1; i < hi; i++) { float a = MathF.Abs(y[i] - y[i - 1]); d.Add(a); worst = MathF.Max(worst, a); sum += a; }
        d.Sort();
        float mean = (float)(sum / d.Count);
        float p90 = d[Math.Clamp((int)MathF.Round(0.90f * (d.Count - 1)), 0, d.Count - 1)];
        return (mean > 1e-7f ? p90 / mean : 1f, worst);
    }

    // (sub, name): 4 = 120 fps inter-tick lerp, 1 = tick-aligned.
    public static IEnumerable<object[]> Cases()
    {
        foreach (int sub in new[] { 4, 1 })
            foreach (bool run in new[] { false, true })
                foreach (bool descend in new[] { false, true })
                    yield return new object[] { sub, run, descend };
    }

    // The smoother must BEAT the raw feet-Y on BOTH bob and judder for every case and both stream shapes - and in
    // particular it must not REGRESS judder/worst on a run-up, which the old horizontalDelta*grade feed-forward did
    // (this test goes RED on that case for the old code: current run-up judder ~1.4-1.5 vs raw ~1.2, worst ~37-142 mm vs
    // raw ~29-117 mm). The time-paced glide brings run-up judder BELOW raw. Descent stays as good as before (the bar).
    [Theory]
    [MemberData(nameof(Cases))]
    public void SmoothedBeatsRaw_OnBobAndJudder_EveryCase(int sub, bool run, bool descend)
    {
        List<Frame> frames = Present(DriveSim(run, descend), sub);
        var (lo, hi) = Window(frames, descend);
        float dtRender = (1f / 30f) / sub, speed = run ? Run : Walk;

        float[] raw = RenderY(frames, dtRender, smootherOn: false, speed);
        float[] sm = RenderY(frames, dtRender, smootherOn: true, speed);

        float rawBob = TimeBob(raw, lo, hi), smBob = TimeBob(sm, lo, hi);
        var (rawJud, rawWorst) = Judder(raw, lo, hi);
        var (smJud, smWorst) = Judder(sm, lo, hi);

        string tag = $"sub={sub} {(descend ? "down" : "up")} {(run ? "run" : "walk")}";

        // Sanity: raw (smoother off) IS the deliberate per-riser sawtooth - a real, juddery signal to beat.
        Assert.True(rawJud > 1.15f, $"{tag}: raw judder {rawJud:F2} should be a real sawtooth (>1.15) to make this a meaningful bar");

        // 1) JUDDER: the smoother must not increase the jerkiness vs raw. RED for the old code on run-up.
        Assert.True(smJud <= rawJud, $"{tag}: smoothed judder {smJud:F2} must be <= raw {rawJud:F2} (the old feed-forward made a run-up WORSE)");

        // 2) BOB: the smoother must reduce the time-domain bob vs raw.
        Assert.True(smBob < rawBob, $"{tag}: smoothed timeBob {smBob * 1000:F0} mm must be under raw {rawBob * 1000:F0} mm");

        // 3) WORST single-frame vertical pop must not exceed raw (5% float margin). RED for the old code on run-up.
        Assert.True(smWorst <= rawWorst * 1.05f + 1e-4f,
            $"{tag}: smoothed worst-frame pop {smWorst * 1000:F0} mm must be <= raw {rawWorst * 1000:F0} mm (the old feed-forward popped bigger on a run-up)");
    }

    // Absolute run-up quality bars at 120 fps (the case prod called "very bad"): the drawn height glides smoothly - a
    // low judder ratio and a small worst-frame pop - independent of the raw baseline.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunUp_And_WalkUp_GlideSmoothly_At120fps(bool run)
    {
        List<Frame> frames = Present(DriveSim(run, descend: false), sub: 4);
        var (lo, hi) = Window(frames, descend: false);
        float dtRender = (1f / 30f) / 4f, speed = run ? Run : Walk;
        float[] sm = RenderY(frames, dtRender, smootherOn: true, speed);
        var (jud, worst) = Judder(sm, lo, hi);
        Assert.True(jud < 1.4f, $"{(run ? "run" : "walk")}-up: judder {jud:F2} should read as a smooth glide (< 1.4)");
        Assert.True(worst < 0.030f, $"{(run ? "run" : "walk")}-up: worst single-frame pop {worst * 1000:F0} mm should be under 30 mm at 120 fps");
    }
}
