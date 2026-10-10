using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class GroundCoreStepTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly GroundCoreSettings Settings = new();

    public static IEnumerable<object[]> TickRows()
    {
        foreach (SceneVariant variant in new[] { SceneVariant.Box, SceneVariant.Mesh })
            foreach (int hz in new[] { 30, 60 }) yield return [variant, hz];
    }

    static GroundStepResult Step(FootSupportScene? scene, Vector3 feet, Vector2 move, int hz,
        MoveTuning? tuning = null, Func<float, float, float>? height = null,
        Func<float, float, Vector3>? normal = null, double? budget = null, bool stepInProgress = false) =>
        GroundCore.Step(feet, move, 1f / hz, tuning ?? Tuning, Settings, height, normal,
            scene?.World, scene?.Lease, climbBudget: budget, stepInProgress: stepInProgress);

    static float Hill(float x, float z) => 0.2f * MathF.Sin(x) * MathF.Cos(z);

    static Vector3 HillNormal(float x, float z) => Vector3.Normalize(new Vector3(
        -0.2f * MathF.Cos(x) * MathF.Cos(z), 1, 0.2f * MathF.Sin(x) * MathF.Sin(z)));

    [Theory]
    [MemberData(nameof(TickRows))]
    public void DoorstepRiseSumsToItsHeight(SceneVariant variant, int hz)
    {
        using FootSupportScene scene = Doorstep(variant, 0.18f);
        var feet = new Vector3(-0.5f, 0, 0);
        var move = new Vector2(Tuning.WalkSpeed / hz, 0);
        double sum = 0;
        int rises = 0;
        bool stepInProgress = false;
        for (int tick = 0; tick < 10; tick++)
        {
            GroundStepResult result = Step(scene, feet, move, hz, stepInProgress: stepInProgress);
            Assert.Equal(GroundFooting.Walkable, result.Footing);
            Assert.True(result.StepRise >= 0, $"tick {tick}: {result}");
            if (result.StepRise != 0) rises++;
            sum += result.StepRise;
            feet = result.Feet;
            stepInProgress = result.StepRise > 0;
        }
        Assert.True(Math.Abs(sum - 0.18f) <= ShellMotion.ContactSkin, $"Summed step rise {sum:R}");
        int maxTicks = (int)Math.Ceiling(0.18f / ((double)Tuning.MaxStepClimbSpeed * (1f / hz))) + 1;
        Assert.True(rises > 0 && rises <= maxTicks,
            $"The doorstep emitted {rises} rise ticks at {hz} Hz, above the bound {maxTicks}.");
    }

    [Theory]
    [MemberData(nameof(TickRows))]
    public void DoorstepDropIsItsNegativeHeight(SceneVariant variant, int hz)
    {
        using FootSupportScene scene = Doorstep(variant, 0.18f);
        var feet = new Vector3(0.5f, 0.18f, 0);
        var move = new Vector2(-Tuning.WalkSpeed / hz, 0);
        var drops = new List<float>();
        for (int tick = 0; tick < 10; tick++)
        {
            GroundStepResult result = Step(scene, feet, move, hz);
            Assert.Equal(GroundFooting.Walkable, result.Footing);
            if (result.StepRise != 0) drops.Add(result.StepRise);
            feet = result.Feet;
        }
        Assert.True(Math.Abs(Assert.Single(drops) + 0.18f) <= ShellMotion.ContactSkin,
            $"Drops: {string.Join(", ", drops)}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CarriedLagEmitsStepsOnlyAboveTheContactSkin(bool above)
    {
        float lag = above ? MathF.BitIncrement(ShellMotion.ContactSkin) : ShellMotion.ContactSkin;
        GroundStepResult result = Step(null, new Vector3(0, -lag, 0), Vector2.Zero, 60, height: (_, _) => 0,
            stepInProgress: true);

        Assert.Equal(GroundFooting.Walkable, result.Footing);
        Assert.Equal(0f, result.Feet.Y);
        Assert.Equal(lag, result.MoveRise);
        Assert.Equal(above ? lag : 0f, result.StepRise);
    }

    public static IEnumerable<object[]> SmoothRows()
    {
        foreach (object[] tick in TickRows())
            foreach (string ground in new[] { "floor", "ramp", "plane", "hill" })
                yield return [tick[0], tick[1], ground];
    }

    [Theory]
    [MemberData(nameof(SmoothRows))]
    public void SmoothGroundHasNoStepRise(SceneVariant variant, int hz, string ground)
    {
        float grade = MathF.Tan(Radians(20));
        Vector3 planeNormal = Vector3.Normalize(new Vector3(-grade, 1, 0));
        using FootSupportScene? scene = ground switch
        {
            "floor" => new FootSupportScene(variant).Flat("floor", -20, 20, -5, 5, 0),
            "ramp" => new FootSupportScene(variant).Slab("ramp", Vector3.Zero, Radians(20), 20, 5),
            _ => null,
        };
        Func<float, float, float>? height = ground switch
        {
            "plane" => (x, _) => grade * x,
            "hill" => Hill,
            _ => null,
        };
        Func<float, float, Vector3>? normal = ground switch
        {
            "plane" => (_, _) => planeNormal,
            "hill" => HillNormal,
            _ => null,
        };
        var feet = new Vector3(0.3f, 0, 0.4f);
        feet.Y = scene is null ? height!(feet.X, feet.Z) : (float)scene.TopHeightAt(ground, feet.X, feet.Z);
        var move = new Vector2(Tuning.WalkSpeed / hz, 0);
        for (int tick = 0; tick < 60; tick++)
        {
            GroundStepResult result = Step(scene, feet, move, hz, height: height, normal: normal);
            Assert.Equal(GroundFooting.Walkable, result.Footing);
            Assert.True(result.StepRise == 0,
                $"{ground}, tick {tick}, StepRise {result.StepRise:R}, from {feet}: {result}");
            feet = result.Feet;
        }
    }

    [Theory]
    [MemberData(nameof(TickRows))]
    public void StairsOnAnAnalyticHillCountOnlyTheRisers(SceneVariant variant, int hz)
    {
        using FootSupportScene scene = Staircase(variant, 4, 0.18f, 0.3f);
        // The approach begins outside the static floor, on the analytic hill. The floor and then the stairs
        // overtake the hill, so both analytic normals and installed tread planes participate in this walk.
        var feet = new Vector3(-5, Hill(-5, 0), 0);
        var move = new Vector2(Tuning.WalkSpeed / hz, 0);
        double sum = 0;
        bool sawTerrain = false, sawStatic = false;
        bool stepInProgress = false;
        for (int tick = 0; tick < 120; tick++)
        {
            GroundStepResult result = Step(scene, feet, feet.X < 1.5f ? move : Vector2.Zero, hz,
                height: Hill, normal: HillNormal, stepInProgress: stepInProgress);
            Assert.Equal(GroundFooting.Walkable, result.Footing);
            sawTerrain |= result.Support.Static is null;
            sawStatic |= result.Support.Static is not null;
            sum += result.StepRise;
            feet = result.Feet;
            stepInProgress = result.StepRise > 0;
            if (feet.X >= 1.5f && Math.Abs(feet.Y - 4 * 0.18f) <= result.Support.HeightError) break;
        }
        Assert.True(sawTerrain && sawStatic, "The walk must use the hill and the staircase.");
        Assert.True(feet.X >= 1.5f, $"The landing was not reached: {feet}");
        Assert.True(Math.Abs(sum - 4 * 0.18f) <= ShellMotion.ContactSkin, $"Summed step rise {sum:R}");
    }

    [Theory]
    [MemberData(nameof(TickRows))]
    public void UnlimitedBudgetAppliesTheStepWhereItIsFound(SceneVariant variant, int hz)
    {
        using FootSupportScene scene = Doorstep(variant, 0.18f);
        MoveTuning tuning = Tuning with { MaxStepClimbSpeed = 0 };
        var feet = new Vector3(-0.5f, 0, 0);
        var rises = new List<float>();
        for (int tick = 0; tick < 10; tick++)
        {
            GroundStepResult result = Step(scene, feet, new Vector2(tuning.WalkSpeed / hz, 0), hz, tuning);
            if (result.StepRise != 0)
            {
                rises.Add(result.StepRise);
                Assert.True(Math.Abs(result.Feet.Y - 0.18f) <= result.Support.HeightError, $"{result}");
            }
            feet = result.Feet;
        }
        Assert.True(Math.Abs(Assert.Single(rises) - 0.18f) <= ShellMotion.ContactSkin / 2);
    }

    [Theory]
    [MemberData(nameof(TickRows))]
    public void DropWhileLaggingReportsTheFeetChange(SceneVariant variant, int hz)
    {
        using FootSupportScene scene = Doorstep(variant, 0.3f);
        GroundStepResult climb = Step(scene, new Vector3(-0.3f, 0, 0), new Vector2(0.2f, 0), hz);
        Assert.True(climb.Feet.Y > 0 && climb.Feet.Y < 0.3f, $"The climb must leave lag: {climb}");
        GroundStepResult drop = Step(scene, climb.Feet, new Vector2(-0.2f, 0), hz,
            stepInProgress: climb.StepRise > 0);
        Assert.Equal(GroundFooting.Walkable, drop.Footing);
        Assert.True(Math.Abs(drop.Feet.Y) <= drop.Support.HeightError, $"{drop}");
        Assert.True(Math.Abs(drop.StepRise - (drop.Feet.Y - climb.Feet.Y)) <= ShellMotion.ContactSkin / 2,
            $"Climb: {climb}, drop: {drop}");
        Assert.True(drop.StepRise < 0, $"{drop}");
    }

    [Fact]
    public void PartialDropKeepsTheRestOwed()
    {
        static float Height(float x, float _) => x < -0.15f ? 0 : x < 0 ? 0.3f : 0.2f;
        static Vector3 Normal(float _, float __) => Vector3.UnitY;
        var move = new Vector2(0.2f, 0);
        GroundStepResult climb = Step(null, new Vector3(-0.3f, 0, 0), move, 60,
            height: Height, normal: Normal);
        Assert.Equal(GroundFooting.Walkable, climb.Footing);
        Assert.Equal(0.3f, climb.Support.Height);
        Assert.True(climb.Feet.Y > 0 && climb.Feet.Y < 0.3f, $"The climb must leave lag: {climb}");

        GroundStepResult drop = Step(null, climb.Feet, move, 60, height: Height, normal: Normal,
            stepInProgress: climb.StepRise > 0);
        Assert.Equal(GroundFooting.Walkable, drop.Footing);
        Assert.Equal(0.2f, drop.Support.Height);
        Assert.True(drop.Feet.Y < 0.2f, $"The partial drop must leave lag: {drop}");
        Assert.True(Math.Abs(drop.StepRise - (drop.Feet.Y - climb.Feet.Y)) <= ShellMotion.ContactSkin / 2,
            $"Climb: {climb}, drop: {drop}");

        double sum = (double)climb.StepRise + drop.StepRise;
        Vector3 feet = drop.Feet;
        bool stepInProgress = drop.StepRise > 0;
        for (int tick = 0; tick < 2; tick++)
        {
            GroundStepResult result = Step(null, feet, Vector2.Zero, 60, height: Height, normal: Normal,
                stepInProgress: stepInProgress);
            Assert.Equal(GroundFooting.Walkable, result.Footing);
            Assert.True(result.StepRise > 0, $"The remaining debt must be paid: {result}");
            Assert.True(Math.Abs(result.StepRise - (result.Feet.Y - feet.Y)) <= ShellMotion.ContactSkin / 2,
                $"tick {tick}, from {feet}: {result}");
            sum += result.StepRise;
            feet = result.Feet;
            stepInProgress = result.StepRise > 0;
        }
        Assert.Equal(0.2f, feet.Y);
        Assert.True(Math.Abs(sum - feet.Y) <= ShellMotion.ContactSkin, $"Summed step rise {sum:R}, feet {feet}");
    }

    [Fact]
    public void PacedHillLagStaysContinuous()
    {
        MoveTuning tuning = Tuning with { MaxStepClimbSpeed = 0.001f };
        var feet = new Vector3(-1, Hill(-1, 0.4f), 0.4f);
        for (int tick = 0; tick < 30; tick++)
        {
            GroundStepResult result = Step(null, feet, new Vector2(0.1f, 0), 60, tuning,
                height: Hill, normal: HillNormal, stepInProgress: false);
            Assert.Equal(GroundFooting.Walkable, result.Footing);
            Assert.True(result.StepRise == 0,
                $"tick {tick}, StepRise {result.StepRise:R}, from {feet}: {result}");
            feet = result.Feet;
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void MoveRiseExcludesRecovery(int hz)
    {
        using FootSupportScene scene = new FootSupportScene(SceneVariant.Box)
            .Flat("floor", -4, 6, -5, 5, 0).Flat("edge", 0, 1, -1, 1, 0.6f);
        var feet = new Vector3(0.1f, 0, 0);
        Assert.True(scene.World.ComputePenetration(ShellGeometry.Shape(Tuning),
            Pose.At(ShellGeometry.Centre(feet, Tuning)), out _));
        GroundStepResult result = Step(scene, feet, Vector2.Zero, hz, budget: 0);
        Assert.True(result.Rise > 0, $"Recovery must raise the feet: {result}");
        Assert.Equal(0f, result.MoveRise);
        Assert.Equal(0f, result.StepRise);
    }
}
