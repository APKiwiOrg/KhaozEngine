using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// MoveState.FacingYaw: the AUTHORITATIVE character heading, carried tick to tick by the movement step. Before this the
// engine had no facing at all - MoveCommand.CameraYaw crossed the wire every tick, was consumed to resolve the move
// direction, and was never stored - so a server derived facing from position deltas and a STATIONARY character could
// not turn. These pin the update rule on the analytic-terrain path (no physics world needed): the FaceCamera target,
// the commanded-direction target, the idle hold, the shortest arc across the wrap seam, the finite turn rate, the NPC
// StepTowards path, and the invariant the whole feature rests on - that facing moves no position.
public class FacingTests
{
    const float Dt = 1f / 30f;
    const float Tau = MathF.PI * 2f;

    static MoveTuning Tuning => MoveTuning.Default;

    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static MoveCommand Idle(float yaw = 0f, bool faceCamera = false) =>
        new(Vector2.Zero, run: false, cameraYaw: yaw, jump: false, faceCamera: faceCamera);

    static MoveCommand Move(Vector2 axis, float yaw = 0f, bool faceCamera = false) =>
        new(axis, run: false, cameraYaw: yaw, jump: false, faceCamera: faceCamera);

    static MoveState Grounded(float facingYaw = 0f) => new()
    {
        Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f),
        Grounded = true,
        FacingYaw = facingYaw,
    };

    // The signed shortest arc between two headings, for assertions that must not care which representative of the
    // angle the step happened to return.
    static float ArcBetween(float a, float b) => MathF.Abs(CharacterMovement.WrapYaw(a - b));

    // ---- The convention ----

    [Fact]
    public void FacingYawOf_MatchesTheCameraYawConvention()
    {
        // The convention is stated once, on MoveState.FacingYaw: identical to MoveCommand.CameraYaw. CameraYaw's own
        // basis (CharacterMovement.ResolveCameraRelative) is forward = (-sin yaw, 0, -cos yaw), so yaw 0 looks down
        // -Z and a positive yaw swings toward -X. FacingYawOf has to be the exact inverse of that, or a character
        // walking forward under camera yaw t would face something other than t, and the two halves of the update rule
        // (the FaceCamera target and the move-direction target) would disagree by a constant offset nobody could see.
        foreach (float yaw in new[] { 0f, 0.7f, 1.5f, -2.2f, 3f, -3f })
        {
            var forward = new Vector2(-MathF.Sin(yaw), -MathF.Cos(yaw));
            Assert.Equal(yaw, CharacterMovement.FacingYawOf(forward), 5);
        }

        // The cardinal readings, spelled out so the convention cannot drift silently: 0 = -Z, +pi/2 = -X, pi = +Z.
        Assert.Equal(0f, CharacterMovement.FacingYawOf(new Vector2(0f, -1f)), 5);
        Assert.Equal(MathF.PI / 2f, CharacterMovement.FacingYawOf(new Vector2(-1f, 0f)), 5);
        Assert.Equal(-MathF.PI / 2f, CharacterMovement.FacingYawOf(new Vector2(1f, 0f)), 5);
        Assert.Equal(MathF.PI, MathF.Abs(CharacterMovement.FacingYawOf(new Vector2(0f, 1f))), 5);
    }

    [Fact]
    public void WrapYaw_IsCanonicalAndReturnsAnInRangeAngleUntouched()
    {
        // An angle already inside the canonical range comes back BIT-IDENTICAL (no arithmetic runs on it at all),
        // which is what makes "FacingYaw converges to CameraYaw exactly" true rather than approximately: a camera
        // producing canonical yaw sees its own float land in FacingYaw unchanged.
        foreach (float yaw in new[] { 0f, 0.1f, -0.1f, 1.234567f, -3.1415f, -MathF.PI })
            Assert.Equal(yaw, CharacterMovement.WrapYaw(yaw));

        Assert.InRange(CharacterMovement.WrapYaw(MathF.PI), -MathF.PI, MathF.PI);      // the open end wraps to the closed one
        Assert.Equal(0f, ArcBetween(CharacterMovement.WrapYaw(MathF.PI), MathF.PI), 5);
        Assert.Equal(0f, ArcBetween(CharacterMovement.WrapYaw(5f * Tau + 0.3f), 0.3f), 4);
        Assert.Equal(0f, ArcBetween(CharacterMovement.WrapYaw(-5f * Tau - 0.3f), -0.3f), 4);
        foreach (float yaw in new[] { 4f, -4f, 10f, -10f, 100f, -100f })
            Assert.InRange(CharacterMovement.WrapYaw(yaw), -MathF.PI, MathF.PI);
        // A non-finite angle is not an angle. Zero rather than a propagated NaN, because FacingYaw is CARRIED state:
        // a NaN reaching it would not corrupt one frame, it would strand the heading for the rest of the session.
        Assert.Equal(0f, CharacterMovement.WrapYaw(float.NaN));
        Assert.Equal(0f, CharacterMovement.WrapYaw(float.PositiveInfinity));
    }

    // ---- FaceCamera: the right-click ask ----

    [Fact]
    public void FaceCamera_TurnsAStationaryCharacter_TheCaseThatWasImpossible()
    {
        // The headline: a character with NO movement input turns to face the camera. A position-delta derivation
        // cannot do this at all (there is no delta), which is exactly why facing had to become authoritative state.
        MoveTuning t = Tuning;
        MoveState s = Grounded();
        var before = s.Position;

        s = CharacterMovement.Step(s, Idle(2.4f, faceCamera: true), Dt, Flat, t);
        Assert.Equal(2.4f, s.FacingYaw);                       // exact: the default turn speed snaps
        Assert.Equal(before, s.Position);                      // and it moved nothing

        s = CharacterMovement.Step(s, Idle(-1.1f, faceCamera: true), Dt, Flat, t);
        Assert.Equal(-1.1f, s.FacingYaw);
        Assert.Equal(before, s.Position);
    }

    [Fact]
    public void FaceCamera_HoldsTheCameraYawWhileStrafing()
    {
        // Strafing is the case the feature is FOR: the character walks sideways while its body keeps pointing where
        // the camera looks. Without the flag the same command faces the direction of travel (the test below).
        MoveTuning t = Tuning;
        MoveState s = Grounded();
        MoveCommand strafe = Move(new Vector2(1f, 0f), yaw: 0.9f, faceCamera: true);

        float startX = s.Position.X, startZ = s.Position.Z;
        for (int i = 0; i < 5; i++) s = CharacterMovement.Step(s, strafe, Dt, Flat, t);

        Assert.Equal(0.9f, s.FacingYaw);
        Assert.True(new Vector2(s.Position.X - startX, s.Position.Z - startZ).Length() > 0.5f,
            "the strafe did not actually travel, so the facing claim is untested");
    }

    // ---- Without the flag: facing follows the commanded direction, and idle holds ----

    [Fact]
    public void WithoutFaceCamera_FacingFollowsTheCommandedMoveDirection()
    {
        MoveTuning t = Tuning;
        MoveState s = Grounded();

        // Forward under camera yaw 0.9 travels along the camera forward, so the facing IS the camera yaw here. The
        // strafe below is what separates the two rules: same camera yaw, a different heading.
        s = CharacterMovement.Step(s, Move(new Vector2(0f, 1f), yaw: 0.9f), Dt, Flat, t);
        Assert.Equal(0f, ArcBetween(s.FacingYaw, 0.9f), 4);

        s = CharacterMovement.Step(s, Move(new Vector2(1f, 0f), yaw: 0.9f), Dt, Flat, t);
        Assert.Equal(0f, ArcBetween(s.FacingYaw, CharacterMovement.WrapYaw(0.9f - MathF.PI / 2f)), 4);

        s = CharacterMovement.Step(s, Move(new Vector2(0f, -1f), yaw: 0f), Dt, Flat, t);
        Assert.Equal(0f, ArcBetween(s.FacingYaw, MathF.PI), 4);   // backing away from the camera faces +Z
    }

    [Fact]
    public void IdleWithoutFaceCamera_HoldsTheHeading()
    {
        // A stationary character keeps the heading it had. Snapping back to some default on the first idle tick would
        // spin every character on the server the instant its player stopped walking.
        MoveTuning t = Tuning;
        MoveState s = Grounded(facingYaw: 1.75f);
        for (int i = 0; i < 20; i++) s = CharacterMovement.Step(s, Idle(-2.9f), Dt, Flat, t);
        Assert.Equal(1.75f, s.FacingYaw);   // the camera yaw underneath is ignored without the flag
    }

    [Fact]
    public void ADefaultStateFacesTheDefaultHeading()
    {
        // default(MoveState) is 0, which is a legal heading (-Z, the camera-yaw-0 direction), not a sentinel. A state
        // that never went through a step therefore reads as facing forward rather than as facing nowhere.
        Assert.Equal(0f, default(MoveState).FacingYaw);
        Assert.Equal(0f, CharacterMovement.Step(Grounded(), Idle(), Dt, Flat, Tuning).FacingYaw);
    }

    // ---- Shortest arc, at the seam ----

    [Theory]
    [InlineData(3.0f, -3.0f, +1)]     // across the +pi/-pi seam the short way: UP through pi, not back down through 0
    [InlineData(-3.0f, 3.0f, -1)]
    [InlineData(0.2f, 1.2f, +1)]      // an ordinary turn, nowhere near the seam
    [InlineData(1.2f, 0.2f, -1)]
    public void AFiniteTurnTakesTheShortestArc(float from, float to, int expectedSign)
    {
        // The seam is where a naive `target - current` clamp fails: 3.0 -> -3.0 is 0.28 rad the short way and 6.0 rad
        // the long way, and the long way is what a character does when the arc is not wrapped - a full spin on the
        // spot every time the camera crosses due-north.
        MoveTuning t = Tuning with { FacingTurnSpeed = 1f };   // 1 rad/s: one tick turns 0.0333 rad, far short of any of these
        MoveState s = CharacterMovement.Step(Grounded(from), Idle(to, faceCamera: true), Dt, Flat, t);

        float step = CharacterMovement.WrapYaw(s.FacingYaw - from);
        Assert.Equal(expectedSign, MathF.Sign(step));
        Assert.Equal(1f * Dt, MathF.Abs(step), 5);
        // And it genuinely closed the gap rather than opening it.
        Assert.True(ArcBetween(s.FacingYaw, to) < ArcBetween(from, to));
    }

    [Fact]
    public void AFiniteTurnRateTurnsAtTheConfiguredRate_AndLandsExactlyOnTheTarget()
    {
        // The rate is radians per second, so the per-tick step is rate * dt and the whole turn takes gap / rate
        // seconds. The final tick lands EXACTLY on the target rather than one float step beside it, which is what
        // keeps the "converges to CameraYaw exactly" claim true at every turn speed and not only at the default.
        const float Rate = 4f;
        MoveTuning t = Tuning with { FacingTurnSpeed = Rate };
        MoveState s = Grounded();
        const float Target = 2.0f;

        int ticks = 0;
        while (s.FacingYaw != Target && ticks < 100)
        {
            float prev = s.FacingYaw;
            s = CharacterMovement.Step(s, Idle(Target, faceCamera: true), Dt, Flat, t);
            float step = MathF.Abs(CharacterMovement.WrapYaw(s.FacingYaw - prev));
            Assert.True(step <= Rate * Dt + 1e-6f, $"tick {ticks} turned {step} rad, over the {Rate * Dt} rad budget");
            ticks++;
        }

        Assert.Equal(Target, s.FacingYaw);                                   // exact, not merely close
        // 2.0 rad at 4 rad/s = 0.5 s = 15 ticks, plus one for the float accumulation of fifteen additions.
        int expected = (int)MathF.Ceiling(Target / (Rate * Dt));
        Assert.InRange(ticks, expected, expected + 1);
    }

    [Fact]
    public void ATurnSpeedOfZeroFreezesTheHeading()
    {
        // A struct default (default(MoveTuning)) reads 0 here, the same way it reads 0 for WalkSpeed. Freezing the
        // heading is the harmless degradation for that. Anything that treated 0 as "no limit" would make the
        // accidental default the most aggressive setting there is.
        MoveTuning t = Tuning with { FacingTurnSpeed = 0f };
        MoveState s = Grounded(facingYaw: 0.5f);
        for (int i = 0; i < 10; i++) s = CharacterMovement.Step(s, Idle(-2f, faceCamera: true), Dt, Flat, t);
        Assert.Equal(0.5f, s.FacingYaw);
    }

    [Fact]
    public void TheDefaultTurnSpeedIsAnInstantSnap()
    {
        // The default matches today's commanded-facing presentation feel, where a consumer pointed the model straight
        // at CameraRelativeDir with no smoothing at all. A game that wants a rate sets one.
        Assert.Equal(float.PositiveInfinity, MoveTuning.Default.FacingTurnSpeed);
        Assert.Equal(-2.5f, CharacterMovement.Step(Grounded(3f), Idle(-2.5f, faceCamera: true), Dt, Flat, Tuning).FacingYaw);
    }

    // ---- The NPC path ----

    [Fact]
    public void StepTowards_FacesTheSteeringDirection()
    {
        // NPCs run the same core through StepTowards, so they inherit facing for free - and their target is the
        // world-space steering vector, since there is no camera on that path.
        MoveTuning t = Tuning;
        MoveState s = Grounded();

        s = CharacterMovement.StepTowards(s, new Vector2(1f, 0f), run: false, Dt, Flat, t);
        Assert.Equal(0f, ArcBetween(s.FacingYaw, -MathF.PI / 2f), 4);       // travelling +X faces -pi/2

        s = CharacterMovement.StepTowards(s, new Vector2(0f, -1f), run: false, Dt, Flat, t);
        Assert.Equal(0f, ArcBetween(s.FacingYaw, 0f), 4);                    // travelling -Z faces 0

        // A steering vector inside the idle dead-zone holds the heading, exactly as an idle player command does.
        s = CharacterMovement.StepTowards(s, Vector2.Zero, run: false, Dt, Flat, t);
        Assert.Equal(0f, ArcBetween(s.FacingYaw, 0f), 4);
    }

    // ---- The load-bearing invariant: facing moves nothing ----

    [Fact]
    public void FacingChangesNoPositionOutput()
    {
        // Every existing game inherits this feature whether it uses it or not, so the whole design rests on facing
        // being a pure OUTPUT. Three streams over the same commands - facing untouched, facing driven by FaceCamera,
        // and facing crawling at a finite rate - must produce bit-identical positions and vertical state.
        MoveTuning t = Tuning;
        MoveTuning slow = Tuning with { FacingTurnSpeed = 0.7f };
        MoveState plain = Grounded(), faced = Grounded(), crawled = Grounded();
        var yaws = new List<float>();
        float widestPlainGap = 0f, widestCrawlGap = 0f;

        for (int i = 0; i < 60; i++)
        {
            // A command stream that jumps, strafes, releases and swings the camera around the seam, so every branch
            // of the facing resolve is exercised while the positions are compared.
            float yaw = -MathF.PI + (i * 0.37f) % Tau;
            var axis = i % 7 == 0 ? Vector2.Zero : new Vector2(MathF.Sin(i * 0.5f), MathF.Cos(i * 0.9f));
            bool jump = i % 17 == 3;
            var bare = new MoveCommand(axis, run: i % 3 == 0, cameraYaw: yaw, jump: jump);
            var flagged = new MoveCommand(axis, run: i % 3 == 0, cameraYaw: yaw, jump: jump, faceCamera: true);

            plain = CharacterMovement.Step(plain, bare, Dt, Flat, t);
            faced = CharacterMovement.Step(faced, flagged, Dt, Flat, t);
            crawled = CharacterMovement.Step(crawled, flagged, Dt, Flat, slow);
            yaws.Add(faced.FacingYaw);
            widestPlainGap = MathF.Max(widestPlainGap, ArcBetween(plain.FacingYaw, faced.FacingYaw));
            widestCrawlGap = MathF.Max(widestCrawlGap, ArcBetween(crawled.FacingYaw, faced.FacingYaw));

            Assert.Equal(plain.Position, faced.Position);
            Assert.Equal(plain.Position, crawled.Position);
            Assert.Equal(plain.VerticalVelocity, faced.VerticalVelocity);
            Assert.Equal(plain.Grounded, faced.Grounded);
            Assert.Equal(plain.HorizontalVelocity, faced.HorizontalVelocity);
            Assert.Equal(plain.CommandedVelocity, faced.CommandedVelocity);
        }

        // Harness validity: the three streams must actually have DIFFERENT facings, or "positions match" is trivial.
        Assert.True(yaws.Count > 50);
        Assert.True(widestPlainGap > 1f, $"the flagged and unflagged streams never diverged ({widestPlainGap} rad)");
        Assert.True(widestCrawlGap > 1f, $"the crawled stream kept up with the snapped one ({widestCrawlGap} rad)");
    }

    [Fact]
    public void FacingStaysInTheCanonicalRange_AcrossALongCameraSweep()
    {
        // The range is a contract the wire quantizer depends on, so it is pinned on the step's OWN output rather than
        // only on the wrap helper: a sweep that spins the camera many turns must never accumulate a growing angle.
        MoveTuning t = Tuning with { FacingTurnSpeed = 12f };
        MoveState s = Grounded();
        for (int i = 0; i < 400; i++)
        {
            s = CharacterMovement.Step(s, Idle(i * 0.31f, faceCamera: true), Dt, Flat, t);
            Assert.InRange(s.FacingYaw, -MathF.PI, MathF.PI);
        }
    }

    [Fact]
    public void ANonFiniteCameraYawHoldsTheHeading()
    {
        // The wire decode rejects a NaN/Inf yaw outright, but a single-player controller or a server-side NPC driver
        // reaches the step directly. FacingYaw feeds the next tick, so the only safe reading is "hold what we had".
        MoveTuning t = Tuning;
        MoveState s = Grounded(facingYaw: 1.25f);
        s = CharacterMovement.Step(s, Idle(float.NaN, faceCamera: true), Dt, Flat, t);
        Assert.Equal(1.25f, s.FacingYaw);
        s = CharacterMovement.Step(s, Idle(float.PositiveInfinity, faceCamera: true), Dt, Flat, t);
        Assert.Equal(1.25f, s.FacingYaw);
    }

    // ---- The swim path ----

    [Fact]
    public void TheSwimPathFacesToo()
    {
        // SwimStep returns early from StepCore, so it is its own facing site: a swimmer that could not turn would be
        // the one place the rule silently did not apply.
        MoveTuning t = Tuning;
        Func<float, float, float, MovementMedium> water = (x, z, feetY) => new MovementMedium(10f, inWater: true);
        var s = new MoveState
        {
            Position = new Vector3(0f, 9f, 0f),
            Swimming = true,
            FacingYaw = 0f,
        };

        s = CharacterMovement.Step(s, Idle(1.4f, faceCamera: true), Dt, Flat, t, medium: water);
        Assert.True(s.Swimming, "the fixture stopped swimming, so the swim facing site was not exercised");
        Assert.Equal(1.4f, s.FacingYaw);

        s = CharacterMovement.Step(s, Move(new Vector2(1f, 0f), yaw: 0f), Dt, Flat, t, medium: water);
        Assert.Equal(0f, ArcBetween(s.FacingYaw, -MathF.PI / 2f), 4);   // swimming +X faces -pi/2
    }

    // ---- MoveCommand source compatibility ----

    [Fact]
    public void TheFlagIsOptionalAndDefaultsOff()
    {
        // Every existing 3- and 4-argument construction site keeps compiling and keeps meaning what it meant.
        Assert.False(new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f).FaceCamera);
        Assert.False(new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true).FaceCamera);
        Assert.False(MoveCommand.Idle.FaceCamera);
        Assert.True(new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false, faceCamera: true).FaceCamera);
    }
}
