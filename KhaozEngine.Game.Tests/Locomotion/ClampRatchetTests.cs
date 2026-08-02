using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// THE GROUND CLAMP MAY NOT LIFT A CAPSULE THAT HAS NO FOOTING (#468).
//
// Third round of the steep-terrain chain (#440 jump ratchet, #442 slide model). A Ruinborne playtest climbed an
// authored sea cliff, and the climber-bot repro that followed found the mechanism has nothing to do with footing,
// with jumps, or with diagonal input being special:
//
//   AdvanceWallSlide admits ANY horizontal move whose destination column stands within StepHeight of the feet - on
//   a 74 degree face as readily as on a doorstep - and step 4's ground clamp then raises the capsule onto that
//   column unconditionally. Two ticks make a limit cycle: the slide tick commits its drop, the next tick is out of
//   slide contact (the smoothed normal and the real height field disagree, so the tangent-plane drop overshoots
//   the real surface and throws the capsule clear of it), its full up-slope command is admitted because the rise
//   is under StepHeight, and the clamp seats it 0.29 to 0.39 m higher. Net climb 2.3 to 2.7 m/s while
//   VerticalVelocity reports falling at 5 to 7 m/s. Zero jumps and zero footing grants.
//
// The invariant that closes it: A TICK WITHOUT FOOTING MUST NEVER END HIGHER THAN ITS OWN RESOLVED VERTICAL MOTION
// ALLOWS. Altitude on steep ground comes only from real velocity, never from the ground clamp.
//
// THE FIXTURE is the repro's measured cliff patch, distilled and needing no Ruinborne data: a piecewise-planar
// face with creases every 4 m whose four planes run 68.6 to 77.1 degrees, under a normal delegate SMOOTHED over a
// stencil wider than the crease spacing, so the normal reads a smooth ~74 degrees while the height field under any
// one tick's travel is one of the four planes. That disagreement is the whole engine of the cycle: it is what
// throws the capsule off the surface on the slide tick and hands the next tick to the free-flight path with its
// full command authority. A single planar face cannot show it (there the slide keeps the capsule glued to the
// surface by construction), which is exactly why 17.28.0's analytic fixtures all passed while the real cliff did
// not.
//
// WHERE THE HOLE OPENS, derived and then measured on this fixture. A cycle climbs when the command tick's rise
// beats the slide tick's drop, which works out to walk*a > Gravity*dt*Gs (a is how much of the input points up the
// fall line, Gs the smoothed gradient), while the admission needs walk*a*dt*Gl <= StepHeight (Gl the LOCAL plane's
// gradient). Both hold together only while Gravity*dt^2*Gs*Gl < StepHeight - so the window opens QUADRATICALLY as
// the tick rate rises, which is the repro's "scales worse with tick rate" stated exactly. At 30 Hz on this face
// the walk window is a slit around 3 m/s (measured: 17.15 m of climb at walk 3.0, nothing at 2.5 or 3.5). With a
// diagonal, which shrinks the per-tick ask by cos of the offset, it is wide open at every rate and every speed
// (measured at the engine default tuning: 76.7 m at 15 Hz, 404.6 at 30, 1059.0 at 60, 1438.6 at 120).
public class ClampRatchetTests
{
    const float Dt = 1f / 30f;

    // The engine defaults (walk 6, run 12, 45 degree gate, StepHeight 0.40) drive every case here but the
    // straight-up walk, which needs the slit above.
    static MoveTuning Tuning => MoveTuning.Default;

