using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal static class ContactNavigationReuseWorkFixtures
{
    // The stair fixture's head-on wall contact, isolated from navigation and reuse. Raw Bepu sweep rounding
    // can leave a tiny residual on the second sweep. GroundCore.Try seats only a nonzero achieved move.
    internal static (ShellSweep Approach, ShellSweep Repeat) StairWallSweeps()
    {
        var tuning = Tuning with { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.15625f, StepHeight = 0.21f };
        using var scene = new OrdinaryContactScene(tuning, install: (world, builder) =>
            Box(world, builder, "wall", new(-0.02f, 0.1875f, 0.5f), new(0.02f, 1, 1.5f)));
        ContactQueryContext queries = new DryContactQueryContext(scene.Height, scene.Normal, scene.Physics, null, tuning);
        Vector3 feet = new(-0.06666669f, 0.1875f, 1);
        Vector2 move = new(0.016666668f, 0);
        var approach = ShellMotion.SweepWithQueries(queries, feet, tuning.StepHeight, move, tuning, MathF.Cos(tuning.MaxSlopeRadians));
        feet += new Vector3(approach.Achieved.X, 0, approach.Achieved.Y);
        var repeat = ShellMotion.SweepWithQueries(queries, feet, tuning.StepHeight, move, tuning, MathF.Cos(tuning.MaxSlopeRadians));
        Assert.True(approach.Blocked);
        Assert.True(repeat.Blocked);
        Assert.Equal(0f, approach.Achieved.Y);
        Assert.Equal(0f, repeat.Achieved.Y);
        return (approach, repeat);
    }

    internal static ContactNavigationBaselineTests.BaselineBake WideLip(NavBakeExecution? execution = null)
    {
        using var world = LowLipTraversalTests.LipWorld();
        using var context = Context(world);
        var options = Options with { MinZ = -6, MaxZ = 6, MaxCells = 576, MaxLayerCells = 2304 };
        return Bake(context, options, new("player", Tuning with { CapsuleRadius = 0.3f }, default), execution);
    }

    internal static ContactNavigationBaselineTests.BaselineBake SingleFlat(NavBakeExecution? execution = null)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world);
        return Bake(context, ContactBakeFile.FlatOptions,
            new("player", Tuning with { CapsuleRadius = 0.2f, CapsuleHalfHeight = 0.75f }, default), execution);
    }

    static ContactNavigationBaselineTests.BaselineBake Bake(GroundMoveContext context, PhysicsNavBakeOptions options,
        NavBakeProfile profile, NavBakeExecution? execution)
    {
        var expectation = new NavBakeExpectation(options, ContactBakeFile.Sources(), [profile]);
        using var capture = PhysicsNavBake.Capture(context, options, _ => 0);
        return new(GroundNavigationBake.Create(capture, expectation.Sources, expectation.Profiles, execution), expectation);
    }
}
