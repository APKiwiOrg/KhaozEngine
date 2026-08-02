using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// WHAT A SLIDE CONTACT DOES TO CARRIED VELOCITY, and what a character WEDGED between two of them gets.
//
// The first cut of the slide model (#442, 17.28.0) got three things wrong, and this suite is the pin for all
// three. The behavioural half of SteepSlopeSlideTests (no ascent, no tunnel, descent free) is unchanged and
// stays there; everything here is about the CONTACT ITSELF - which components of an incoming velocity die on
// it, which survive, and what happens when two contacts oppose each other so that neither can be left.
//
//   - CONCAVE-CREASE SOFT-LOCK. A character in a V-gully reads a steep column, so it never gets support, and
//     the fall line of either wall points straight into the other, so the wall slide removes the whole
//     horizontal every tick. Nothing moved, nothing grounded, and a held jump could never fire: measured
//     0 grounded ticks in 400. A capsule whose descent the world is SWALLOWING is physically supported, so the
//     resolve now says so on the ticks it is. (The crease is that rule's motivating case, not its condition -
//     see SlideWedged for the arming test itself and for the harmless open-face transient it also admits.)
//   - CONTOUR MOMENTUM. The resolve kept only the fall-line component of the carried velocity, so a fast run
//     ACROSS a face (perpendicular to its fall line, needing no drop at all to follow) was deleted on the
//     contact tick. Only the INTO-SURFACE component may die.
//   - SIGNED FALL LINE. The fall-line speed was clamped non-negative, so a jump grazing a face lost its whole
//     upward along-face motion on the contact tick. The speed is signed now: gravity always accumulates
//     downward along it, so a rising slide decelerates, reverses, and comes back down on its own.
public class SlideContactResolveTests
{
    const float Dt = 1f / 30f;
    const float EdgeX = 5f;
    const float SteepGrade = 5f;          // 78.7 deg: past the 45 deg gate by a wide margin

    static MoveTuning Tuning => MoveTuning.Default;

    // The canonical single face: flat at 0 west of EdgeX, rising 5:1 east of it, with the normal that
    // describes exactly that surface (its outward horizontal points WEST, down the fall line).
    static Func<float, float, float> RisingFace => (x, z) => x < EdgeX ? 0f : (x - EdgeX) * SteepGrade;
    static readonly Vector3 RisingFaceNormal = Vector3.Normalize(new Vector3(-SteepGrade, 1f, 0f));
    static Func<float, float, Vector3> RisingFaceNormals => (x, z) => x < EdgeX ? Vector3.UnitY : RisingFaceNormal;

    // THE V-GULLY: two opposing 5:1 faces meeting along the line x = 0, both far past the gate. East of the
    // crease the ground rises with x, so that wall's fall line points WEST, straight into the other wall;
    // west of it the mirror. There is no walkable column anywhere, which is what makes it a wedge rather than
    // a slide with a slow exit.
    static Func<float, float, float> Gully => (x, z) => MathF.Abs(x) * SteepGrade;
    static readonly Vector3 GullyEastNormal = Vector3.Normalize(new Vector3(-SteepGrade, 1f, 0f));
    static readonly Vector3 GullyWestNormal = Vector3.Normalize(new Vector3(SteepGrade, 1f, 0f));
    static Func<float, float, Vector3> GullyNormals => (x, z) => x >= 0f ? GullyEastNormal : GullyWestNormal;