    // THE WALK-UP OPERATING POINT, and the reason it is not the default tuning. Straight up the fall line the
    // per-tick ask is the full gradient, so the admission caps the speed that can climb at all: at 30 Hz on this
    // face that is a slit around 3 m/s, which is also where the repro's own tuning sat (its walk asked 0.33 m of
    // rise per tick against StepHeight 0.40, and its run asked 0.65 and was refused - the playtest's "walking up
    // works, running up does not"). The default 6 m/s asks 0.82 straight up and is refused at 30 Hz, which is why
    // the diagonal cases below are the ones that show the hole at the shipped speeds.
    static MoveTuning WalkUpTuning => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f };

    // ---- The cliff patch ----

    // The face is piecewise-planar on a 4 m crease grid, alternating gradient per 4 m segment on each axis (so the
    // period is 8 m). The x gradients are the repro's measured 0.5 and 1.5 m/m, the z gradients its -4.1 and -2.5.
    // The four combinations are planes of 68.6, 71.1, 76.4 and 77.1 degrees.
    const float Crease = 4f;
    const float GradXLow = 0.5f, GradXHigh = 1.5f;
    const float GradZLow = -4.1f, GradZHigh = -2.5f;

    // The stencil the NORMAL is smoothed over: WIDER than the crease spacing, so every sample averages across at
    // least one crease and the reported normal never matches the plane the capsule is standing on (it reads 73.5 to
    // 74.4 degrees everywhere). This is the ordinary shape of a real terrain sampler - a smoothed or
    // lower-resolution normal field over a heightmap - not a contrived mismatch.
    const float NormalStencil = 5f;

    // The piecewise-linear integral of a two-segment gradient: one axis's contribution to the height.
    static float Ramp(float t, float a, float b)
    {
        float period = 2f * Crease;
        float k = MathF.Floor(t / period);
        float r = t - k * period;
        return k * (a + b) * Crease + (r < Crease ? a * r : a * Crease + b * (r - Crease));
    }

    static float Face(float x, float z) => Ramp(x, GradXLow, GradXHigh) + Ramp(z, GradZLow, GradZHigh);

    // The SMOOTHED normal: a central difference over a stencil wider than the crease spacing.
    static Vector3 FaceNormal(float x, float z)
    {
        float dhdx = (Face(x + NormalStencil, z) - Face(x - NormalStencil, z)) / (2f * NormalStencil);
        float dhdz = (Face(x, z + NormalStencil) - Face(x, z - NormalStencil)) / (2f * NormalStencil);
        return Vector3.Normalize(new Vector3(-dhdx, 1f, -dhdz));
    }

    static Func<float, float, float> Ground => Face;
    static Func<float, float, Vector3> Normals => FaceNormal;

    // The UPHILL horizontal direction at a point, read off the same smoothed normal the engine reads: the normal's
    // XZ projection points DOWN the fall line, so its negation points up it.
    static Vector2 Uphill(float x, float z)
    {
        Vector3 n = FaceNormal(x, z);
        return Vector2.Normalize(new Vector2(-n.X, -n.Z));
    }

    // A camera-relative command heading in a WORLD XZ direction. At yaw 0 the right axis is +X and forward is -Z,
    // so (Move.X, Move.Y) = (dir.X, -dir.Z).
    static MoveCommand Toward(Vector2 dir, bool run = false, bool jump = false)
        => new(new Vector2(dir.X, -dir.Y), run, cameraYaw: 0f, jump: jump);

    static Vector2 Rotate(Vector2 v, float degrees)
    {
        float a = degrees * MathF.PI / 180f, s = MathF.Sin(a), c = MathF.Cos(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    const float StartX = 2f, StartZ = 2f;

    static MoveState Seed(in MoveTuning t) => new()
    {
        Position = new Vector3(StartX, Face(StartX, StartZ) + t.CapsuleHalfHeight, StartZ),
        Grounded = false,
        TimeSinceGrounded = 1f,   // the coyote window is long expired, so no jump can fire off the seed
    };

    // The whole run in one shape: hold a heading for a window of TIME (so the tick-rate rows all cover the same
    // seconds of contact) and report what the face gave back.
    static (float peak, float final, int grants, int jumps) Ride(in MoveTuning t, Vector2 heading, bool run,
        bool jump, float dt, float seconds)
    {
        MoveState s = Seed(t);
        float startFeet = s.Position.Y - t.CapsuleHalfHeight;
        float peak = 0f;
        int grants = 0, jumps = 0;
        int ticks = (int)(seconds / dt);
        for (int i = 0; i < ticks; i++)
        {
            s = CharacterMovement.Step(s, Toward(heading, run, jump), dt, Ground, t, Normals);
            float feet = s.Position.Y - t.CapsuleHalfHeight;
            peak = MathF.Max(peak, feet - startFeet);
            if (s.Grounded || s.SupportGranted) grants++;
            if (s.VerticalVelocity == t.JumpSpeed) jumps++;
            // NEVER INSIDE TERRAIN: the ground clamp still forbids penetration, which is the property the fix must
            // not trade away. Refusing the raise outright would buy the invariant with a tunnel.
            Assert.True(feet >= Face(s.Position.X, s.Position.Z) - 1e-3f,
                $"tick {i} left the capsule under terrain, feet={feet:F5}, " +
                $"ground={Face(s.Position.X, s.Position.Z):F5}");
        }
        return (peak, s.Position.Y - t.CapsuleHalfHeight - startFeet, grants, jumps);
    }

    // WHAT A SLIDE MAY STILL REACH, and the ceiling every case here measures against. The character starts at REST,
    // so the only energy on the face is what the face and the INPUT gave it - and a held run keeps re-presenting run
    // speed at every airborne tick, which the signed fall line converts to altitude whenever a crease turns the slope
    // under it. That is 17.28.0's intended behaviour (a face is a ramp that pays out and takes back) and it is worth
    // RunSpeed^2 / 2g = 2.88 m at the shipped tuning. Measured across the eight cases in this file after the fix:
    // seven of them gain exactly 0.000 m and the eighth (120 Hz, run plus held jump) peaks once at 0.620 m in its
    // first half second and never returns there over the remaining 9500 ticks. One metre carries that with margin
    // and is still 76x under the SMALLEST climb the ratchet bought (76.7 m at 15 Hz) and 1400x under the largest.
    const float SlideTransient = 1f;

    // ---- (a) walking straight up the fall line ----

    [Fact]
    public void Walking_straight_up_the_fall_line_gains_no_altitude()
    {
        // THE KILLER CASE, and the one the playtest reported: no jump, no diagonal, and no footing anywhere on the
        // face. The repro measured 19.9 m of climb up the real cliff from exactly this input, on the clamp alone.
        // Measured here before the fix: 17.15 m over 80 seconds, with zero footing grants and zero jumps - the
        // ratchet needs neither, which is what makes it invisible to every anti-cheat signal the sim exported.
        var t = WalkUpTuning;
        (float peak, float final, int grants, int jumps) = Ride(t, Uphill(StartX, StartZ), run: false, jump: false,
            Dt, seconds: 80f);

        string measured = $"peak {peak:F3} m above the start, final {final:F3} m, grants {grants}, jumps {jumps}";
        Assert.Equal(0, jumps);
        Assert.True(peak <= SlideTransient, $"the walk climbed the face: {measured}");
        Assert.True(final < 0f, $"the walk did not slide down the face: {measured}");
    }

    // ---- (b) run plus held jump, diagonally ----

    [Fact]
    public void A_running_held_jump_across_the_fall_line_gains_no_altitude()
    {
        // The playtest's own input pattern: run diagonally at the face with the jump button held. A diagonal is not
        // a different mechanism, it is the SAME one reached at the shipped speeds - holding 50 degrees off the fall
        // line shrinks the per-tick ask by cos(50) and slips a full-speed run under the StepHeight admission that
        // refuses it head-on. Measured before the fix at the engine default tuning: 404.6 m in 80 seconds.
        var t = Tuning;
        (float peak, float final, int grants, int jumps) = Ride(t, Rotate(Uphill(StartX, StartZ), 50f), run: true,
            jump: true, Dt, seconds: 80f);

        string measured = $"peak {peak:F3} m above the start, final {final:F3} m, grants {grants}, jumps {jumps}";
        Assert.True(peak <= SlideTransient, $"the running jump climbed the face: {measured}");
        Assert.True(final < 0f, $"the run did not slide down the face: {measured}");
    }

    // ---- (c) the tick rate must not be a climbing aid ----

    [Theory]
    [InlineData(1f / 15f)]
    [InlineData(1f / 30f)]
    [InlineData(1f / 60f)]
    [InlineData(1f / 120f)]
    public void No_tick_rate_lets_the_face_be_climbed(float dt)
    {
        // THE SCALE TEST, and the one that says the old admission was a tick-rate exploit rather than a tolerance
        // question. The ratchet's ceiling is StepHeight per TICK against gravity's g*dt takeback, so it pays more
        // the faster the server runs: measured on this fixture before the fix, over the same 80 seconds of contact,
        // 76.7 m at 15 Hz, 404.6 at 30, 1059.0 at 60 and 1438.6 at 120. The fix is scale-free instead - the
        // allowance IS the tick's own resolved vertical motion, which shrinks with dt exactly as the ask does.
        var t = Tuning;
        (float peak, float final, _, _) = Ride(t, Rotate(Uphill(StartX, StartZ), 50f), run: true, jump: true, dt,
            seconds: 80f);

        Assert.True(peak <= SlideTransient,
            $"a {1f / dt:F0} Hz tick climbed the face: peak {peak:F3} m above the start, final {final:F3} m");
    }

    // ---- (d) the open-face wedge census ----

    [Fact]
    public void The_open_face_grants_no_wedge_support()
    {
        // SlideWedged grants support to a tick whose demanded descent the world SWALLOWED, because a capsule the
        // world is holding up is being held up by something. Its motivating case is the concave crease, where
        // refusing support is a soft-lock. But the arming test is a SHORTFALL, and ANY curvature under one tick's
        // travel produces one - so on a face whose normal is smoothed, the open face produces them steadily, and
        // each grant is a free jump for a player holding the button. Measured on the real cliff: 5 grants in one
        // climb, with the ring census reading 0 of 8 samples walkable and the fall lines across the ring spread by
        // 1 to 3 degrees. Measured here before the fix: 111 grants in 2400 ticks. A face is not a wedge, whatever
        // the shortfall says, and the fall-line fan is what tells the two apart.
        var t = Tuning;
        (_, _, int grants, _) = Ride(t, Uphill(StartX, StartZ), run: false, jump: false, Dt, seconds: 80f);

        Assert.Equal(0, grants);
    }

    // ---- The footing grant a held jump used to hide ----

    [Fact]
    public void A_held_jump_exports_the_footing_grant_its_launch_consumes()
    {
        // WHY THIS EXISTS. Step 5 launches off the support the tick just found and sets Grounded false on its way
        // out, so a player holding the jump button reports Grounded false on EVERY tick of a hop cycle - and any
        // support the sim granted underneath is invisible to whatever reads the step output. The #468 climber-bot's
        // first sweep read zero footing grants over a 21 m cliff climb for exactly that reason, and concluded
        // footing was not involved when the wedge was in fact granting one every few seconds. MoveState.SupportGranted
        // is the missing half: what the sim decided, before the jump spent it.
        var t = Tuning;
        Func<float, float, float> flat = (x, z) => 0f;
        Func<float, float, Vector3> flatNormals = (x, z) => Vector3.UnitY;
        var s = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true };
        var east = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: true);

        int launches = 0, hiddenGrants = 0, airborneGrants = 0;
        for (int i = 0; i < 300; i++)
        {
            s = CharacterMovement.Step(s, east, Dt, flat, t, flatNormals);
            if (s.VerticalVelocity == t.JumpSpeed)
            {
                launches++;
                // The launch tick: no footing to see in Grounded, the grant plainly visible beside it.
                Assert.False(s.Grounded, $"tick {i} launched and still reported Grounded");
                if (s.SupportGranted) hiddenGrants++;
            }
            else if (!s.Grounded && s.SupportGranted)
            {
                airborneGrants++;   // a grant on a tick that neither landed nor launched: there is no such thing
            }
            else
            {
                // Everywhere else the two agree, so the new field cannot be read as an event of its own.
                Assert.Equal(s.Grounded, s.SupportGranted);
            }
        }

        Assert.True(launches > 10, $"the fixture never ran a hop cycle, launches={launches}");   // ~24 ticks per hop at 30 Hz, so 300 ticks is 14
        Assert.Equal(launches, hiddenGrants);
        Assert.Equal(0, airborneGrants);
    }
}
