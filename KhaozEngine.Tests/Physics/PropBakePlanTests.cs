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
        Assert.IsType<CylinderShape>(plan.Coll);     // thin trunk cylinder
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

    [Fact]
    public void ForProxy_UsesProxyColl_AndSurfaceFromRenderMesh()
    {
        GltfMesh render = TestMeshes.UnitIcosphere();   // walkable solid => gets a surface
        var proxy = new KhaozEngine.Physics.CompoundShape(new[]
        {
            new KhaozEngine.Physics.CompoundChild(
                new KhaozEngine.Physics.BoxShape(new System.Numerics.Vector3(1, 1, 1)),
                KhaozEngine.Physics.Pose.At(System.Numerics.Vector3.Zero)),
        });

        PropBakePlan plan = PropBakePlan.ForProxy(render, proxy);
        Assert.Same(proxy, plan.Coll);     // proxy compound is the collision shape
        Assert.NotNull(plan.Surface);      // walkable solid => surface baked from the render mesh
    }
}
