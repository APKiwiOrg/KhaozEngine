using KhaozEngine.Movement;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement.Fixtures;

internal static class ContactNavigationReuseWorkFixtures
{
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
