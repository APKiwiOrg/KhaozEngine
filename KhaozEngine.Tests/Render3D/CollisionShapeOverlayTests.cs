using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class CollisionShapeOverlayTests
{
    [Fact]
    public void BuildMeshes_makes_one_mesh_per_static_with_pose_world()
    {
        var statics = new[]
        {
            new CollisionStatic(new BoxShape(Vector3.One), new Pose(new Vector3(3, 0, 0), Quaternion.Identity)),
            new CollisionStatic(new SphereShape(1f), new Pose(new Vector3(0, 5, 0), Quaternion.Identity)),
        };
        var built = CollisionShapeOverlay.BuildMeshes(statics, new CollisionOverlayPalette(), out var kinds);
        Assert.Equal(2, built.Length);
        Assert.Equal(new Vector3(3, 0, 0), built[0].World.Translation, PosCmp);
        Assert.Equal(new Vector3(0, 5, 0), built[1].World.Translation, PosCmp);
        Assert.Contains(CollisionShapeKind.Box, kinds);
        Assert.Contains(CollisionShapeKind.Sphere, kinds);
    }

    [Fact]
    public void PresentKinds_are_distinct()
    {
        var statics = new[]
        {
            new CollisionStatic(new BoxShape(Vector3.One), Pose.At(Vector3.Zero)),
            new CollisionStatic(new BoxShape(Vector3.One), Pose.At(Vector3.UnitX)),
        };
        _ = CollisionShapeOverlay.BuildMeshes(statics, new CollisionOverlayPalette(), out var kinds);
        Assert.Single(kinds);
        Assert.Equal(CollisionShapeKind.Box, kinds[0]);
    }

    [Fact]
    public void Compound_present_kinds_include_all_child_kinds()
    {
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(new BoxShape(Vector3.One), Pose.At(Vector3.Zero)),
            new CompoundChild(new SphereShape(1f), Pose.At(Vector3.UnitZ)),
        });
        _ = CollisionShapeOverlay.BuildMeshes(new[] { new CollisionStatic(compound, Pose.At(Vector3.Zero)) },
            new CollisionOverlayPalette(), out var kinds);
        Assert.Contains(CollisionShapeKind.Box, kinds);
        Assert.Contains(CollisionShapeKind.Sphere, kinds);
    }

    static readonly VecCmp PosCmp = new();
    sealed class VecCmp : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
        public int GetHashCode(Vector3 v) => 0;
    }
}
