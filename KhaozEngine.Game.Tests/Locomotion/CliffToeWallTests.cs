using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// A CLIFF TOE IS A WALL, NOT A SEAT (#486, 17.31.0). Seventh round of the steep-terrain chain, and like #475 it
// comes from a playtest rather than an exploit sweep.
//
// WHAT WAS REPORTED. Walking into the bottom of a steep face on Ruinborne (engine 17.30.0, the traction band in)
// flickers the character into the falling pose instead of simply refusing to walk up. The hysteresis band never
// targeted this: it fixes flip-flopping on marginal ground, and a cliff toe is a step function from walkable beach
// to a face far past gate plus band.
//
// THE MECHANISM, and it is a disagreement between two decisions that should never disagree. A grounded tick's wall
// contact admitted any destination within MoveTuning.StepHeight of the feet, whatever its steepness, on the argument
// that "ground the character can be seated on is admitted and the no-traction rule makes doing so worthless". The
// support decision at the end of the same tick then read that destination's normal, called it too steep, and refused
// footing. So one tick admitted the seat and the next slid off it: Grounded false, the falling pose, a slide back
// onto the beach, footing regained, and the walk into the face again. A grounded-airborne oscillation at every
// steep-face base, at every tick rate, for as long as the key is held.
//
// THE RULE. A tick that STARTS with footing takes the WALL against any destination past its own traction ceiling
// (this tick's resolved gate, which is gate plus band while the character has footing), regardless of how little the
// destination rises. The StepHeight admission survives for everything at or under that ceiling, which is walkable
// ground and band ground, so every legitimate step-up, step-down and stair glide is untouched. Descent is untouched
// too, because a destination BELOW the feet rises by a negative amount and is admitted: cresting onto a steep face
// from above is still a slide, as is falling onto one.
//
// The fixture is the reported shape: a flat beach, a 60 degree face far past gate plus band (45 + 3), and honest
// normals that agree with the height field exactly, so nothing here is measuring a normal/height disagreement.
public class CliffToeWallTests
{
    static MoveTuning Tuning => MoveTuning.Default;

    const float ToeX = 5f;            // where the beach ends and the face starts
    const float FaceRun = 4f;         // horizontal run of the face before it tops out
    static readonly float FaceGradient = MathF.Tan(60f * MathF.PI / 180f);   // 60 degrees, 12 past gate plus band
    static readonly float TopY = FaceRun * FaceGradient;

    // Beach at y = 0, a 60 degree face over [ToeX, ToeX + FaceRun], then a level plateau.
    static float Cliff(float x, float z)
    {
        if (x <= ToeX) return 0f;
        if (x >= ToeX + FaceRun) return TopY;
        return (x - ToeX) * FaceGradient;
    }

    // The honest normal of that height field. Level either side of the face, the face's own plane on it.
    static Vector3 CliffNormal(float x, float z)
        => x <= ToeX || x >= ToeX + FaceRun
            ? Vector3.UnitY
            : Vector3.Normalize(new Vector3(-FaceGradient, 1f, 0f));

    static Func<float, float, float> CliffGround => Cliff;
    static Func<float, float, Vector3> CliffNormals => CliffNormal;

    static MoveCommand Toward(float x, float z, bool run = false)
        => new(Vector2.Normalize(new Vector2(x, z)), run, cameraYaw: 0f, jump: false);

    // Walk a held direction from a standing start on the beach and report what the walk looked like: how many times
    // footing flipped, how many ticks were spent airborne, how far east the feet ever got, and whether the walk ever
    // went BACKWARD (the slide-back half of the reported oscillation).
    readonly record struct Walk(int Flips, int Airborne, float MaxX, float EndX, float EndZ, float MinXAfterMaxX, string Footing);

