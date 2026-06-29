using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PropBakePlanTests
{
    [Fact]
    public void Tree_PlansCollOnly_NoSurface()
    {
        GltfMesh tree = TestMeshes.LeaningTree();
        PropBakePlan plan = PropBakePlan.For(tree);
        Assert.NotNull(plan.Coll);
        Assert.IsType<ConvexHullShape>(plan.Coll);   // trunk hull
        Assert.Null(plan.Surface);                   // thin blocker: no walkable top
    }

    [Fact]
    public void WalkableSolid_PlansCollAndSurface()
    {
        GltfMesh rock = TestMeshes.UnitIcosphere();
        PropBakePlan plan = PropBakePlan.For(rock);
        Assert.NotNull(plan.Coll);
        Assert.NotNull(plan.Surface);                // walkable solid: surface baked
    }
}