    static MoveCommand Jump() => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);

    // ---- B2: the concave-crease wedge ----

    [Fact]
    public void A_capsule_wedged_in_a_concave_crease_is_supported_instead_of_falling_forever()
    {
        // THE SOFT-LOCK. Dropped into the crease with no input at all: before the wedge rule the character
        // reported 0 grounded ticks in 400 while its fall-line speed ran away toward terminal in place, with
        // the horizontal sign-flipping between the two walls. A wedged capsule is held up by the two faces,
        // so support is the honest answer and the accumulated fall must not run away.
        var t = Tuning;
        const float StartX = 0.5f;
        var s = new MoveState
        {
            Position = new Vector3(StartX, Gully(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        int groundedTicks = 0, latches = 0;
        float deepestFall = 0f, firstLatch = 0f, loudestAfterFirst = 0f;
        for (int i = 0; i < 400; i++)
        {
            s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, Gully, t, GullyNormals);
            if (s.Grounded) groundedTicks++;
            if (s.LandingImpactSpeed != 0f)
            {
                latches++;
                if (firstLatch == 0f) firstLatch = s.LandingImpactSpeed;
                else loudestAfterFirst = MathF.Max(loudestAfterFirst, s.LandingImpactSpeed);
            }
            deepestFall = MathF.Max(deepestFall, -s.VerticalVelocity);
        }
        string measured = $"grounded={groundedTicks}/400, latches={latches}, first latch {firstLatch:F3} m/s, " +
                          $"loudest after it {loudestAfterFirst:F3} m/s, deepest fall {deepestFall:F3} m/s";

        Assert.True(groundedTicks > 0, $"the crease never granted support in 400 ticks: the soft-lock. {measured}");
        Assert.True(deepestFall < 0.5f * t.MaxFallSpeed,
            $"the wedged capsule accelerated toward terminal in place. {measured}");

        // THE PULSE, pinned because it is what "support for THAT TICK" honestly costs. Support is granted on the
        // ticks the crease actually arrests a fall, so a character parked in one reports a repeating cycle -
        // support, four or five ticks of fresh gravity, support again - rather than steady footing. Measured at
        // the shipped tuning: 86 grounded ticks in 400, about a fifth.
        Assert.InRange(groundedTicks, 40, 160);
        // CONSUMER CONSEQUENCE. Every one of those is an airborne-to-grounded transition, so LandingImpactSpeed
        // latches on every one: a game reading it for a landing SOUND gets a rattle while the character sits in
        // the crease. What it does not get is repeated fall DAMAGE, because only the FIRST latch carries the real
        // fall (11.2 m/s here, the slide down the wall) and every later one is the few ticks of gravity between
        // pulses (4.0 m/s at the shipped tuning). A consumer that gates on impact speed is therefore unaffected.
        Assert.True(firstLatch > 5f, $"the arrival was not latched as a real landing. {measured}");
        Assert.True(loudestAfterFirst < 0.5f * firstLatch,
            $"a repeat pulse latched a full fall rather than the gap's worth of gravity. {measured}");
        // And an ABSOLUTE pin beside the relative one, because the relative bound only says the pulses are quieter
        // than the arrival - which stays true if BOTH grow, and the consumer claim being made here is not relative.
        // What a game needs to know is that no pulse ever reaches a speed it would take fall damage at. Measured at
        // the shipped tuning, over 400 ticks and over 4000: 4.01 m/s, the few ticks of gravity between pulses. 6 m/s
        // covers it with margin and is a fall of under 2 m, far below any sane damage threshold.
        Assert.True(loudestAfterFirst < 6f,
            $"a repeat pulse latched a speed a consumer could plausibly take fall damage at. {measured}");
    }

    [Fact]
    public void A_held_jump_fires_out_of_a_concave_crease()
    {
        // The exit. The jump button is held for the whole window and the coyote clock starts long expired, so
        // before the wedge rule not one launch could ever fire. Support means the launch fires, which is the
        // difference between a crease you can climb out of and a hole the character is stuck in.
        var t = Tuning;
        const float StartX = 0.5f;
        var s = new MoveState
        {
            Position = new Vector3(StartX, Gully(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };
        // The seed sits part way UP one wall, so the character slides into the crease first. The launch is
        // therefore measured from the crease FLOOR it reaches, not from the seed, which is higher than any jump
        // from the bottom would ever reach.
        // Measured from the first LAUNCH rather than the first grounded tick, because they are the same tick and
        // the launch wins it: step 5 consumes the support it fired from, so the state that comes back out of a
        // wedge-then-jump tick reports Grounded false. That is the ordinary jump contract, not a wedge quirk.
        int jumps = 0, launchedAt = -1;
        float floorFeet = float.MaxValue, peakAfterLaunch = float.MinValue;
        for (int i = 0; i < 200; i++)
        {
            s = CharacterMovement.Step(s, Jump(), Dt, Gully, t, GullyNormals);
            if (s.VerticalVelocity == t.JumpSpeed) { jumps++; if (launchedAt < 0) launchedAt = i; }
            float feet = s.Position.Y - t.CapsuleHalfHeight;
            floorFeet = MathF.Min(floorFeet, feet);
            if (launchedAt >= 0) peakAfterLaunch = MathF.Max(peakAfterLaunch, feet);
        }

        Assert.True(jumps > 0, "a held jump never fired in the crease");
        Assert.True(peakAfterLaunch > floorFeet + 1f,
            $"the launch never left the crease floor, peak {peakAfterLaunch:F3} against a floor {floorFeet:F3}");
    }

    // ---- S3: contour momentum survives contact ----

    [Fact]
    public void Contour_momentum_survives_a_slide_contact()
    {
        // A 14 m/s run ACROSS the face - due +Z, which is horizontal, in the surface plane, and perpendicular
        // to the fall line, so following it costs no drop whatsoever. The old resolve kept the fall-line
        // component alone, so this whole velocity died on the contact tick: a fast fall parallel to a wall was
        // stopped dead by the wall it was running alongside. Only the into-surface component may die.
        var t = Tuning;
        const float StartX = EdgeX + 2f;
        var seed = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(0f, 14f),
        };

        MoveState one = CharacterMovement.Step(seed, MoveCommand.Idle, Dt, RisingFace, t, RisingFaceNormals);
        Assert.True(one.HorizontalVelocity.Y > 13.9f,
            $"the contact deleted the contour momentum in one tick: z carry {one.HorizontalVelocity.Y:F4} m/s of 14");

        MoveState s = seed;
        for (int i = 0; i < 10; i++) s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, RisingFace, t, RisingFaceNormals);
        Assert.True(s.Position.X > EdgeX, $"the fixture left the face, x={s.Position.X:F3}");
        Assert.True(s.HorizontalVelocity.Y > 13.9f,
            $"the contour momentum bled away over ten ticks: z carry {s.HorizontalVelocity.Y:F4} m/s of 14");
        Assert.True(s.Position.Z > 4f, $"the contour travel was eaten, z={s.Position.Z:F3}");
    }

    [Fact]
    public void A_slide_still_deletes_the_into_surface_component()
    {
        // The other half of the same rule, so "keep the contour" cannot quietly become "keep everything": a
        // velocity aimed straight INTO the face (due +X, up-slope and into the surface) keeps only what lies in
        // the surface plane. The up-slope part of it survives as a SIGNED fall-line speed (see S4 below); the
        // component along the normal does not, so the capsule cannot be driven through the face.
        var t = Tuning;
        const float StartX = EdgeX + 2f;
        var seed = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(20f, 0f),
        };

        MoveState one = CharacterMovement.Step(seed, MoveCommand.Idle, Dt, RisingFace, t, RisingFaceNormals);
        // Into-surface is deleted, so the surviving speed is the in-plane projection of (20,0,0) onto the
        // tangent: |20 * T.x| = 20 * ny = 3.92 m/s up the fall line, never the whole 20.
        float ny = RisingFaceNormal.Y;
        Assert.True(MathF.Abs(one.HorizontalVelocity.X) < 20f * ny + 0.5f,
            $"the into-surface component survived the contact: x carry {one.HorizontalVelocity.X:F3} m/s of 20");
        Assert.True(one.Position.Y - t.CapsuleHalfHeight >= RisingFace(one.Position.X, one.Position.Z) - 1e-3f,
            $"the capsule was driven under the face, feetY={one.Position.Y - t.CapsuleHalfHeight:F5}");
    }

    // ---- The round-two blocker: a held steer must not erode the carry through the collision clip ----

    // The contour axis on RisingFace is world Z (the face's outward horizontal is due west, so the level
    // direction across it is Z). A camera-relative (0, 1) at yaw 0 is world -Z, so against a carry running +Z it
    // is the steer whose contour component OPPOSES the carried one - which is the case that eroded the carry.
    static MoveCommand AgainstTheContour(bool run = false)
        => new(new Vector2(0f, 1f), run, cameraYaw: 0f, jump: false);

    static MoveState Slide(MoveState seed, MoveCommand cmd, int ticks, in MoveTuning t)
    {
        MoveState s = seed;
        for (int i = 0; i < ticks; i++) s = CharacterMovement.Step(s, cmd, Dt, RisingFace, t, RisingFaceNormals);
        return s;
    }

    [Fact]
    public void A_held_contour_steer_leaves_the_carry_exactly_where_idle_input_leaves_it()
    {
        // THE BLOCKER. A slide tick ADVANCES by the commanded velocity (the carry PLUS this tick's contour
        // steer), but the carry was clipped against the carry ALONE. So ClipToAchieved measured a displacement
        // the steer had helped produce against a vector that did not contain the steer, read the difference as a
        // collision denial, and rescaled the WHOLE carry - fall line included - by it. Measured on this fixture
        // before the fix: the 14 m/s contour carry fell to 8.001 m/s on the first tick and to 0.000 by the tenth,
        // while ten ticks of idle input kept all 14. That is the documented rule inverted. Input has no fall-line
        // authority and does not accumulate into the contour, which means it adds nothing to the carry AND takes
        // nothing from it: the carry may only ever be shed by GEOMETRY.
        //
        // Nothing here is denied by geometry (the face is planar and the contour needs no drop), so the two runs
        // must agree exactly, not approximately. The tolerance is float noise only.
        var t = Tuning;
        const float StartX = EdgeX + 2f;
        var seed = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(0f, 14f),
        };

        foreach (int ticks in new[] { 1, 10 })
        {
            Vector2 idle = Slide(seed, MoveCommand.Idle, ticks, t).HorizontalVelocity;
            Vector2 steered = Slide(seed, AgainstTheContour(), ticks, t).HorizontalVelocity;
            string measured = $"at tick {ticks}: steered carry ({steered.X:F4}, {steered.Y:F4}) against an idle " +
                              $"({idle.X:F4}, {idle.Y:F4})";
            Assert.True(MathF.Abs(steered.Y - idle.Y) < 1e-3f, $"the steer eroded the CONTOUR carry {measured}");
            Assert.True(MathF.Abs(steered.X - idle.X) < 1e-3f, $"the steer eroded the FALL-LINE carry {measured}");
        }
    }

    [Fact]
    public void One_tick_of_opposing_strafe_does_not_destroy_the_fall_line_component()
    {
        // The same fault seen on the component it has no business touching at all. A MIXED carry (running down
        // the fall line AND across the contour) met one tick of run-speed steer opposing the contour, and because
        // the clip rescaled along the carry's own direction, the FALL LINE was scaled by the same factor the
        // contour shortfall produced. Input has no fall-line authority in either direction, so a fall-line
        // component that changes when a strafe key is tapped is the rule failing, not a tuning question.
        //
        // On this face the carry's X IS the fall-line axis (the outward horizontal is due west) and its Z is the
        // contour, so the two components can be read straight off the vector.
        var t = Tuning;
        const float StartX = EdgeX + 2f;
        var seed = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(-8f, 14f),   // 8 m/s down the fall line, 14 across the contour
        };

        Vector2 idle = Slide(seed, MoveCommand.Idle, 1, t).HorizontalVelocity;
        Vector2 steered = Slide(seed, AgainstTheContour(run: true), 1, t).HorizontalVelocity;
        string measured = $"one tick of opposing run-speed strafe left ({steered.X:F4}, {steered.Y:F4}) " +
                          $"against an idle ({idle.X:F4}, {idle.Y:F4})";
        Assert.True(MathF.Abs(steered.X - idle.X) < 1e-3f, $"the fall-line carry was destroyed: {measured}");
        Assert.True(MathF.Abs(steered.Y - idle.Y) < 1e-3f, $"the contour carry was destroyed: {measured}");
    }

    [Fact]
    public void A_wall_contact_sheds_the_same_carry_whether_or_not_a_steer_is_held()
    {
        // THE OTHER HALF, so "the steer cannot erode the carry" does not quietly become "geometry cannot either".
        // The seed slides UP the fall line into a sheer block standing on the face a centimetre above it - a
        // genuine wall contact, its ground 30 m over the feet - while carrying 14 m/s across the contour, which
        // needs no drop and is denied nothing. The face is 46 degrees rather than the file's 78.7, because a
        // near-gate face converts an up-slope horizontal into fall-line speed almost whole (ny is 0.69 there
        // against 0.20), which is what makes the denied share of the carry large enough to measure.
        //
        // The block's outward direction is the fall-line axis and the steer lies on the contour axis, so the
        // steer contributes exactly nothing to what the wall removed. The two runs must therefore agree EXACTLY,
        // not approximately: the clip hands the steer's own share of the displacement back before it measures, so
        // what is left to measure is the carry's own denied travel and nothing else.
        var t = Tuning;
        const float StartX = EdgeX + 2f;
        const float BlockX = StartX + 0.01f;      // sheer, 40 m tall, outward horizontal due west
        float grade = MathF.Tan(46f * MathF.PI / 180f);
        Vector3 faceNormal = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        Func<float, float, float> face = (x, z) => x >= BlockX ? 40f : (x < EdgeX ? 0f : (x - EdgeX) * grade);
        Func<float, float, Vector3> normals = (x, z) => x >= BlockX ? new Vector3(-1f, 0f, 0f)
            : x < EdgeX ? Vector3.UnitY : faceNormal;
        var seed = new MoveState
        {
            Position = new Vector3(StartX, face(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(20f, 14f),   // 20 up-slope INTO the block, 14 across the contour
        };

        MoveState Run(MoveCommand cmd, in MoveTuning tuning)
            => CharacterMovement.Step(seed, cmd, Dt, face, tuning, normals);

        MoveState idle = Run(MoveCommand.Idle, t);
        MoveState steered = Run(AgainstTheContour(run: true), t);
        // The unclipped resolve: the same seed, the same tuning and the same face with the BLOCK TAKEN AWAY, so
        // nothing in front of the capsule is a wall contact and the carry is the raw in-plane velocity.
        //
        // This reference used to be built by handing the tick a StepHeight of 100 instead, which admitted every
        // destination because the wall test read StepHeight. Since #468 a tick with no footing is measured against
        // its OWN RESOLVED VERTICAL MOTION, which no tuning can inflate, so that lever is gone - and it was never
        // an honest "unobstructed" tick anyway: admitting the destination put the capsule 38 m inside the block and
        // left the ground clamp to pop it out the top, which is the exact behaviour #468 retires. Removing the
        // obstacle is what "unobstructed" always meant.
        Func<float, float, float> openFace = (x, z) => x < EdgeX ? 0f : (x - EdgeX) * grade;
        Func<float, float, Vector3> openNormals = (x, z) => x < EdgeX ? Vector3.UnitY : faceNormal;
        Vector2 unclipped = CharacterMovement.Step(seed, MoveCommand.Idle, Dt, openFace, t, openNormals)
            .HorizontalVelocity;

        string measured = $"steered ({steered.HorizontalVelocity.X:F4}, {steered.HorizontalVelocity.Y:F4}), " +
                          $"idle ({idle.HorizontalVelocity.X:F4}, {idle.HorizontalVelocity.Y:F4}), " +
                          $"unclipped ({unclipped.X:F4}, {unclipped.Y:F4})";
        // The wall genuinely bit on both runs: the up-slope travel died and the contour travel did not.
        Assert.True(idle.Position.X <= StartX + 1e-4f, $"the idle run climbed the face, x={idle.Position.X:F5}");
        Assert.True(steered.Position.X <= StartX + 1e-4f, $"the steered run climbed the face, x={steered.Position.X:F5}");
        Assert.True(MathF.Abs(idle.Position.Z) > 0.4f, $"the contour travel died too, z={idle.Position.Z:F5}");
        Assert.True(idle.HorizontalVelocity.Length() < 0.99f * unclipped.Length(),
            $"the wall shed nothing from the carry, so this fixture tests nothing: {measured}");
        // And the shed is the same shed with a steer held.
        Assert.True(MathF.Abs(steered.HorizontalVelocity.X - idle.HorizontalVelocity.X) < 1e-3f,
            $"the steer changed what the wall shed on the fall-line axis: {measured}");
        Assert.True(MathF.Abs(steered.HorizontalVelocity.Y - idle.HorizontalVelocity.Y) < 1e-3f,
            $"the steer changed what the wall shed on the contour axis: {measured}");
    }

    // ---- S4: the fall-line speed is signed ----

    [Fact]
    public void A_rising_graze_along_a_steep_face_keeps_its_up_slope_motion()
    {
        // A jump arriving at the face still rising. Its along-face component points UP the fall line, and the
        // old non-negative clamp deleted it outright, so the launch died the instant it touched the surface.
        // Signed, the contact keeps it and gravity is left to take it back.
        var t = Tuning;
        const float StartX = EdgeX + 0.5f;
        var s = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            VerticalVelocity = t.JumpSpeed,
            HorizontalVelocity = new Vector2(t.WalkSpeed, 0f),
        };
        float seedY = s.Position.Y;

        MoveState one = CharacterMovement.Step(s, MoveCommand.Idle, Dt, RisingFace, t, RisingFaceNormals);
        Assert.True(one.VerticalVelocity > 0f,
            $"the contact deleted the launch: vVel {one.VerticalVelocity:F3} m/s from a {t.JumpSpeed:F3} m/s rise");
        Assert.True(one.Position.Y > seedY, $"the rising graze did not rise, y={one.Position.Y:F5} from {seedY:F5}");
    }

    [Fact]
    public void A_rising_graze_decelerates_reverses_and_comes_back_down()
    {
        // The whole arc, and the reason a signed fall line is not a ratchet: gravity accumulates downward along
        // the fall line whatever the sign, so the rise is transient. The character never has footing on the
        // face while it happens, so there is no second launch to be had up there, and the run ends back at the
        // toe rather than anywhere above where it started.
        var t = Tuning;
        const float StartX = EdgeX + 0.5f;
        var s = new MoveState
        {
            Position = new Vector3(StartX, RisingFace(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            VerticalVelocity = t.JumpSpeed,
            HorizontalVelocity = new Vector2(t.WalkSpeed, 0f),
        };
        float seedFeet = s.Position.Y - t.CapsuleHalfHeight;

        float peakFeet = seedFeet;
        bool rose = false, cameBack = false;
        for (int i = 0; i < 150; i++)
        {
            s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, RisingFace, t, RisingFaceNormals);
            float feet = s.Position.Y - t.CapsuleHalfHeight;
            if (s.Position.X > EdgeX)
                Assert.False(s.Grounded, $"tick {i} found footing on the face at x={s.Position.X:F5}");
            peakFeet = MathF.Max(peakFeet, feet);
            if (feet > seedFeet + 0.5f) rose = true;
            if (rose && feet <= seedFeet) cameBack = true;
        }

        Assert.True(rose, $"the up-slope motion was not carried, peak feet {peakFeet:F3} from a seed {seedFeet:F3}");
        Assert.True(cameBack, $"the rise never reversed, final feet {s.Position.Y - t.CapsuleHalfHeight:F3}");
        Assert.True(s.Grounded, "the arc never settled");
        Assert.Equal(t.CapsuleHalfHeight, s.Position.Y, 3);   // the flat toe, the only ground there is
    }

    // ---- S6: a degenerate slope gate cannot produce an unbounded carry ----

    [Fact]
    public void A_degenerate_slope_gate_cannot_produce_an_unbounded_slide()
    {
        // MaxSlopeRadians = 0 makes every surface off level "too steep", and the terminal divide reads
        // MaxFallSpeed / h with h the normal's horizontal magnitude - which on a near-level face is near zero.
        // Unguarded that is a carry of tens of thousands of metres per second on a 0.6 degree ramp. Two guards
        // hold it: h floors at the sine of a VALIDATED gate, and the horizontal carry is clamped to the wire's
        // own per-axis ceiling, so the sim can never commit a velocity the wire cannot carry.
        MoveTuning t = Tuning with { MaxSlopeRadians = 0f };
        // 3.5e-4 is not an arbitrary shallowness: it is about the SMALLEST non-zero horizontal magnitude a float
        // normal can express (one ulp below a Y of 1 leaves h = sqrt(2 * 6e-8)), so it is the worst case the
        // unguarded divide could ever be handed. MaxFallSpeed / that is ~144000 m/s.
        const float Grade = 3.5e-4f;
        Func<float, float, float> face = (x, z) => x < EdgeX ? 0f : (x - EdgeX) * Grade;
        Vector3 n = Vector3.Normalize(new Vector3(-Grade, 1f, 0f));
        Func<float, float, Vector3> normals = (x, z) => x < EdgeX ? Vector3.UnitY : n;

        const float StartX = EdgeX + 500f;
        var s = new MoveState
        {
            Position = new Vector3(StartX, face(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(-40f, 12f),   // arriving fast, so the resolve reads a real speed
            VerticalVelocity = -t.MaxFallSpeed,
        };

        for (int i = 0; i < 200; i++)
        {
            s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, face, t, normals);
            Assert.True(float.IsFinite(s.Position.X) && float.IsFinite(s.Position.Y),
                $"tick {i} produced a non-finite position, {s.Position}");
            Assert.True(MathF.Abs(s.HorizontalVelocity.X) <= MovementState.MaxHorizontalSpeed,
                $"tick {i} carried {s.HorizontalVelocity.X:F1} m/s, past the wire ceiling");
            Assert.True(MathF.Abs(s.HorizontalVelocity.Y) <= MovementState.MaxHorizontalSpeed,
                $"tick {i} carried {s.HorizontalVelocity.Y:F1} m/s on Z, past the wire ceiling");
            Assert.True(-s.VerticalVelocity <= t.MaxFallSpeed + 1e-3f,
                $"tick {i} fell at {-s.VerticalVelocity:F1} m/s, past terminal");
        }
    }

    [Theory]
    [InlineData(45f)]   // the shipped gate: the terminal fall-line horizontal is MaxFallSpeed / tan(45) = 50 m/s
    [InlineData(20f)]   // shallow enough that MaxFallSpeed / tan(gate) = 137 m/s exceeds the wire's 127
    public void The_slide_carry_never_exceeds_the_wire_horizontal_ceiling(float gateDegrees)
    {
        // The slide's horizontal terminal is MaxFallSpeed / tan(surface angle), largest on the SHALLOWEST face
        // the gate still calls steep. The wire clamps each horizontal axis at MovementState.MaxHorizontalSpeed,
        // so an unclamped sim would quietly disagree with its own replication. The resolve clamps to the same
        // ceiling, so both heads and the wire agree by construction.
        float gate = gateDegrees * MathF.PI / 180f;
        MoveTuning t = Tuning with { MaxSlopeRadians = gate };
        float grade = MathF.Tan(gate * 1.02f);       // a face 2% past the gate: steep, and as shallow as steep gets
        Func<float, float, float> face = (x, z) => x < EdgeX ? 0f : (x - EdgeX) * grade;
        Vector3 n = Vector3.Normalize(new Vector3(-grade, 1f, 0f));
        Func<float, float, Vector3> normals = (x, z) => x < EdgeX ? Vector3.UnitY : n;

        const float StartX = EdgeX + 4000f;          // long enough for the fall line to reach its terminal
        var s = new MoveState
        {
            Position = new Vector3(StartX, face(StartX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = false,
            TimeSinceGrounded = 1f,
        };

        float peak = 0f;
        for (int i = 0; i < 600; i++)
        {
            s = CharacterMovement.Step(s, MoveCommand.Idle, Dt, face, t, normals);
            peak = MathF.Max(peak, MathF.Abs(s.HorizontalVelocity.X));
            Assert.True(MathF.Abs(s.HorizontalVelocity.X) <= MovementState.MaxHorizontalSpeed,
                $"tick {i} carried {s.HorizontalVelocity.X:F2} m/s, past the wire ceiling " +
                $"{MovementState.MaxHorizontalSpeed}");
        }
        Assert.True(peak > 10f, $"the fixture never built a fast slide, peak carry {peak:F2} m/s");
        // The 20 degree gate is chosen so the ceiling actually BINDS: MaxFallSpeed / tan(20.4 deg) is 134 m/s of
        // horizontal, past the wire's 127. Saturating on exactly that number is what pins the resolve's private
        // mirror of MovementState.MaxHorizontalSpeed to the constant it mirrors - a drift between the two would
        // land here rather than in a desync. At the 45 degree gate the terminal horizontal is MaxFallSpeed itself
        // (50 m/s), so the ceiling is nowhere near and the clamp is pure structure.
        if (gateDegrees < 30f) Assert.Equal(MovementState.MaxHorizontalSpeed, peak, 3);
        else Assert.InRange(peak, 40f, 60f);
    }

    // ---- The reviewer's untested lead: a jagged crest at column resolution ----

    [Fact]
    public void A_jagged_crest_alternating_steep_and_walkable_pins_its_landing_latch_stream()
    {
        // A saw-toothed ridge: each 2.08 m period is a 0.4 m riser at 5:1 (78.7 deg, past the gate, so no
        // traction) followed by a 2 m run back down at 11.3 deg (walkable). Running east across it, the
        // character alternates between a steep column (support refused, so it goes airborne for a tick or two
        // and slides) and a walkable one (support granted, so it lands).
        //
        // CONSUMER CONSEQUENCE, and the reason this is pinned rather than designed around: LandingImpactSpeed
        // is a per-tick EVENT, and it is the source a game reads for fall damage and for the landing SOUND. A
        // strip like this therefore emits a STREAM of small latches - a rattle of landing sounds while running
        // over rough ground - rather than one. A consumer that dislikes it gates on the impact SPEED, which is
        // small here by construction (a tick or two of gravity), not on the event's presence. The numbers below
        // are the measured behaviour of the shipped model, recorded so a change to it is visible.
        var t = Tuning;
        var s = new MoveState { Position = new Vector3(0f, Comb(0f, 0f) + t.CapsuleHalfHeight, 0f), Grounded = true };
        var east = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: false);

        int latches = 0, airborneTicks = 0;
        float loudestLatch = 0f;
        for (int i = 0; i < 300; i++)
        {
            s = CharacterMovement.Step(s, east, Dt, Comb, t, CombNormals);
            if (s.LandingImpactSpeed != 0f) { latches++; loudestLatch = MathF.Max(loudestLatch, s.LandingImpactSpeed); }
            if (!s.Grounded) airborneTicks++;
            Assert.True(s.Position.Y - t.CapsuleHalfHeight >= Comb(s.Position.X, s.Position.Z) - 1e-3f,
                $"tick {i} left the capsule under the crest, feetY={s.Position.Y - t.CapsuleHalfHeight:F5}");
        }

        string measured = $"latches={latches}, airborneTicks={airborneTicks}, loudest={loudestLatch:F3} m/s, " +
                          $"x={s.Position.X:F2}";
        Assert.True(s.Position.X > 20f, $"the run never crossed the crest: {measured}");
        // Measured at the shipped tuning over 300 ticks and 100 m of crest: 9 latches, 60 airborne ticks, and the
        // loudest latch 4.29 m/s. So it IS a stream (not one landing), it is far sparser than one latch per
        // period (48 periods crossed), and every latch is a few ticks of gravity rather than a fall.
        Assert.InRange(latches, 2, 40);
        Assert.InRange(airborneTicks, 10, 150);
        Assert.True(loudestLatch < 0.2f * t.MaxFallSpeed,
            $"a crest latch reported a real fall rather than a few ticks of gravity: {measured}");
    }

    const float CombPeriod = 2.08f;
    const float CombRise = 0.08f;                       // 0.4 m of rise at 5:1, one tick's worth of riser
    const float CombFallGrade = 0.4f / (CombPeriod - CombRise);   // 0.2: 11.3 deg, comfortably walkable

    static float Comb(float x, float z)
    {
        float p = x - MathF.Floor(x / CombPeriod) * CombPeriod;
        return p < CombRise ? p * SteepGrade : (CombPeriod - p) * CombFallGrade;
    }

    static readonly Vector3 CombRiseNormal = Vector3.Normalize(new Vector3(-SteepGrade, 1f, 0f));
    static readonly Vector3 CombFallNormal = Vector3.Normalize(new Vector3(CombFallGrade, 1f, 0f));

    static Func<float, float, Vector3> CombNormals => (x, z) =>
    {
        float p = x - MathF.Floor(x / CombPeriod) * CombPeriod;
        return p < CombRise ? CombRiseNormal : CombFallNormal;
    };
}
