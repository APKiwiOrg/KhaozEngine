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

// The signal-driven glide's EQUILIBRIUM: with ClimbRate stamped from the sim's own SMOOTHED APPLIED RISE (the EWMA in
// CharacterMovement step 4b) instead of commanded-forward * grade, the render-glide feed-forward/damp equilibrium sits
// ON the true feet at BOTH walk and run, so there is no persistent half-riser hover mid-climb and no one-frame snap when
// the signal disengages at the crest. These drive the REAL Bepu + CharacterMovement climb (30 Hz) through the real
// animator, both tick-aligned (render == sim) and inter-tick-lerped to 120 fps, and pin the absolute hover / crest that
// StairGlideRealisticStreamTests' bob/judder metrics could not see (a CONSTANT offset adds no bob and no judder). GPU-free.
//
// Pre-fix (commanded * grade signal) baselines, same fixtures - all RED against the bars below:
//   walk mid-climb sustained hover ~151 mm, run ~101 mm (the signal overstates the co-paced achieved rise);
//   walk crest single-frame render drop ~52 mm (the hover collapses in one frame when the signal cuts to 0).
public class StairGlideEquilibriumTests
{
    const float Riser = 0.30f, Tread = 0.40f;   // grade 0.75, the consumer TestStaircase scale
    const int Risers = 33;
    const float Walk = 3f, Run = 6f;            // Ruinborne consumer tuning

