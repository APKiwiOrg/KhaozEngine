using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class SwimTraversalProbeTests
{
    private const float Dt = 1f / 30f;
    private const float PitHalf = 1.5f;

    internal static readonly MoveTuning Duck = MoveTuning.Default with
    {
        CapsuleRadius = 0.2f,
        CapsuleHalfHeight = 0.25f,
        StepHeight = 0.2f,
        MaxSlopeRadians = 0.8f,
    };

    // Float feet for a surface at 0: 0 - 0.6 x 0.5.
    private static readonly float FloatY = 0f - Duck.SwimSurfaceSubmersionFraction * (2f * Duck.CapsuleHalfHeight);

    [Fact]
    public void FloatEdgeAcrossDeepWaterIsAccepted()
    {
        using BepuPhysicsWorld world = PoolWorld();
        GroundMoveContext context = PoolContext(world);
        Vector3 from = new(-0.5f, FloatY, 0f), to = new(0.5f, FloatY, 0f);

        Assert.True(Float(context, Duck, from, from));
        Assert.True(Float(context, Duck, from, to));
        Assert.True(Float(context, Duck, to, from));
    }

    [Fact]
    public void FloatHoldIsRefusedWhenGroundHeightLiesAboveTheFloatLine()
    {
        using BepuPhysicsWorld world = PoolWorld();
        var flat = new GroundMoveContext((_, _) => 0f, physics: world, medium: PoolMedium);
        Vector3 feet = new(0f, FloatY, 0f);

        Assert.True(Float(PoolContext(world), Duck, feet, feet));
        Assert.False(Float(flat, Duck, feet, feet));
    }

    [Fact]
    public void NarrowDeckBetweenCellCentresIsRefusedByClearance()
    {
        using BepuPhysicsWorld world = PoolWorld();
        // 0.1 m wide in X, centred at x 0.25, underside 0.49 m above the float feet.
        float underside = FloatY + 0.49f;
        world.AddStatic(new BoxShape(new Vector3(0.05f, 0.05f, 1f)), Pose.At(new Vector3(0.25f, underside + 0.05f, 0f)));
        GroundMoveContext context = PoolContext(world);
        Vector3 left = new(0.125f, FloatY, 0f), right = new(0.375f, FloatY, 0f);

        Assert.True(Float(context, Duck, left, left));
        Assert.True(Float(context, Duck, right, right));
        Assert.False(Float(context, Duck, left, right));
        Assert.False(Float(context, Duck, right, left));
        Assert.True(context.SwimClear(Floating(left), Duck));
        Assert.False(context.SwimClear(Floating(new Vector3(0.25f, FloatY, 0f)), Duck));
    }

    [Fact]
    public void PostBesideTheLineRefusesFloatEdges()
    {
        using BepuPhysicsWorld world = PoolWorld();
        world.AddStatic(new BoxShape(new Vector3(0.1f, 1.5f, 0.1f)), Pose.At(new Vector3(0f, -0.5f, 0.25f)));
        GroundMoveContext context = PoolContext(world);
        Vector3 from = new(-0.5f, FloatY, 0f), to = new(0.5f, FloatY, 0f);

        Assert.True(Float(context, Duck, from, from));
        Assert.True(Float(context, Duck, to, to));
        Assert.False(Float(context, Duck, from, to));
        Assert.True(Float(context, Duck, from, new Vector3(-0.5f, FloatY, -0.5f)));
    }

    [Fact]
    public void ZeroSwimSpeedRefusesFloatEdges()
    {
        using BepuPhysicsWorld world = PoolWorld();
        GroundMoveContext context = PoolContext(world);
        MoveTuning still = Duck with { SwimSpeed = 0f };
        Vector3 from = new(-0.5f, FloatY, 0f), to = new(0.5f, FloatY, 0f);

        Assert.True(Float(context, still, from, from));
        Assert.False(Float(context, still, from, to));
        Assert.True(Float(context, Duck, from, to));
    }

    [Fact]
    public void SliceTravelNeverExceedsTheRadius()
    {
        using BepuPhysicsWorld world = PoolWorld();
        var positions = new List<Vector2>();
        var context = new GroundMoveContext((x, z) =>
        {
            if (positions.Count == 0 || positions[^1] != new Vector2(x, z)) positions.Add(new Vector2(x, z));
            return InPit(x, z) ? -2f : 0f;
        }, physics: world, medium: PoolMedium);
        MoveTuning fast = Duck with { SwimSpeed = 30f };
        Vector3 from = new(-0.6f, FloatY, 0f), to = new(0.6f, FloatY, 0f);

        Assert.True(Float(context, fast, from, to));

        Assert.True(positions.Count >= 7, $"Only {positions.Count} swim positions for a 1.2 m edge.");
        float longest = 0f;
        for (int i = 1; i < positions.Count; i++)
            longest = MathF.Max(longest, Vector2.Distance(positions[i - 1], positions[i]));
        Assert.InRange(longest, 0.19f, Duck.CapsuleRadius + 1e-5f);
    }

    [Fact]
    public void ShoreEdgesAreProvenBothWays()
    {
        // Bed falls from 0 at x 0 to -1 at x 4. Surface water at 0. No physics, so clearance passes.
        static float Bed(float x) => Math.Clamp(-0.25f * x, -1f, 0f);
        Vector3 normal = Vector3.Normalize(new Vector3(0.25f, 1f, 0f));
        var context = new GroundMoveContext((x, _) => Bed(x), (x, _) => x > 0f && x < 4f ? normal : Vector3.UnitY,
            medium: (_, _, feetY) => new MovementMedium(0f, feetY < 0f));
        // Wading at bed -0.25 (depth 0.5 of body height, below the enter fraction), floating over bed -0.4.
        Vector3 wade = new(1f, Bed(1f), 0f), afloat = new(1.6f, FloatY, 0f);

        Assert.True(Probe(context, Duck, wade, false, wade, false));
        Assert.True(Probe(context, Duck, afloat, true, afloat, true));
        Assert.True(Probe(context, Duck, wade, false, afloat, true));
        Assert.True(Probe(context, Duck, afloat, true, wade, false));
    }

    [Fact]
    public void ShoreEdgeWithPhysicsClearsItsSwimmingSlices()
    {
        // Wading at bed -0.3, floating over bed -0.38. Swimming starts on the terrace at -0.34 from x 4.
        Vector3 wade = new(3.625f, RampBed(3.625f), 0f), afloat = new(4.625f, FloatY, 0f);
        using (BepuPhysicsWorld world = RampWorld())
        {
            GroundMoveContext context = RampContext(world);
            Assert.True(Probe(context, Duck, wade, false, afloat, true));
            Assert.True(Probe(context, Duck, afloat, true, wade, false));
        }

        // A beam over the swimming stretch only: its underside is below a floating capsule's top, and the walking
        // stretch stays more than a radius away from it. The core does not collide a swimmer, so only the clearance
        // check can refuse these edges.
        using BepuPhysicsWorld beamed = RampWorld();
        beamed.AddStatic(new BoxShape(new Vector3(0.02f, 0.075f, 2f)), Pose.At(new Vector3(4.25f, 0.225f, 0f)));
        GroundMoveContext blocked = RampContext(beamed);
        Assert.True(Probe(blocked, Duck, wade, false, wade, false));
        Assert.True(Probe(blocked, Duck, afloat, true, afloat, true));
        Assert.False(Probe(blocked, Duck, wade, false, afloat, true));
        Assert.False(Probe(blocked, Duck, afloat, true, wade, false));
    }

    [Fact]
    public void BedGrazeNearTheShoreIsClear()
    {
        using var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(4f, 1f, 4f)), Pose.At(new Vector3(0f, -1f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);

        Assert.True(context.SwimClear(Floating(new Vector3(0f, 0.1f, 0f)), Duck));
        Assert.True(context.SwimClear(Floating(new Vector3(0f, -0.05f, 0f)), Duck));
        Assert.False(context.SwimClear(Floating(new Vector3(0f, -0.3f, 0f)), Duck));
        Assert.True(new GroundMoveContext((_, _) => 0f).SwimClear(Floating(new Vector3(0f, -0.3f, 0f)), Duck));
    }

    /// <summary>A terraced bank: land at 0 below x 0, then 0.25 m terraces each 0.02 m lower, down to -0.6 from x 7.5.
    /// Cell centres of a 0.25 m grid from x 0 sit mid-terrace.</summary>
    /// <remarks>Terraces rather than a smooth physics slope: dry ground proofs on a smooth physics slope of this
    /// grade refuse uphill and lateral edges, which predates aquatic profiles.</remarks>
    internal static float RampBed(float x) => x < 0f ? 0f : x >= 7.5f ? -0.6f : -0.02f * (MathF.Floor(x / 0.25f) + 1f);

    /// <summary>The analytic ground matches <see cref="RampWorld"/>, with water at 0 over the whole world.</summary>
    internal static GroundMoveContext RampContext(IPhysicsWorld world, float zoneScale = 1f) =>
        new((x, _) => RampBed(x), physics: world, medium: (_, _, feetY) => new MovementMedium(0f, feetY < 0f, zoneScale));

    /// <summary>The physics of <see cref="RampBed"/>: a land box, 30 terrace boxes and a bed box beyond.</summary>
    internal static BepuPhysicsWorld RampWorld()
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(4f, 0.1f, 8f)), Pose.At(new Vector3(-4f, -0.1f, 0f)));
        for (int k = 0; k < 30; k++)
        {
            float top = -0.02f * (k + 1);
            world.AddStatic(new BoxShape(new Vector3(0.125f, 0.1f, 8f)), Pose.At(new Vector3(0.25f * k + 0.125f, top - 0.1f, 0f)));
        }
        world.AddStatic(new BoxShape(new Vector3(4f, 0.1f, 8f)), Pose.At(new Vector3(11.5f, -0.7f, 0f)));
        return world;
    }

    internal static bool InPit(float x, float z) => MathF.Abs(x) < PitHalf && MathF.Abs(z) < PitHalf;

    /// <summary>Water with its surface at 0 inside the pit, dry land outside.</summary>
    internal static MovementMedium PoolMedium(float x, float z, float feetY) =>
        InPit(x, z) ? new MovementMedium(0f, feetY < 0f) : MovementMedium.Dry;

    /// <summary>The analytic ground matches the captured bed: -2 in the pit, 0 outside.</summary>
    internal static GroundMoveContext PoolContext(IPhysicsWorld world) =>
        new((x, z) => InPit(x, z) ? -2f : 0f, physics: world, medium: PoolMedium);

    /// <summary>A flat floor with its top at 0 around a 3 m square pit whose bed is at -2.</summary>
    internal static BepuPhysicsWorld PoolWorld()
    {
        var world = new BepuPhysicsWorld();
        float side = (8f - PitHalf) / 2f, centre = PitHalf + side;
        world.AddStatic(new BoxShape(new Vector3(side, 0.1f, 8f)), Pose.At(new Vector3(-centre, -0.1f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(side, 0.1f, 8f)), Pose.At(new Vector3(centre, -0.1f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(PitHalf, 0.1f, side)), Pose.At(new Vector3(0f, -0.1f, -centre)));
        world.AddStatic(new BoxShape(new Vector3(PitHalf, 0.1f, side)), Pose.At(new Vector3(0f, -0.1f, centre)));
        world.AddStatic(new BoxShape(new Vector3(PitHalf, 0.1f, PitHalf)), Pose.At(new Vector3(0f, -2.1f, 0f)));
        return world;
    }

    private static MoveState Floating(Vector3 feet) => new()
    {
        Position = feet + Vector3.UnitY * Duck.CapsuleHalfHeight,
        Swimming = true,
        SpeedScale = 1f,
    };

    private static bool Float(GroundMoveContext context, MoveTuning tuning, Vector3 from, Vector3 to)
        => Probe(context, tuning, from, true, to, true);

    private static bool Probe(GroundMoveContext context, MoveTuning tuning, Vector3 from, bool fromFloats,
        Vector3 to, bool toFloats)
        => SwimTraversalProbe.TryEdge(context, tuning, from, fromFloats, to, toFloats, _ => true, Dt, 64);
}
