using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Regression for the RUN-up-stairs stutter (engine 10.68). The paced step-climb caps the per-tick VERTICAL rise at
// MaxStepClimbSpeed, which implicitly caps forward stair speed at MaxStepClimbSpeed/grade. A walk stays under that
// cap and climbs smoothly; a RUN exceeds it, and the excess used to become a freeze/jump stutter: the capsule's XZ
// raced ahead of the paced height, strobing forward advance between a frozen 0 and a full-tread catch-up, plowing
// sustained ~metre-deep penetration into the risers, lurching ~0.8 m on the first mount, and (angled) wagging the
// facing with big lateral jumps. The fix co-paces the horizontal along the stair tangent so a runner glides up at the
// honest grade-limited speed. These drive CharacterMovement.Step against a real Bepu-collided box staircase on an
// ANALYTIC flat approach (Ground=0, no physics floor - as a placed staircase sits on the consumer's analytic terrain),
// and pin the smooth glide. Tuning mirrors the consumer that surfaced it (walk 3 / run 6): run 6 > the 0.30/0.40 stair
// cap 4.67, so run is paced and walk is not.
public class StairRunTangentPacingTests
{
    // Consumer tuning (Ruinborne): walk 3, run 6, radius 0.4, half-height 0.9, StepHeight 0.4, climb 3.5.
    static MoveTuning BaseTuning(float radius) => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f, CapsuleRadius = radius };

    // Solid-box staircase climbing in -Z from Z=0, approached head-on from +Z (yaw 0 => forward -Z). Step i has its
    // front riser at Z=-tread*i and its tread top at riser*(i+1); each box runs from the floor up to its tread top and
    // back to a far wall, so the boxes nest into a solid stair. 33 risers mirrors a long consumer TestStaircase run.
    static void AddStairs(IPhysicsWorld world, float riser, float tread, int risers)
    {
        float backZ = -tread * risers - 2f;
        const float halfX = 20f;
        for (int i = 0; i < risers; i++)
        {
            float frontZ = -tread * i;
            float treadTop = riser * (i + 1);
            float centerZ = 0.5f * (frontZ + backZ);
            float depth = frontZ - backZ;
            world.AddStatic(new BoxShape(new Vector3(halfX, treadTop * 0.5f, depth * 0.5f)),
                Pose.At(new Vector3(0f, treadTop * 0.5f, centerZ)));
        }
    }

    sealed class Climb
    {
        public List<float> Fwd = new();       // per-tick advance along the climb axis (-Z)
        public List<float> Lat = new();       // per-tick lateral (X) delta
        public List<float> Planar = new();    // per-tick planar speed (m/s)
        public List<float> Pen = new();       // ComputePenetration MTV length this tick
        public List<bool> Grounded = new();
        public List<float> Y = new();
        public float MaxY;                    // highest capsule-centre Y reached (the top, before any walk-off)
        public float TopY;                    // capsule-centre Y standing on the top tread
    }

    // Drive a head-on (or yaw-offset) climb and capture per-tick metrics while grounded on the ramp. Ticks are sized
    // to reach the top with margin (a paced climb crawls at ~half the flat rate, and 1/60 doubles the tick count), then
    // a few flat ticks off the top; the climb window is filtered afterwards.
    static Climb Drive(float riser, float tread, int risers, float radius, float dt, bool run, float yawDeg = 0f)
    {
        MoveTuning tuning = BaseTuning(radius);
        float speed = run ? tuning.RunSpeed : tuning.WalkSpeed;
        int ticks = (int)(1.6f * (tread * risers + 3f) / (0.5f * speed * dt));
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world, riser, tread, risers);
        world.Step(dt);

        float halfH = tuning.CapsuleHalfHeight;
        float yaw = yawDeg * MathF.PI / 180f;
        var state = new MoveState { Position = new Vector3(0f, halfH, 1.0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run, cameraYaw: yaw, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        CapsuleShape capsule = CharacterMovement.CapsuleFor(tuning);

        var c = new Climb { TopY = riser * risers + halfH };
        var prev = state.Position;
        for (int i = 0; i < ticks; i++)
        {
            state = CharacterMovement.Step(state, cmd, dt, Ground, tuning, normal, world);
            Vector3 p = state.Position;
            c.Fwd.Add(-(p.Z - prev.Z));
            c.Lat.Add(p.X - prev.X);
            c.Planar.Add(MathF.Sqrt((p.X - prev.X) * (p.X - prev.X) + (p.Z - prev.Z) * (p.Z - prev.Z)) / dt);
            world.ComputePenetration(capsule, Pose.At(p), out Vector3 mtv);
            c.Pen.Add(mtv.Length());
            c.Grounded.Add(state.Grounded);
            c.Y.Add(p.Y);
            c.MaxY = MathF.Max(c.MaxY, p.Y);
            prev = p;
        }
        return c;
    }

    // Indices of ticks spent grounded on the STEADY ramp: past the first two risers of engagement (where the demo-radius
    // footprint on a floorless approach has a one-time mount hiccup, off the sustained-climb question) and below the top
    // landing. This is the window the run stutter lived in.
    static List<int> ClimbTicks(Climb c, float halfH, float riser, int risers)
    {
        float loY = halfH + 2f * riser + 0.05f;          // past the first two risers of engagement
        float hiY = riser * risers + halfH - 0.05f;      // below the top landing
        var idx = new List<int>();
        for (int i = 0; i < c.Y.Count; i++)
            if (c.Grounded[i] && c.Y[i] > loY && c.Y[i] < hiY) idx.Add(i);
        return idx;
    }

    // ---- The parameter matrix: dt {1/30,1/60} x radius {0.3,0.4} x run {false,true} on the 0.30/0.40 stair. ----
    public static IEnumerable<object[]> Matrix()
    {
        foreach (float dt in new[] { 1f / 30f, 1f / 60f })
            foreach (float r in new[] { 0.3f, 0.4f })
                foreach (bool run in new[] { false, true })
                    yield return new object[] { dt, r, run };
    }

    const float Riser = 0.30f, Tread = 0.40f;   // grade 0.75; cap 3.5/0.75 = 4.67 m/s (walk 3 under, run 6 over)
    const int Risers = 33;

    // (1) No 0-then-full-tread strobe: the pre-fix run trace alternated a frozen ~0 tick with a full-tread (~0.4 m)
    //     catch-up. Bound the count of such (near-zero -> catch-up) pairs to ~zero. Paced pauses are fine as long as
    //     they are not followed by a tread-sized lurch.
    [Theory]
    [MemberData(nameof(Matrix))]
    public void ForwardAdvance_NoFreezeThenCatchUpStrobe(float dt, float radius, bool run)
    {
        Climb c = Drive(Riser, Tread, Risers, radius, dt, run);
        var climb = ClimbTicks(c, 0.9f, Riser, Risers);
        Assert.True(climb.Count > 20, $"too few climb ticks ({climb.Count}) to characterize");
        float runStep = 6f * dt;
        int strobePairs = 0;
        for (int k = 0; k + 1 < climb.Count; k++)
        {
            int i = climb[k], j = climb[k + 1];
            if (j != i + 1) continue;
            bool frozen = c.Fwd[i] < 0.15f * runStep;
            bool catchUp = c.Fwd[j] > 0.9f * Tread;         // a full-tread jump = the pre-fix catch-up
            if (frozen && catchUp) strobePairs++;
        }
        Assert.True(strobePairs == 0,
            $"dt={dt:F4} r={radius} run={run}: {strobePairs} freeze-then-full-tread strobe pair(s) - the run stutter.");
    }

    // (2) Sustained penetration bound: the pre-fix run plowed the capsule ~1.2 m into the risers; co-pacing keeps it on
    //     the surface. The residual is the inherent monotone-forward mounting press (the capsule presses the riser it is
    //     mounting), which stays well under a tread. Assert every climbing tick's MTV is small.
    [Theory]
    [MemberData(nameof(Matrix))]
    public void Penetration_StaysSmall_EveryClimbingTick(float dt, float radius, bool run)
    {
        Climb c = Drive(Riser, Tread, Risers, radius, dt, run);
        var climb = ClimbTicks(c, 0.9f, Riser, Risers);
        float worst = 0f;
        foreach (int i in climb) worst = MathF.Max(worst, c.Pen[i]);
        Assert.True(worst < 0.15f,
            $"dt={dt:F4} r={radius} run={run}: worst climbing-tick penetration {worst:F3} m (the run raced its XZ into the risers).");
    }

    // (3) Clip-stability driver: the pre-fix run strobed the predicted planar speed ~0 vs ~run-speed, flipping the
    //     locomotion clip Idle<->moving. After co-pacing the planar speed stays in a moving band; the paced climb still
    //     pauses ~a tick per riser, so this feeds the per-tick planar speed through the REAL AnimatedCharacter (with its
    //     shipped state debounce) and pins that the committed clip state never resolves to Idle mid-climb - no Idle
    //     strobe. (This is the evidence the sim fix needs no extra animator smoothing: the existing debounce absorbs the
    //     brief per-riser pauses.)
    [Theory]
    [MemberData(nameof(Matrix))]
    public void ClipState_NeverIdle_DuringRunClimb(float dt, float radius, bool run)
    {
        Climb c = Drive(Riser, Tread, Risers, radius, dt, run);
        var climb = ClimbTicks(c, 0.9f, Riser, Risers);

        // A one-bone animator with the shipped default state debounce and low idle / high run thresholds (0.1 / 9).
        var skeleton = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
        AnimationClip Park(string n) => new(n, 1f, new List<JointTrack>
        {
            new JointTrack(0) { Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear) },
        });
        var clips = new Dictionary<LocomotionState, AnimationClip>
        {
            [LocomotionState.Idle] = Park("idle"), [LocomotionState.Walk] = Park("walk"),
            [LocomotionState.Run] = Park("run"), [LocomotionState.Jump] = Park("jump"), [LocomotionState.Fall] = Park("fall"),
        };
        var anim = new AnimatedCharacter(skeleton, clips, new LocomotionThresholds(0.1f, 9f));

        var climbSet = new HashSet<int>(climb);
        int idleTicks = 0;
        for (int i = 0; i < c.Planar.Count; i++)
        {
            anim.Update(c.Planar[i], c.Grounded[i], verticalVelocity: 0f, swimming: false, dt);
            if (climbSet.Contains(i) && anim.State == LocomotionState.Idle) idleTicks++;
        }
        Assert.True(idleTicks == 0,
            $"dt={dt:F4} r={radius} run={run}: locomotion clip resolved Idle on {idleTicks} climbing tick(s) - the Idle strobe.");
    }

    // (4) Angled approach: the pre-fix run wagged the facing with ~0.48 m/tick lateral jumps (the derived heading swings
    //     left/right). Co-pacing holds the lateral to the honest walk-angle rate. Drive a 20 deg off-axis run and bound
    //     the per-tick |lateral|.
    [Theory]
    [InlineData(1f / 30f, 0.4f)]
    [InlineData(1f / 60f, 0.4f)]
    [InlineData(1f / 30f, 0.3f)]
    public void AngledRun_LateralPerTick_IsBounded(float dt, float radius)
    {
        const float deg = 20f;
        Climb c = Drive(Riser, Tread, Risers, radius, dt, run: true, yawDeg: deg);
        var climb = ClimbTicks(c, 0.9f, Riser, Risers);
        // The honest lateral a run at this angle covers in one tick (run 6). The pre-fix leak jumped several times this.
        float honestLat = MathF.Sin(deg * MathF.PI / 180f) * 6f * dt;
        float worst = 0f;
        foreach (int i in climb) worst = MathF.Max(worst, MathF.Abs(c.Lat[i]));
        Assert.True(worst <= honestLat + 0.02f,
            $"dt={dt:F4} r={radius}: worst on-ramp lateral {worst:F4} m/tick over the honest {honestLat:F4} + margin - the facing wag.");
    }

    // (5) First mount + no lurch: the pre-fix run lurched ~0.8 m forward on the first mount from flat (the single-riser
    //     seat-commit firing on a stair run). Co-pacing bounds every single-tick advance to about the run step. Assert
    //     the max single-tick forward advance over the whole climb is bounded (no lurch) AND the climb reaches the top.
    [Theory]
    [MemberData(nameof(Matrix))]
    public void FirstMount_NoLurch_AndReachesTop(float dt, float radius, bool run)
    {
        Climb c = Drive(Riser, Tread, Risers, radius, dt, run);
        // Max single-tick forward advance over the WHOLE grounded climb (the first mount included). The pre-fix run
        // first mount lurched ~0.8 m (2 treads) in one tick; the fix bounds any single tick well under a tread. The
        // legitimate single-riser mount seat (~0.16 m) and the run tread-crossing (~one run step) both pass.
        float maxAdvance = 0f;
        for (int i = 0; i < c.Fwd.Count; i++)
            if (c.Grounded[i] && c.Y[i] > 0.9f + 0.05f && c.Y[i] < c.TopY - 0.05f)
                maxAdvance = MathF.Max(maxAdvance, c.Fwd[i]);
        Assert.True(maxAdvance < Tread,
            $"dt={dt:F4} r={radius} run={run}: a mount tick advanced {maxAdvance:F3} m (>= a {Tread:F2} m tread) - the first-mount lurch.");
        Assert.True(c.MaxY > c.TopY - 0.25f,
            $"dt={dt:F4} r={radius} run={run}: climb did not reach the top: peak Y {c.MaxY:F3}, expected ~{c.TopY:F3}.");
    }

    // Steeper grade (0.38 riser / 0.44 tread = grade 0.86; cap 3.5/0.86 = 4.1 m/s, so run 6 is well over - the tread
    // stays wider than the 0.4 footprint so the step-up can seat, unlike a tread shallower than the foot). The co-pace
    // holds there too: reaches the top, no freeze-then-catch-up strobe. Penetration runs a touch higher than the
    // grade-0.75 matrix (a stair steeper than MaxClimbGrade advances a bounded sub-tread lead, by design), so the bound
    // is looser here; the grade-0.75 matrix pins the tight bound.
    [Theory]
    [InlineData(1f / 30f)]
    [InlineData(1f / 60f)]
    public void SteeperGrade_RunClimb_IsSmooth_AndReachesTop(float dt)
    {
        const float riser = 0.38f, tread = 0.44f;
        int risers = 33;
        Climb c = Drive(riser, tread, risers, 0.4f, dt, run: true);
        var climb = ClimbTicks(c, 0.9f, riser, risers);
        Assert.True(climb.Count > 20, $"too few climb ticks ({climb.Count})");
        Assert.True(c.MaxY > riser * risers + 0.9f - 0.3f,
            $"steeper-grade run climb did not reach the top: peak Y {c.MaxY:F3}.");
        float runStep = 6f * dt;
        int strobePairs = 0;
        for (int k = 0; k + 1 < climb.Count; k++)
        {
            int i = climb[k], j = climb[k + 1];
            if (j == i + 1 && c.Fwd[i] < 0.15f * runStep && c.Fwd[j] > 0.9f * tread) strobePairs++;
        }
        Assert.True(strobePairs == 0, $"dt={dt:F4}: {strobePairs} freeze-then-catch-up strobe pair(s) on the steep stair.");
        float worstPen = 0f;
        foreach (int i in climb) worstPen = MathF.Max(worstPen, c.Pen[i]);
        Assert.True(worstPen < 0.2f, $"dt={dt:F4}: worst steep-stair penetration {worstPen:F3} m.");
    }
}
