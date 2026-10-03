using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

/// <summary>Ground traversal proofs reuse their dry context, footprint predicate and physics scratch.</summary>
[Collection("AllocSensitive")]
public class ProfileAllocationTests
{
    private static MoveTuning Tuning => GroundTraversalProbeTests.Tuning;

    [Fact]
    public void WarmedMediumEdgeProbeAllocatesNothingPerCall()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world, medium: (_, _, _) => MovementMedium.Dry);
        Func<Vector3, bool> accepts = _ => true;
        MoveTuning tuning = Tuning;
        Vector3 to = new(0.25f, 0f, 0f);
        for (int i = 0; i < 3; i++)
            Assert.True(GroundTraversalProbe.TryEdge(context, tuning, Vector3.Zero, to, accepts, 1f / 30f, 64));

        AllocAssert.NoPerCallAllocation("medium TryEdge", () =>
        {
            for (int i = 0; i < 50; i++)
                GroundTraversalProbe.TryEdge(context, tuning, Vector3.Zero, to, accepts, 1f / 30f, 64);
        });
    }

    [Fact]
    public void DryContextIsCachedAndCarriesQuerySelection()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        StaticHandle extra = world.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.At(new Vector3(4f, 0.5f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics(new[] { extra });
        var context = new GroundMoveContext((_, _) => 0f, (_, _) => Vector3.UnitY, world,
            (x, z) => new Vector2(x, z), (_, _, _) => MovementMedium.Dry, view);
        var noMedium = new GroundMoveContext((_, _) => 0f, physics: world);

        Assert.Same(context.DryContext, context.DryContext);
        Assert.Null(context.DryContext.Medium);
        Assert.Same(context.MovementQueries, context.DryContext.MovementQueries);
        Assert.Same(context.GroundHeight, context.DryContext.GroundHeight);
        Assert.Same(context.GroundNormal, context.DryContext.GroundNormal);
        Assert.Same(context.Physics, context.DryContext.Physics);
        Assert.Same(context.ClampXz, context.DryContext.ClampXz);
        Assert.Same(noMedium, noMedium.DryContext);
    }

    [Fact]
    public void BuildProfileStaysWithinOneKibPerColumn()
    {
        const long Budget = 4L * 1024 * 1024;
        using (var warmWorld = GroundTraversalProbeTests.FlatWorld())
        using (var warm = PhysicsNavBake.Capture(new GroundMoveContext((_, _) => 0f, physics: warmWorld),
            new PhysicsNavBakeOptions(-1.5f, -0.5f, 1.5f, 0.5f, 1f, 5f, 6f, 0.8f, 128, 512), _ => 0u))
            warm.BuildProfile(Tuning, default);

        using var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(9f, 0.1f, 9f)), Pose.At(new Vector3(0f, -0.1f, 0f)));
        using var bake = PhysicsNavBake.Capture(new GroundMoveContext((_, _) => 0f, physics: world),
            new PhysicsNavBakeOptions(-8f, -8f, 8f, 8f, 0.25f, 5f, 6f, 0.8f, 4096, 4096), _ => 0u);

        // One retry, as AllocAssert does, so a foreign gen-0 collection inside the window cannot fail a clean build.
        long allocated = Measure(bake, out GroundNavigation nav);
        if (allocated > Budget) allocated = Measure(bake, out nav);

        Assert.True(nav.Graph.IsNodePassable(0, 32, 32));
        Assert.True(nav.Graph.CanTraverse(0, 32, 32, 0, 33, 32));
        Assert.True(allocated <= Budget, $"BuildProfile allocated {allocated} bytes for 4096 columns");
    }

    private static long Measure(PhysicsNavBake bake, out GroundNavigation nav)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        nav = bake.BuildProfile(Tuning, default);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
