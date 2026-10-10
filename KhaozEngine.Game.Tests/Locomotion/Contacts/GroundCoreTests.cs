// Every expectation below is derived from the installed geometry, never from a stepper run. The default tuning
// gives a shell from feet + 0.4 to feet + 1.8 with radius 0.4, substeps of at most 0.2, a footprint radius of 0.2
// and a band from the start support less StepHeight 0.4 to the start feet plus StepHeight.
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class GroundCoreTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly GroundCoreSettings Settings = new(FootRadiusFraction: 0.5f);
    const float Dt = 1f / 30f;

    static GroundStepResult Step(FootSupportScene scene, Vector3 feet, Vector2 displacement) =>
        Step(scene, feet, displacement, Tuning);

    static GroundStepResult Step(FootSupportScene scene, Vector3 feet, Vector2 displacement, MoveTuning tuning) =>
        GroundCore.Step(feet, displacement, Dt, tuning, Settings, null, null, scene.World, scene.Lease);

    static GroundStepResult Step(Func<float, float, float> height, Func<float, float, Vector3>? normal,
        Vector3 feet, Vector2 displacement) =>
        GroundCore.Step(feet, displacement, Dt, Tuning, Settings, height, normal, null, null);

    static FootSupportScene Floor(SceneVariant variant) =>
        new FootSupportScene(variant).Flat("floor", -4, 6, -5, 5, 0);

    static void AssertFooting(GroundFooting expected, GroundStepResult result) =>
        Assert.True(result.Footing == expected, $"Expected {expected}, got {result}");

    static void AssertFeetY(double expected, GroundStepResult result) =>
        Assert.True(Math.Abs(result.Feet.Y - expected) <= result.Support.HeightError,
            $"Expected feet {expected:R}, got {result}");

    static void AssertBitIdentical(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.X), BitConverter.SingleToInt32Bits(actual.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Y), BitConverter.SingleToInt32Bits(actual.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.Z), BitConverter.SingleToInt32Bits(actual.Z));
    }

    static void AssertNear(Vector2 expected, Vector2 actual) =>
        Assert.True(Vector2.Distance(expected, actual) <= 1e-6f, $"Expected {expected}, got {actual}");

    // Moves of 0.3 take two substeps, so exactness holds across a substep boundary.
    public static IEnumerable<object[]> FreeMotionRows()
    {
        Vector2[] slopeMoves = [new(0.3f, 0), new(-0.3f, 0), new(0, 0.3f)];
        Vector2[] flatMoves = [.. slopeMoves, new(0.21f, -0.17f)];
        foreach (SceneVariant variant in new[] { SceneVariant.Box, SceneVariant.Mesh })
        {
            foreach (Vector2 move in flatMoves) yield return ["prop", variant, 0f, move.X, move.Y];
            foreach (float degrees in new[] { 5f, 20f })
                foreach (Vector2 move in slopeMoves) yield return ["slope", variant, degrees, move.X, move.Y];
        }
        foreach (float degrees in new[] { 0f, 20f })
            foreach (Vector2 move in flatMoves) yield return ["terrain", SceneVariant.Box, degrees, move.X, move.Y];
    }

    [Theory]
    [MemberData(nameof(FreeMotionRows))]
    public void FreeMotionIsExact(string ground, SceneVariant variant, float degrees, float dx, float dz)
    {
        var displacement = new Vector2(dx, dz);
        GroundStepResult result;
        double expectedY;
        if (ground == "terrain")
        {
            float grade = MathF.Tan(Radians(degrees));
            Vector3 normal = Vector3.Normalize(new Vector3(-grade, 1, 0));
            result = Step((x, _) => grade * x, (_, _) => normal, Vector3.Zero, displacement);
            expectedY = grade * displacement.X;
            Assert.Equal(0f, result.Support.HeightError);
        }
        else
        {
            using FootSupportScene scene = ground == "prop" ? PropFloor(variant) : Slope(variant, degrees);
            string top = ground == "prop" ? "floor" : "slope";
            var feet = new Vector3(0, (float)scene.TopHeightAt(top, 0, 0), 0);
            result = Step(scene, feet, displacement);
            expectedY = scene.TopHeightAt(top, displacement.X, displacement.Y);
        }

        Assert.Equal(displacement, result.Achieved);
        Assert.Equal(displacement, new Vector2(result.Feet.X, result.Feet.Z));
        Assert.False(result.Blocked, $"{result}");
        AssertFooting(GroundFooting.Walkable, result);
        AssertFeetY(expectedY, result);
    }

    [Theory]
    [InlineData(SceneVariant.Box, 0f)]
    [InlineData(SceneVariant.Mesh, 0f)]
    [InlineData(SceneVariant.Box, 20f)]
    [InlineData(SceneVariant.Mesh, 20f)]
    public void StandingStillHoldsExactly(SceneVariant variant, float degrees)
    {
        using FootSupportScene scene = degrees == 0 ? PropFloor(variant) : Slope(variant, degrees);
        string top = degrees == 0 ? "floor" : "slope";
        var feet = new Vector3(0.5f, (float)scene.TopHeightAt(top, 0.5f, 0), 0.25f);

        GroundStepResult result = Step(scene, feet, Vector2.Zero);

        AssertBitIdentical(feet, result.Feet);
        Assert.Equal(0f, result.Rise);
        Assert.Equal(Vector2.Zero, result.Achieved);
        Assert.False(result.Blocked);
        AssertFooting(GroundFooting.Walkable, result);
    }

    // From x -0.3 the disc ends at -0.1, short of the step face at x 0. One 0.2 substep brings it 0.1 over the
    // step. A step of StepHeight + 0.01 is above the band, so the lifted and the unlifted attempt both refuse and
    // the body stays where it started.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void StepUpWithinStepHeight(SceneVariant variant)
    {
        var start = new Vector3(-0.3f, 0, 0);
        var move = new Vector2(0.2f, 0);

        using (FootSupportScene lip = Lip(variant))
        {
            GroundStepResult result = Step(lip, start, move);
            AssertFooting(GroundFooting.Walkable, result);
            Assert.Equal(lip["lip"], result.Support.Static);
            AssertFeetY(LipTop, result);
            Assert.Equal(move, result.Achieved);
        }

        using (FootSupportScene crate = Crate(variant))
        {
            GroundStepResult result = Step(crate, start, move, Tuning with { MaxStepClimbSpeed = 0 });
            AssertFooting(GroundFooting.Walkable, result);
            Assert.Equal(crate["crate"], result.Support.Static);
            AssertFeetY(CrateTop, result);
            Assert.Equal(move, result.Achieved);

            // Paced, the 0.3 rise is all step part, so the tick climbs only MaxStepClimbSpeed * dt of it and the
            // feet stay below the tread. The allowance is the float rounding of the pacing subtraction.
            GroundStepResult paced = Step(crate, start, move);
            AssertFooting(GroundFooting.Walkable, paced);
            Assert.Equal(crate["crate"], paced.Support.Static);
            Assert.True(Math.Abs(paced.Rise - (double)Tuning.MaxStepClimbSpeed * Dt) <= 1e-6, $"{paced}");
            Assert.Equal(move, paced.Achieved);
        }

        using FootSupportScene tall = Floor(variant).Flat("crate", 0, 1, -1, 1, Tuning.StepHeight + 0.01f);
        GroundStepResult blocked = Step(tall, start, move);
        Assert.True(blocked.Blocked, $"{blocked}");
        AssertFooting(GroundFooting.Walkable, blocked);
        AssertBitIdentical(start, blocked.Feet);
        Assert.Equal(Vector2.Zero, blocked.Achieved);
    }

    // A ledge over x [-2, 0] above the floor at Y 0. From x -0.1 two substeps reach x 0.1, where the ledge edge
    // is still inside the disc, then x 0.3, where it is not.
    [Theory]
    [InlineData(SceneVariant.Box, 0.32f, true)]
    [InlineData(SceneVariant.Mesh, 0.32f, true)]
    [InlineData(SceneVariant.Box, 0.40f, true)]
    [InlineData(SceneVariant.Mesh, 0.40f, true)]
    [InlineData(SceneVariant.Box, 0.41f, false)]
    [InlineData(SceneVariant.Mesh, 0.41f, false)]
    [InlineData(SceneVariant.Box, 0.60f, false)]
    [InlineData(SceneVariant.Mesh, 0.60f, false)]
    public void StepDownSeatsOrFalls(SceneVariant variant, float drop, bool seats)
    {
        using FootSupportScene scene = Floor(variant).Flat("ledge", -2, 0, -2, 2, drop);
        var move = new Vector2(0.4f, 0);

        GroundStepResult result = Step(scene, new Vector3(-0.1f, drop, 0), move);

        Assert.Equal(move, result.Achieved);
        Assert.False(result.Blocked, $"{result}");
        if (seats)
        {
            AssertFooting(GroundFooting.Walkable, result);
            Assert.Equal(scene["floor"], result.Support.Static);
            AssertFeetY(0, result);
        }
        else
        {
            AssertFooting(GroundFooting.None, result);
            Assert.Equal(drop, result.Feet.Y);
            Assert.Equal(0f, result.Rise);
        }
    }

    // A 60 degree face rises from x 0. The first substep reaches (-0.05, 0.05) on the floor. The second reaches
    // x 0.05, where the face is 0.087 above the feet, so it is undone and slides along the face to (-0.05, 0.1).
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SteepRiseBlocksAndSlides(SceneVariant variant)
    {
        using FootSupportScene scene = Ramp(variant, 2f * MathF.Tan(Radians(60f)));

        GroundStepResult result = Step(scene, new Vector3(-0.15f, 0, 0), new Vector2(0.2f, 0.1f));

        Assert.True(result.Blocked, $"{result}");
        AssertFooting(GroundFooting.Walkable, result);
        Assert.Equal(scene["floor"], result.Support.Static);
        AssertFeetY(0, result);
        AssertNear(new Vector2(0.1f, 0.1f), result.Achieved);
    }

    // A 60 degree face falls from the floor edge at x 0. At x 0.21 the edge is outside the disc and the face is
    // 0.364 below the feet, within StepHeight.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void WalkingOffOntoSteepGivesSteep(SceneVariant variant)
    {
        using FootSupportScene scene = Ramp(variant, -2f * MathF.Tan(Radians(60f)));
        var move = new Vector2(0.2f, 0);

        GroundStepResult result = Step(scene, new Vector3(0.01f, 0, 0), move);

        AssertFooting(GroundFooting.Steep, result);
        Assert.Equal(scene["ramp"], result.Support.Static);
        AssertFeetY(scene.TopHeightAt("ramp", 0.21f, 0), result);
        Assert.Equal(move, result.Achieved);
        Assert.False(result.Blocked, $"{result}");
    }

    // Flat until x 0.1, then rising 5 in 1. The first substep reaches (0.1, 0.1) on the flat. The second reaches
    // x 0.2, where the terrain is 0.5, so it slides along the cliff to (0.1, 0.2). Without a normal the wall faces
    // back along the move, which leaves no tangent to slide on.
    [Fact]
    public void AnalyticCliffBlocksAndSlides()
    {
        static float Height(float x, float z) => MathF.Max(0, 5 * (x - 0.1f));
        static Vector3 Normal(float x, float z) =>
            x > 0.1f ? Vector3.Normalize(new Vector3(-5, 1, 0)) : Vector3.UnitY;
        var move = new Vector2(0.2f, 0.2f);

        GroundStepResult slides = Step(Height, Normal, Vector3.Zero, move);
        Assert.True(slides.Blocked, $"{slides}");
        AssertFooting(GroundFooting.Walkable, slides);
        Assert.Equal(0f, slides.Feet.Y);
        AssertNear(new Vector2(0.1f, 0.2f), slides.Achieved);

        GroundStepResult stops = Step(Height, null, Vector3.Zero, move);
        Assert.True(stops.Blocked, $"{stops}");
        AssertFooting(GroundFooting.Walkable, stops);
        AssertNear(new Vector2(0.1f, 0.1f), stops.Achieved);
    }

    // A sphere of radius 0.25 on the floor at x 0.3, its surface from x 0.05. The first substep's disc meets it
    // with or without the lift, so the move is blocked at the start.
    [Fact]
    public void RefusedTargetBlocks()
    {
        using FootSupportScene scene = Floor(SceneVariant.Box);
        scene.World.AddStatic(new SphereShape(0.25f), Pose.At(new Vector3(0.3f, 0, 0)));
        var start = new Vector3(-0.2f, 0, 0);

        GroundStepResult result = Step(scene, start, new Vector2(0.5f, 0));

        Assert.True(result.Blocked, $"{result}");
        AssertFooting(GroundFooting.Walkable, result);
        AssertBitIdentical(start, result.Feet);
        Assert.Equal(Vector2.Zero, result.Achieved);
    }

    [Fact]
    public void RefusedStartHolds()
    {
        using FootSupportScene scene = Floor(SceneVariant.Box);
        scene.World.AddStatic(new SphereShape(0.25f), Pose.At(Vector3.Zero));
        var start = new Vector3(0, 0.25f, 0);

        GroundStepResult result = Step(scene, start, new Vector2(0.2f, 0));

        AssertFooting(GroundFooting.Held, result);
        Assert.Equal(SupportStatus.Refused, result.Support.Status);
        AssertBitIdentical(start, result.Feet);
        Assert.Equal(Vector2.Zero, result.Achieved);
        Assert.Equal(0f, result.Rise);
        Assert.True(result.Blocked, $"{result}");
    }

    [Fact]
    public void LowCeilingBlocksEntry()
    {
        // The ceiling spans x [1, 4] with its underside at 1.5, below the shell top, lifted or not. Its side face
        // at x 1 meets the standing shell's rounded upper cap, whose centre is at body height less radius.
        using (FootSupportScene low = Floor(SceneVariant.Box).Slab("ceiling", new Vector3(2.5f, 1.7f, 0), 0, 1.5f, 3))
        {
            GroundStepResult result = Step(low, new Vector3(0.39f, 0, 0), new Vector2(0.3f, 0));
            Assert.True(result.Blocked, $"{result}");
            AssertFooting(GroundFooting.Walkable, result);
            float capY = 2 * Tuning.CapsuleHalfHeight - Tuning.CapsuleRadius;
            float cornerY = 1.5f - capY;
            float contactX = 1f - MathF.Sqrt(Tuning.CapsuleRadius * Tuning.CapsuleRadius - cornerY * cornerY);
            Assert.Equal(contactX - ShellMotion.ContactSkin - 0.39f, result.Achieved.X, 1e-5f);
            Assert.Equal(0f, result.Achieved.Y);
        }

        // The underside at 1.9 is 0.1 above the unlifted shell top, so the lift is 0.099 and the move is free.
        using FootSupportScene high = Floor(SceneVariant.Box).Slab("ceiling", new Vector3(0, 2.1f, 0), 0, 3, 3);
        var move = new Vector2(0.3f, 0.1f);
        GroundStepResult free = Step(high, Vector3.Zero, move);
        Assert.False(free.Blocked, $"{free}");
        AssertFooting(GroundFooting.Walkable, free);
        Assert.Equal(move, free.Achieved);
        AssertFeetY(0, free);
    }

    // The start is tick 12 of a 6 m/s climb up a 40 degree incline from x -6. A move of 0.20000002 takes two
    // substeps. Summed, they reach x -3.400002, one ulp short of the planned -3.4000018, which is 2e-7 of height
    // here. The feet must sit on the support certified at their own axis.
    [Fact]
    public void FeetSitOnTheSupportAtTheirOwnAxis()
    {
        using FootSupportScene scene = Incline(40);
        var move = new Vector2(6 * Dt, 0);

        GroundStepResult result = Step(scene, new Vector3(-3.6000018f, -3.0207605f, 0), move);

        AssertFooting(GroundFooting.Walkable, result);
        Assert.Equal(move, result.Achieved);
        AssertFeetY(scene.TopHeightAt("incline", result.Feet.X, result.Feet.Z), result);
    }

    // Treads of 0.35 with risers of 0.30. The body stands on tread 1 at x 0.1 with its feet 0.1333 below the
    // tread, as a paced climb leaves them. Tread 2 is then 0.4333 above the feet, past the band. The owed climb is
    // paid first, MaxStepClimbSpeed * dt of it, so the band reaches tread 2 at x 0.25 and the body seats there
    // with nothing left to climb this tick.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void LaggingBodyPaysItsClimbBeforeMoving(SceneVariant variant)
    {
        using FootSupportScene scene = Stairs(variant, 0.35f, 0.30f, 3);
        var start = new Vector3(0.1f, 0.3f - 0.4f / 3f, 0);
        var move = new Vector2(0.15f, 0);
        double budget = (double)Tuning.MaxStepClimbSpeed * Dt;

        GroundStepResult result = Step(scene, start, move);

        AssertFooting(GroundFooting.Walkable, result);
        Assert.False(result.Blocked, $"{result}");
        Assert.Equal(move, result.Achieved);
        Assert.True(Math.Abs(result.Support.Height - 2 * 0.30f) <= result.Support.HeightError, $"{result}");
        Assert.True(Math.Abs(result.Rise - budget) <= 1e-6, $"{result}");
    }

    // A body on the 0.3 crate with its feet 0.3 below the top, as a paced climb that stopped leaves them. Standing
    // still, it pays MaxStepClimbSpeed * dt of the owed climb each tick, so it stands on the crate within
    // ceil(0.3 / budget) = 3 ticks without moving horizontally.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void LaggingBodyAtRestReachesItsTread(SceneVariant variant)
    {
        using FootSupportScene crate = Crate(variant);
        var feet = new Vector3(0.5f, 0, 0.25f);
        double budget = (double)Tuning.MaxStepClimbSpeed * Dt;
        int ticks = (int)Math.Ceiling(CrateTop / budget);
        GroundStepResult result = default;

        for (int tick = 1; tick <= ticks; tick++)
        {
            result = Step(crate, feet, Vector2.Zero);
            AssertFooting(GroundFooting.Walkable, result);
            Assert.False(result.Blocked, $"{result}");
            Assert.Equal(Vector2.Zero, result.Achieved);
            Assert.Equal(feet.X, result.Feet.X);
            Assert.Equal(feet.Z, result.Feet.Z);
            Assert.True(result.Rise <= budget + 1e-6, $"Tick {tick} climbed past the budget: {result}");
            feet = result.Feet;
        }

        Assert.Equal(crate["crate"], result.Support.Static);
        AssertFeetY(CrateTop, result);
    }

    // Treads of 0.35 with risers of 0.30. The body stands at x 0.65 with its feet at 0.6, owing 0.3 of climb onto
    // tread 3 at 0.9. It pays MaxStepClimbSpeed * dt of it first, which spends the budget. At x 0.84 its shell,
    // 0.4 above the feet at 0.7167, comes 0.38 from the tread 4 nosing at (1.05, 1.2), inside the 0.4 radius. A
    // clearance push out of the nosing would raise the body past the spent budget. The standing retry advances
    // only to its analytic nosing contact less skin. Later ticks pay the lag and climb onto the next tread.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void PacedShellAtTheNextNosingWaits(SceneVariant variant)
    {
        using FootSupportScene scene = Stairs(variant, 0.35f, 0.30f, 6);
        var start = new Vector3(0.65f, 2 * 0.30f, 0);
        double budget = (double)Tuning.MaxStepClimbSpeed * Dt;

        GroundStepResult result = Step(scene, start, new Vector2(0.19f, 0));

        AssertFooting(GroundFooting.Walkable, result);
        Assert.True(result.Blocked, $"{result}");
        float capY = result.Feet.Y + Tuning.StepHeight + Tuning.CapsuleRadius;
        float cornerY = capY - 4 * 0.30f;
        float contactX = 3 * 0.35f -
            MathF.Sqrt(Tuning.CapsuleRadius * Tuning.CapsuleRadius - cornerY * cornerY);
        // The skin absorbs sweep error, so the stop bound is half the skin short of the analytic contact. Contact
        // less the full skin would pin backend precision.
        Assert.True(result.Feet.X <= contactX - ShellMotion.ContactSkin / 2, $"{result}");
        Assert.True(result.Achieved.X >= 0 && result.Achieved.X < 0.19f, $"{result}");
        Assert.Equal(0f, result.Achieved.Y);
        Assert.True(Math.Abs(result.Rise - budget) <= 1e-6, $"{result}");
        Vector3 centre = ShellGeometry.Centre(result.Feet, Tuning);
        Assert.False(scene.World.ComputePenetration(ShellGeometry.Shape(Tuning), Pose.At(centre), out _),
            $"The shell overlaps at {result}");

        GroundStepResult climbed = result;
        for (int tick = 0; tick < 20 && climbed.Feet.Y < 4 * 0.30f - climbed.Support.HeightError; tick++)
        {
            climbed = Step(scene, climbed.Feet, new Vector2(0.19f, 0));
            AssertFooting(GroundFooting.Walkable, climbed);
            Assert.True(climbed.Rise <= budget + climbed.Support.HeightError, $"{climbed}");
            Assert.False(scene.World.ComputePenetration(ShellGeometry.Shape(Tuning),
                Pose.At(ShellGeometry.Centre(climbed.Feet, Tuning)), out _), $"{climbed}");
        }
        Assert.True(climbed.Feet.X >= 3 * 0.35f && climbed.Feet.Y >= 4 * 0.30f - climbed.Support.HeightError,
            $"The next tread was not reached: {climbed}");
    }

    // The parameterless constructor carries the documented default. The zero default is still rejected.
    [Fact]
    public void DefaultSettingsUseHalfTheRadius()
    {
        Assert.Equal(0.5f, new GroundCoreSettings().FootRadiusFraction);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroundCore.Step(Vector3.Zero, Vector2.UnitX, Dt, Tuning, default(GroundCoreSettings), (_, _) => 0, null,
                null, null));
    }

    [Fact]
    public void InvalidInputsThrow()
    {
        static float Flat(float x, float z) => 0;
        MoveTuning tuning = Tuning;
        GroundCoreSettings settings = Settings;
        GroundStepResult Run(Vector3 feet, Vector2 move, float dt) =>
            GroundCore.Step(feet, move, dt, tuning, settings, Flat, null, null, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => Run(new Vector3(float.NaN, 0, 0), Vector2.UnitX, Dt));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Vector3.Zero, new Vector2(float.PositiveInfinity, 0), Dt));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Vector3.Zero, Vector2.UnitX, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Vector3.Zero, Vector2.UnitX, -Dt));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Vector3.Zero, Vector2.UnitX, float.NaN));

        settings = new GroundCoreSettings(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(Vector3.Zero, Vector2.UnitX, Dt));

        // A span of 2 * 0.5 - 0.4 = 0.6 cannot hold the 0.8 diameter shell.
        settings = Settings;
        tuning = Tuning with { CapsuleHalfHeight = 0.5f };
        Assert.Throws<ArgumentException>(() => Run(Vector3.Zero, Vector2.UnitX, Dt));
    }
}