    static Walk WalkInto(in MoveTuning t, Vector2 dir, float dt, int ticks, float startX = 4f)
    {
        var s = new MoveState
        {
            Position = new Vector3(startX, Cliff(startX, 0f) + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };
        var chars = new char[ticks];
        int flips = 0, airborne = 0;
        bool previous = true;
        float maxX = s.Position.X, minAfterMax = s.Position.X;
        var cmd = new MoveCommand(dir, run: false, cameraYaw: 0f, jump: false);
        for (int i = 0; i < ticks; i++)
        {
            s = CharacterMovement.Step(s, cmd, dt, CliffGround, t, CliffNormals);
            chars[i] = s.Grounded ? 'F' : '.';
            if (s.Grounded != previous) flips++;
            if (!s.Grounded) airborne++;
            previous = s.Grounded;
            if (s.Position.X > maxX) { maxX = s.Position.X; minAfterMax = s.Position.X; }
            else if (s.Position.X < minAfterMax) minAfterMax = s.Position.X;
        }
        return new Walk(flips, airborne, maxX, s.Position.X, s.Position.Z, minAfterMax, new string(chars));
    }

    [Fact]
    public void Walking_into_a_cliff_toe_never_flips_footing()
    {
        // THE REPORTED BUG, as one number. 600 ticks of holding east into a 60 degree face at 30 Hz.
        Walk w = WalkInto(Tuning, new Vector2(1f, 0f), 1f / 30f, 600);

        string measured = $"flips {w.Flips}, airborne {w.Airborne}/600, max x {w.MaxX:F3}, end x {w.EndX:F3}";
        Assert.True(w.Flips == 0, $"footing flipped at the toe: {measured}\n{w.Footing}");
        Assert.True(w.Airborne == 0, $"the toe put the character in the air: {measured}\n{w.Footing}");
        Assert.True(w.MaxX <= ToeX + 1e-3f, $"the feet climbed onto the face: {measured}");
        // Feet stall AT the toe rather than short of it: the walk is refused by the face, not by the beach.
        Assert.True(w.EndX >= ToeX - Tuning.WalkSpeed / 30f, $"the walk stalled short of the toe: {measured}");
        // And it never slides back, which is the other half of the oscillation.
        Assert.True(w.MaxX - w.MinXAfterMaxX < 1e-3f, $"the character slid back off the toe: {measured}\n{w.Footing}");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    public void Walking_into_a_cliff_toe_never_leaves_the_ground_at_any_tick_rate(int hz)
    {
        // The bounce is a per-tick admission, so its rate is the tick rate. A fix that only holds at 30 Hz is the
        // reach admission again with a different constant in it.
        float dt = 1f / hz;
        int ticks = 4 * hz;
        Walk w = WalkInto(Tuning, new Vector2(1f, 0f), dt, ticks);

        string measured = $"{hz} Hz: flips {w.Flips}, airborne {w.Airborne}/{ticks}, max x {w.MaxX:F3}, end x {w.EndX:F3}";
        Assert.True(w.Flips == 0, $"footing flipped at the toe: {measured}");
        Assert.True(w.Airborne == 0, $"the toe put the character in the air: {measured}");
        Assert.True(w.MaxX <= ToeX + 1e-3f, $"the feet climbed onto the face: {measured}");
    }

    [Fact]
    public void Strafing_along_a_cliff_base_keeps_its_along_face_component()
    {
        // The wall contact removes the INTO-face component and nothing else, which is what the whole model has said
        // since 17.28.0. A held 45 degree diagonal into the face keeps every metre of its lateral travel.
        const int Ticks = 300;
        float dt = 1f / 30f;
        Vector2 dir = Vector2.Normalize(new Vector2(1f, 1f));
        Walk w = WalkInto(Tuning, dir, dt, Ticks);

        // Free lateral travel is the along-face component of the commanded speed for the whole run: the diagonal is
        // never denied its z, only its x, so the far side of the wall contact must show all of it. Measured as a
        // magnitude because the camera-relative resolve maps a command's +Y onto world -Z, and the direction the
        // strafe runs along the face is not what this is about.
        float free = Tuning.WalkSpeed * dir.Y * dt * Ticks;
        float along = MathF.Abs(w.EndZ);
        string measured = $"flips {w.Flips}, airborne {w.Airborne}/{Ticks}, along-face {along:F3} of a free {free:F3}";
        Assert.True(w.Flips == 0, $"footing flipped while strafing the toe: {measured}");
        Assert.True(w.Airborne == 0, $"the strafe put the character in the air: {measured}");
        Assert.True(along > 0.99f * free, $"the wall ate lateral travel: {measured}");
        Assert.True(w.MaxX <= ToeX + 1e-3f, $"the diagonal climbed onto the face: max x {w.MaxX:F3}");
    }

    [Fact]
    public void Cresting_onto_a_steep_face_from_above_still_slides()
    {
        // A DESCENDING destination is not an ascent, so the wall test does not fire on it and the slide keeps this
        // entrance. Walk west off the plateau at the top of the same face.
        float dt = 1f / 60f;
        var t = Tuning;
        var s = new MoveState
        {
            Position = new Vector3(ToeX + FaceRun + 1f, TopY + t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };
        var cmd = Toward(-1f, 0f);
        int airborne = 0;
        float minFeet = TopY;
        for (int i = 0; i < 400; i++)
        {
            s = CharacterMovement.Step(s, cmd, dt, CliffGround, t, CliffNormals);
            if (!s.Grounded) airborne++;
            minFeet = MathF.Min(minFeet, s.Position.Y - t.CapsuleHalfHeight);
        }

        Assert.True(airborne > 0, "cresting onto the face never entered a slide");
        Assert.True(minFeet < 1e-3f, $"the crest never reached the beach: lowest feet {minFeet:F3} m");
        // It ends standing on the beach at the bottom, which is where a slide is supposed to terminate.
        Assert.True(s.Grounded, "the slide never terminated on walkable ground");
        Assert.True(s.Position.X < ToeX, $"the slide never left the face: end x {s.Position.X:F3}");
    }

    [Fact]
    public void Falling_onto_a_steep_face_still_slides()
    {
        // A tick that starts WITHOUT footing never reads the widened gate and never reaches the new rule at all, so
        // this entrance is untouched by construction. Pinned anyway, because "untouched by construction" is an
        // argument and this is a measurement.
        float dt = 1f / 60f;
        var t = Tuning;
        float dropX = ToeX + FaceRun / 2f;
        var s = new MoveState
        {
            Position = new Vector3(dropX, Cliff(dropX, 0f) + t.CapsuleHalfHeight + 3f, 0f),
            Grounded = false,
        };
        var cmd = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
        float startY = s.Position.Y;
        bool groundedOnFace = false;
        for (int i = 0; i < 400; i++)
        {
            s = CharacterMovement.Step(s, cmd, dt, CliffGround, t, CliffNormals);
            if (s.Grounded && s.Position.X > ToeX && s.Position.X < ToeX + FaceRun) groundedOnFace = true;
        }

        Assert.False(groundedOnFace, "the fall found footing on the face");
        Assert.True(s.Position.Y < startY, "the fall never descended");
        Assert.True(s.Position.X < ToeX, $"the fall never slid off the face: end x {s.Position.X:F3}");
        Assert.True(s.Grounded, "the slide never terminated on the beach");
    }

    // ---- A walkable riser is still stepped onto ----
    //
    // The rule keys on the destination being past the traction CEILING, and a riser the character is meant to mount
    // is not: a step in a height field is a discontinuity whose columns either side are level, so its normal is
    // level too and the wall contact returns before it ever reads a height.

    const float RiserX = 5f;
    const float RiserHeight = 0.3f;   // inside StepHeight (0.4)

    static float Riser(float x, float z) => x >= RiserX ? RiserHeight : 0f;
    static Vector3 RiserNormal(float x, float z) => Vector3.UnitY;

    [Fact]
    public void A_walkable_riser_is_still_stepped_onto()
    {
        float dt = 1f / 30f;
        var t = Tuning;
        var s = new MoveState
        {
            Position = new Vector3(4f, t.CapsuleHalfHeight, 0f),
            Grounded = true,
        };
        var cmd = Toward(1f, 0f);
        int flips = 0;
        bool previous = true;
        for (int i = 0; i < 120; i++)
        {
            s = CharacterMovement.Step(s, cmd, dt, Riser, t, RiserNormal);
            if (s.Grounded != previous) flips++;
            previous = s.Grounded;
        }

        Assert.Equal(0, flips);
        Assert.True(s.Position.X > RiserX + 1f, $"the riser was never mounted: end x {s.Position.X:F3}");
        Assert.Equal(RiserHeight + t.CapsuleHalfHeight, s.Position.Y, 3);
    }
}
