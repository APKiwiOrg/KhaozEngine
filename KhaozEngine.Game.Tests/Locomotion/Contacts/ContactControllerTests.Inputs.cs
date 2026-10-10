// Rows for the inputs around the move: the play-area clamp and the ground core settings.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

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
