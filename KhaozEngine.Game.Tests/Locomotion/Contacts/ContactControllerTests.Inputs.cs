// Rows for the inputs around the move: the play-area clamp and the ground core settings.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
    // The coyote row's 2 m ledge with a play-area bound across the walk: x 0.15 for a prop ledge, where the
    // footprint of radius 0.2 still overlaps the ledge, and x -0.05 for terrain, which is sampled at the axis. The
    // clamp cuts the planned move before it runs, so the walk stops at the bound standing on the ledge, as legacy
    // clamps its target. Clamping after the move would let the tick leave the ledge and put the body back over it
    // airborne and below its top.
    [Theory]
    [MemberData(nameof(Grounds))]
    public void ClampAtALedgeEdgeNeverHoversOrSinks(string ground)
    {
        const float Top = 2;
        float bound = ground == "terrain" ? -0.05f : 0.15f;
        using Ground g = Make(ground, v => FootSupportScenes.Floor(v).Flat("ledge", -4, 0, -2, 2, Top),
            (x, _) => x <= 0 ? Top : 0);
        Vector2 Clamp(float x, float z) => new(MathF.Min(x, bound), z);
        MoveState s = Standing(new Vector3(-0.5f, Top, 0));
        for (int tick = 1; tick <= 10; tick++)
        {
            s = g.Step(s, Vector2.UnitX, 1, false, Tuning, clampXz: Clamp);
            Assert.True(s.Grounded, $"tick {tick}: {Show(s)}");
            AssertNear(Top, Feet(s).Y, HalfSkin, $"tick {tick}: {Show(s)}");
            Assert.True(Feet(s).X <= bound, $"tick {tick}: {Show(s)}");
        }
        AssertNear(bound, Feet(s).X, HalfSkin, Show(s));
    }

    // A 60 degree terrain face falls to +X from the origin, and a play-area bound stands at x 2. A body at rest on the
    // face slides down it, reaching about 9 m/s by the bound. The slide plans its own move, so the clamp cuts that move
    // before it runs, and for 60 ticks every tick ends on the face, to half a skin, and inside the bound. Shifting the
    // slide back after the move would leave its feet below the face, where no support or wall ever catches them.
    [Fact]
    public void SlideIntoAClampBoundStaysOnTheSurface()
    {
        const float Bound = 2;
        float grade = MathF.Tan(Radians(60));
        Vector3 normal = Vector3.Normalize(new Vector3(grade, 1, 0));
        using var g = new Ground(null, (x, _) => -grade * x, (_, _) => normal);
        Vector2 Clamp(float x, float z) => new(MathF.Min(x, Bound), z);
        MoveState s = Falling(Vector3.Zero);
        for (int tick = 1; tick <= 60; tick++)
        {
            s = g.Step(s, Vector2.Zero, 0, false, Tuning, clampXz: Clamp);
            Assert.True(Feet(s).X <= Bound, $"tick {tick}: {Show(s)}");
            AssertNear(-grade * Feet(s).X, Feet(s).Y, HalfSkin, $"tick {tick} left the face: {Show(s)}");
        }
        AssertNear(Bound, Feet(s).X, HalfSkin, Show(s));
    }

    // Level terrain, and wherever x + z > 1 a steep face rising out of it, 3 m per metre of x + z, 77 degrees. A
    // play-area bound holds z at 0 or more, across the face's toe. A walk +X at 6 m/s from x 0.8 on z 0 plans moves the
    // clamp leaves whole. From the toe each substep meets the face as a wall, and its tangent along the toe points
    // toward -Z, past the bound. Shifting that end back to z 0 after the move would put the axis on the face, above
    // the feet. The ground core clamps the tangent before it runs, so for 10 ticks each tick ends inside the bound
    // and on the terrain to half a skin.
    [Fact]
    public void WallSlideAcrossAClampBoundStaysOnTheTerrain()
    {
        Vector3 face = Vector3.Normalize(new Vector3(-3, 1, -3));
        static float Height(float x, float z) => x + z > 1 ? 3 * (x + z - 1) : 0;
        using var g = new Ground(null, Height, (x, z) => x + z > 1 ? face : Vector3.UnitY);
        static Vector2 Clamp(float x, float z) => new(x, MathF.Max(z, 0));
        MoveState s = Standing(new Vector3(0.8f, 0, 0));
        for (int tick = 1; tick <= 10; tick++)
        {
            s = g.Step(s, Vector2.UnitX, 1, false, Tuning, clampXz: Clamp);
            Assert.True(Feet(s).Z >= 0, $"tick {tick}: {Show(s)}");
            AssertNear(Height(Feet(s).X, Feet(s).Z), Feet(s).Y, HalfSkin, $"tick {tick} left the terrain: {Show(s)}");
        }
    }

    // The wall slide row's geometry at run speed, held for 600 ticks at 30 and 60 Hz. The run reaches the face's toe
    // at the bound, where each substep meets the face as a wall and its tangent points past the bound. Every substep's
    // move is clamped, the tangent included, so no tick ends outside the bound or moves the feet onto the face. Every
    // tick ends inside the bound, no lower than the terrain at its axis less half a skin, and no more than one climb
    // budget, MaxStepClimbSpeed dt, above where it started: the level terrain is the only ground to stand on, so any
    // rise is a step.
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void RunAlongAClampBoundStaysOnTheTerrain(int hz)
    {
        Vector3 face = Vector3.Normalize(new Vector3(-3, 1, -3));
        static float Height(float x, float z) => x + z > 1 ? 3 * (x + z - 1) : 0;
        using var g = new Ground(null, Height, (x, z) => x + z > 1 ? face : Vector3.UnitY);
        static Vector2 Clamp(float x, float z) => new(x, MathF.Max(z, 0));
        float dt = 1f / hz;
        double budget = (double)Tuning.MaxStepClimbSpeed * dt;
        MoveState s = Standing(new Vector3(0.8f, 0, 0));
        for (int tick = 1; tick <= 600; tick++)
        {
            MoveState next = g.Step(s, Vector2.UnitX, 1, false, Tuning, dt, run: true, clampXz: Clamp);
            Vector3 feet = Feet(next);
            Assert.True(feet.Z >= 0, $"tick {tick}: {Show(next)}");
            Assert.True(feet.Y >= Height(feet.X, feet.Z) - HalfSkin, $"tick {tick} ended below the terrain: {Show(next)}");
            Assert.True(feet.Y - Feet(s).Y <= budget + HalfSkin, $"tick {tick} climbed past one budget: {Show(next)}");
            s = next;
        }
    }

    // A clamp that answers NaN poisons the target. The tick returns the input state with its per-tick events
    // cleared and the commitment the tick advanced, as legacy does.
    [Fact]
    public void NonFiniteClampReturnsTheInputState()
    {
        using var g = new Ground(null, Flat);
        var commitment = new MovementCommitment(7u, Vector2.UnitX, 5, 4, 1, 3);
        MoveState state = Standing(Vector3.Zero) with
        {
            VerticalVelocity = 0.5f,
            HorizontalVelocity = new Vector2(1, 2),
            FacingYaw = 0.3f,
            LandingImpactSpeed = 4,
            SupportGranted = true,
            StepDeltaY = 0.2f,
            Commitment = commitment,
        };

        MoveState result = g.Step(state, Vector2.UnitX, 1, false, Tuning,
            clampXz: (_, _) => new Vector2(float.NaN, float.NaN));

        Assert.Equal(state.Position, result.Position);
        Assert.Equal(state.VerticalVelocity, result.VerticalVelocity);
        Assert.Equal(state.HorizontalVelocity, result.HorizontalVelocity);
        Assert.Equal(state.FacingYaw, result.FacingYaw);
        Assert.True(result.Grounded);
        Assert.Equal(0f, result.LandingImpactSpeed);
        Assert.False(result.SupportGranted);
        Assert.Equal(0f, result.StepDeltaY);
        Assert.Equal(commitment with
        {
            TimeoutRemaining = MathF.Max(0f, commitment.TimeoutRemaining - Dt),
            PreparationRemaining = MathF.Max(0f, commitment.PreparationRemaining - Dt),
        }, result.Commitment);
    }

    // Omitted settings mean the half-radius foot. An explicit zero fraction is invalid and is rejected, not replaced.
    [Fact]
    public void ExplicitZeroFootRadiusIsRejected()
    {
        MoveState state = Standing(Vector3.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => ContactController.Step(state, Vector2.UnitX, 1, false, false,
            Dt, Flat, Tuning, null, null, null, null, settings: new GroundCoreSettings(0)));
    }
}