    static MoveTuning Tuning() => MoveTuning.Default with { WalkSpeed = Walk, RunSpeed = Run, CapsuleRadius = 0.4f };

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
        public Frame(Vector3 p, bool g, float vv, float cr) { Pos = p; Grounded = g; VVel = vv; ClimbRate = cr; }
        public Vector3 Pos { get; }
        public bool Grounded { get; }
        public float VVel { get; }
        public float ClimbRate { get; }
    }

    // Drive the real CharacterMovement sim (30 Hz) UP the staircase; capture per-tick position/grounded/vVel/ClimbRate.
    static List<Frame> DriveAscent(bool run)
    {
        MoveTuning tuning = Tuning();
        float speed = run ? Run : Walk;
        float dt = 1f / 30f, halfH = tuning.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world);
        world.Step(dt);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        var state = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run, cameraYaw: 0f, jump: false);
        int ticks = (int)(1.6f * (Tread * Risers + 3f) / (0.5f * speed * dt));
        var outp = new List<Frame>();
        for (int i = 0; i < ticks; i++)
        {
            state = CharacterMovement.Step(state, cmd, dt, Ground, tuning, normal, world);
            outp.Add(new Frame(state.Position, state.Grounded, state.VerticalVelocity, state.ClimbRate));
        }
        return outp;
    }

    // Inter-tick lerp the 30 Hz stream to `sub` render frames per tick (sub=4 -> 120 fps; sub=1 -> tick-aligned).
    // Grounded/vVel/ClimbRate are nearest-sampled from the target tick, as a real interpolating client presents them.
    static List<Frame> Present(List<Frame> ticks, int sub)
    {
        var outp = new List<Frame> { ticks[0] };
        for (int k = 1; k < ticks.Count; k++)
            for (int f = 1; f <= sub; f++)
                outp.Add(new Frame(Vector3.Lerp(ticks[k - 1].Pos, ticks[k].Pos, (float)f / sub), ticks[k].Grounded, ticks[k].VVel, ticks[k].ClimbRate));
        return outp;
    }

    static ReplicatedCharacterAnimators NewAnimators()
    {
        var skel = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
        AnimationClip Park(string n) => new(n, 1f, new List<JointTrack>
        { new JointTrack(0) { Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear) } });
        var clips = new Dictionary<LocomotionState, AnimationClip>
        {
            [LocomotionState.Idle] = Park("i"), [LocomotionState.Walk] = Park("w"), [LocomotionState.Run] = Park("r"),
            [LocomotionState.Jump] = Park("j"), [LocomotionState.Fall] = Park("f"),
        };
        return new ReplicatedCharacterAnimators(() => new AnimatedCharacter(skel, clips, LocomotionThresholds.Default), CharacterAnimatorTuning.Default);
    }

    static float[] RenderY(List<Frame> frames, float dtRender, float speed)
    {
        var a = NewAnimators();
        var y = new float[frames.Count];
        for (int i = 0; i < frames.Count; i++)
        {
            a.Update(new[] { new CharacterSample(1, frames[i].Pos, isLocal: true, grounded: frames[i].Grounded,
                verticalVelocity: frames[i].VVel, planarSpeed: speed, swimming: false, climbRate: frames[i].ClimbRate) }, dtRender);
            y[i] = a.Live[0].RenderPosition.Y;
        }
        return y;
    }

    static (int engage, int peak) EngageAndPeak(List<Frame> frames)
    {
        int peak = 0; float peakY = -1e9f;
        for (int i = 0; i < frames.Count; i++) if (frames[i].Pos.Y > peakY) { peakY = frames[i].Pos.Y; peak = i; }
        int engage = 0;
        for (int i = 0; i < frames.Count; i++) if (frames[i].ClimbRate != 0f) { engage = i; break; }
        return (engage, peak);
    }

    public static IEnumerable<object[]> WalkAndRun_TickAndSmooth()
    {
        foreach (bool run in new[] { false, true })
            foreach (int sub in new[] { 1, 4 })
                yield return new object[] { run, sub };
    }

    // (1) + (2): mid-climb equilibrium. The drawn feet TRACK the true feet up the stair - no sustained hover above them.
    // Measured over the steady middle of the climb (start warm-up and crest excluded): the MEAN offset is ~0 (the signal
    // converged to the achieved rise), and even a bob-removed moving average never shows a sustained float. Pre-fix the
    // mean was +151 mm (walk) / +101 mm (run): a half-riser feet-float for the whole ascent.
    [Theory]
    [MemberData(nameof(WalkAndRun_TickAndSmooth))]
    public void AscentMidClimb_RenderTracksTrueFeet_NoSustainedHover(bool run, int sub)
    {
        List<Frame> frames = Present(DriveAscent(run), sub);
        var (engage, peak) = EngageAndPeak(frames);
        float dtR = (1f / 30f) / sub, speed = run ? Run : Walk;
        float[] r = RenderY(frames, dtR, speed);

        int span = peak - engage;
        Assert.True(span > 20 * sub, "degenerate climb window");
        int lo = engage + span / 4, hi = peak - span / 12;      // the steady middle only

        double sum = 0; int n = 0;
        for (int i = lo; i < hi; i++) { sum += r[i] - frames[i].Pos.Y; n++; }
        float meanHover = (float)(sum / n);

        int win = 4 * sub; float maxMovAvg = 0f;                 // bob-removed sustained offset (~1-riser window)
        for (int i = lo; i < hi; i++)
        {
            float mv = 0f; int c = 0;
            for (int k = i - win; k <= i + win; k++) if (k >= lo && k < hi) { mv += r[k] - frames[k].Pos.Y; c++; }
            maxMovAvg = MathF.Max(maxMovAvg, MathF.Abs(mv / c));
        }

        string tag = $"{(run ? "run" : "walk")} sub={sub}";
        Assert.True(MathF.Abs(meanHover) < 0.03f,
            $"{tag}: sustained mid-climb hover {meanHover * 1000:F1} mm must be under 30 mm (pre-fix ~{(run ? 101 : 151)} mm)");
        Assert.True(maxMovAvg < 0.035f,
            $"{tag}: bob-removed sustained hover {maxMovAvg * 1000:F1} mm must be under 35 mm (no persistent stair float)");
    }

    // (3): crest disengage. When the climb signal cuts to 0 at the top of the climb, the drawn feet do NOT snap DOWN: the
    // mid-climb hover is ~0, so there is no accumulated offset to collapse, and the ascent-crest ease brings the render
    // onto the top tread from at/below true (rising gently), never a downward jump. Measured as the max single-frame
    // render-Y DECREASE across the DISENGAGE (the last climbing tick -> the flat top), the frame where the pre-fix hard
    // cut happened. Pre-fix that was tens of mm (the +150 mm hover dropping toward the true feet as the signal cut).
    [Theory]
    [MemberData(nameof(WalkAndRun_TickAndSmooth))]
    public void AscentCrest_Disengage_NoDownwardSnap(bool run, int sub)
    {
        List<Frame> frames = Present(DriveAscent(run), sub);
        float dtR = (1f / 30f) / sub, speed = run ? Run : Walk;
        float[] r = RenderY(frames, dtR, speed);

        // Disengage = the last frame (before the walk-off-back fall) that still carried a climb signal.
        var (engage, peak) = EngageAndPeak(frames);
        int disengage = engage;
        for (int i = engage; i <= peak && i < frames.Count; i++) if (frames[i].ClimbRate != 0f) disengage = i;

        // The snap, if any, lands as the signal cuts and for a few frames after. Only count DOWNWARD render moves that
        // the true feet did NOT make (fall-immune): a true riser drop is legitimate to follow, a render-only drop is the
        // hover collapse. Bounded to grounded frames so the eventual walk-off-the-back fall never contaminates it.
        float worstSnap = 0f;
        for (int i = Math.Max(1, disengage - sub); i <= disengage + 6 * sub && i < frames.Count; i++)
        {
            if (!frames[i].Grounded) break;
            float renderDrop = r[i - 1] - r[i];
            float trueDrop = frames[i - 1].Pos.Y - frames[i].Pos.Y;
            worstSnap = MathF.Max(worstSnap, renderDrop - MathF.Max(0f, trueDrop));
        }

        Assert.True(worstSnap < 0.02f,
            $"{(run ? "run" : "walk")} sub={sub}: crest single-frame render drop {worstSnap * 1000:F1} mm must be under 20 mm (pre-fix hard snap of the ~{(run ? 101 : 151)} mm hover)");
    }
}
