using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>
/// The kinematic AI movement seam: <see cref="CharacterMovement.StepTowards"/> drives a server-authoritative NPC
/// through the SAME collision resolution as the player (swept collide-and-slide against an
/// <see cref="IPhysicsWorld"/>, the terrain support floor, the groundNormal wall slide, and the clampXz bounds),
/// but from a WORLD-SPACE steering direction rather than a camera-relative <see cref="MoveCommand"/>. The parity
/// test pins the headline guarantee: an axis-aligned world direction resolves bit-identically to the equivalent
/// camera-relative command, so the AI and player share one collision implementation by construction.
/// </summary>
public class CharacterMovementStepTowardsTests
{
    const float Scale = 1.5f;   // RuinborneWorld.BuildingScale, matching the other real-collider fixtures.
    static readonly MoveTuning Tuning = MoveTuning.Default;   // walk 6 / run 12, half-height 0.9, radius 0.4, 45 deg slope
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    const float Dt = 1f / 60f;

    // A REAL baked collision shape (a solid convex compound authored by ke-propbake), not a synthetic box - so the
    // test exercises the same Bepu sweep/depenetration path the game hits.
    static CompoundShape BlacksmithProxy()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Physics", "Fixtures", "blacksmith_proxy.coll");
        var shape = (CompoundShape)PropCollisionFormat.Read(path);
        return (CompoundShape)PhysicsShapeScale.Uniform(shape, Scale);
    }

    // A ground normal leaning in -X so the surface slope (angle from +Y) is exactly `degrees`: it rises toward +X,
    // the direction the steering tests below drive, so steering east is steering UP it (mirrors the player
    // steep-terrain tests). Paired with SlopeGround so the normal and the height are one surface, which the
    // model needs: everything about it is directional, so a mismatched pair would test nothing.
    static Func<float, float, Vector3> SlopeNormal(float degrees)
    {
        float a = degrees * MathF.PI / 180f;
        var n = new Vector3(-MathF.Sin(a), MathF.Cos(a), 0f);
        return (x, z) => n;
    }

    // The height field of that same surface.
    static Func<float, float, float> SlopeGround(float degrees)
    {
        float grade = MathF.Tan(degrees * MathF.PI / 180f);
        return (x, z) => x * grade;
    }

    [Fact]
    public void SteeredStraightAtAStaticCollider_StopsInsteadOfPenetrating()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(BlacksmithProxy(), Pose.At(Vector3.Zero));
        world.Step(Dt);
        CapsuleShape cap = CharacterMovement.CapsuleFor(Tuning);

        // Start on flat ground well clear on the +X side, steer straight at the solid body in -X world space. Free
        // travel would carry it ~18 m (run 12 * 1.5 s), far past the far side.
        var s = new MoveState { Position = new Vector3(6f, Tuning.CapsuleHalfHeight, 0f), Grounded = true };
        Assert.False(world.ComputePenetration(cap, Pose.At(s.Position), out _), "start must be outside the mesh");
        for (int i = 0; i < 180; i++)
            s = CharacterMovement.StepTowards(s, new Vector2(-1f, 0f), run: true, Dt, Flat, Tuning, null, world);

        Assert.False(world.ComputePenetration(cap, Pose.At(s.Position), out _),
            $"agent penetrated the collider, ended inside the mesh at {s.Position}");
        Assert.True(s.Position.X > 0f,
            $"agent tunneled through the solid body to the far side (x={s.Position.X:F2}); it should stop on the near side");
    }

    [Fact]
    public void SteeringMatchesTheEquivalentCameraRelativeCommand_BitForBit()
    {
        // Parity by construction: an axis-aligned world direction (-1,0) resolves to the SAME unit direction and
        // full speed fraction as a pure-strafe command at yaw 0, so player Step and AI StepTowards must produce
        // byte-identical state every tick, INCLUDING through the collider - proving one shared collision core.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(BlacksmithProxy(), Pose.At(Vector3.Zero));
        world.Step(Dt);

        var start = new MoveState { Position = new Vector3(6f, Tuning.CapsuleHalfHeight, 0f), Grounded = true };
        MoveState player = start, ai = start;
        var cmd = new MoveCommand(new Vector2(-1f, 0f), run: true, cameraYaw: 0f);   // strafe left => world (-1,0,0)
        for (int i = 0; i < 180; i++)
        {
            player = CharacterMovement.Step(player, cmd, Dt, Flat, Tuning, null, world);
            ai = CharacterMovement.StepTowards(ai, new Vector2(-1f, 0f), run: true, Dt, Flat, Tuning, null, world);
            Assert.Equal(player.Position, ai.Position);
            Assert.Equal(player.VerticalVelocity, ai.VerticalVelocity);
            Assert.Equal(player.Grounded, ai.Grounded);
        }
    }

    [Fact]
    public void SteeringUpASlopePastMaxSlope_GetsNoFootingAndSlidesBack()
    {
        // No physics world: the pure analytic surface. A 49 deg slope exceeds the traction budget - the 45 deg gate
        // PLUS the 3 deg hysteresis band a standing character keeps its footing over - so it grants no TRACTION: the
        // agent never gets footing on it, cannot steer up the fall line at all, and slides back down instead.
        // (Through 17.26.1 this fixture asserted the move was REFUSED and the agent stayed put. #442 replaced refusal
        // with sliding, so the honest assertion became that it goes DOWN. It ran at 47 deg until #475 gave the
        // decision a memory, which makes 47 deg ground a standing agent legitimately KEEPS - see
        // TractionHysteresisTests, where that is the behaviour under test rather than a regression. The intent here is
        // unchanged and is about ground past the budget, so the fixture moves past the budget.)
        var s = new MoveState { Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f), Grounded = true };
        Vector3 startPos = s.Position;
        // The altitude available has exactly two sources, and neither is a climb. One is the single StepHeight the
        // ground clamp may seat the first tick's move onto, which is what turns the agent into a slider. The other
        // is the run speed it arrives with: a contact deletes only the into-surface component, so the rest becomes
        // signed up-slope motion, worth at most RunSpeed^2 / 2g before gravity takes it all back. Measured peak here
        // is 1.325 m of the 3.28 m that bounds it. Nothing REPEATS either of them, because there is no footing up
        // there to arrive a second time from.
        //
        // THE WINDOW IS 3 SECONDS since #475, where one used to do. Gravity still decelerates the up-slope ride at
        // FULL strength (friction never lengthens a rise), so the peak is what it always was, but the way back down a
        // face 4 degrees past the gate now runs at half strength, and one second of window ended with the agent still
        // above its start. That is the friction ramp behaving exactly as designed rather than a stall, and the fix is
        // to let the ride finish.
        float ceiling = startPos.Y + Tuning.StepHeight + Tuning.RunSpeed * Tuning.RunSpeed / (2f * Tuning.Gravity);
        float peak = startPos.Y;
        for (int i = 0; i < 180; i++)
        {
            s = CharacterMovement.StepTowards(s, new Vector2(1f, 0f), run: true, Dt, SlopeGround(49f), Tuning, SlopeNormal(49f));
            peak = MathF.Max(peak, s.Position.Y);
            Assert.False(s.Grounded, $"tick {i} found footing on a 49 deg slope");
            Assert.True(s.Position.Y <= ceiling,
                $"tick {i} climbed the slope, y={s.Position.Y:F5} against a ceiling {ceiling:F5}");
            Assert.Equal(startPos.Z, s.Position.Z, 5);   // nothing steers the agent off the fall line
        }
        string measured = $"x={s.Position.X:F5}, y={s.Position.Y:F5}, peak {peak - startPos.Y:F3} m above the start";
        Assert.True(s.Position.X < startPos.X - 0.5f, $"the agent never slid back down: {measured}");
        Assert.True(s.Position.Y < startPos.Y, $"the agent ended no lower than it started: {measured}");
    }

    [Fact]
    public void SteeringUpAGentleSlope_Advances()
    {
        // Control for the slide test: a 30 deg slope is under the budget, so it carries the agent normally and the
        // same steer DOES advance up it. Only the too-steep case loses its footing.
        var s = new MoveState { Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f), Grounded = true };
        for (int i = 0; i < 60; i++)
            s = CharacterMovement.StepTowards(s, new Vector2(1f, 0f), run: true, Dt, SlopeGround(30f), Tuning, SlopeNormal(30f));

        Assert.True(s.Position.X > 0.1f, $"a 30 deg slope should be walkable, x={s.Position.X:F3}");
    }

    [Fact]
    public void SteeringPastACircleBoundsEdge_IsClamped()
    {
        var bounds = new CircleBounds(new Vector2(0f, 0f), radius: 5f);
        Func<float, float, Vector2> clampXz = (x, z) => bounds.Clamp(x, z);

        // Start inside near the +X edge and steer straight out. Free travel would push X well past 5; the clamp holds
        // the agent on the boundary circle.
        var s = new MoveState { Position = new Vector3(4f, Tuning.CapsuleHalfHeight, 0f), Grounded = true };
        for (int i = 0; i < 120; i++)
            s = CharacterMovement.StepTowards(s, new Vector2(1f, 0f), run: true, Dt, Flat, Tuning, null, null, clampXz);

        float r = MathF.Sqrt(s.Position.X * s.Position.X + s.Position.Z * s.Position.Z);
        Assert.True(r <= 5f + 1e-3f, $"agent escaped the CircleBounds (r={r:F4} > 5)");
        Assert.True(r >= 5f - 1e-2f, $"agent did not reach the bound edge (r={r:F4}); clamp not exercised");
    }
}
