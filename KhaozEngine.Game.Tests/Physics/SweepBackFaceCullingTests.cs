using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// A support probe sweeps down through one-sided mesh geometry (#1332). With CullBackFaces a triangle whose front
// normal Cross(C - A, B - A) does not face against the sweep is skipped, so the probe reaches the floor under a
// down-facing or vertical triangle. Every vertex is exactly representable in binary32.
public class SweepBackFaceCullingTests
{
    // The capsule's lower sphere starts at Y 0.605f - 0.005f = 0.4. The floor at Y 0 is met after 0.4 of travel
    // and a surface at Y 0.1 after 0.3. Bepu's sweep converges to within 1e-5 of the shape size estimate over
    // the speed, the capsule radius 0.2 at unit speed gives 2e-6. One binary32 ulp near 0.4 covers the input
    // rounding of the start and the surface heights.
    const float FloorT = 0.4f;
    const float UpperT = 0.3f;
    const float ConvergenceT = 1e-5f * 0.2f;
    const float Ulp = 3e-8f;
    const float Distance = 0.8f;

    static readonly CapsuleShape Capsule = new(0.2f, 0.01f);
    // Off the quad diagonal x + z = 0 so the contact is interior to one triangle.
    static readonly Pose Start = Pose.At(new Vector3(-0.5f, 0.605f, -0.25f));
    static readonly int[] Indices = [0, 1, 2, 3, 4, 5];
    static readonly QueryFilter Culling = QueryFilter.StaticsOnly with { CullBackFaces = true };

    // Quad x, z in [-2, 2] at height y. Wound A, B, C as below, Cross(C - A, B - A) is +Y for the first triangle
    // ((0, 0, 4) x (4, 0, 0) = (0, 16, 0)) and the second. Swapping B and C reverses it to -Y.
    static Vector3[] Quad(float y, bool facesUp)
    {
        Vector3 a = new(-2, y, -2), b = new(2, y, -2), c = new(-2, y, 2);
        Vector3 d = new(2, y, -2), e = new(2, y, 2), f = new(-2, y, 2);
        return facesUp ? [a, b, c, d, e, f] : [a, c, b, d, f, e];
    }

    static void AssertHitAt(SweepHit hit, StaticHandle expected, float t)
    {
        Assert.Equal(expected, hit.Body);
        Assert.InRange(hit.Distance, t - Ulp, t + ConvergenceT + Ulp);
    }

    [Fact]
    public void DownFacingTriangleIsPassedWithCulling()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floor = world.AddStatic(new TriangleMeshShape(Quad(0f, facesUp: true), Indices), Pose.Identity);
        world.AddStatic(new TriangleMeshShape(Quad(0.1f, facesUp: false), Indices), Pose.Identity);

        Assert.True(world.SweepCapsule(Capsule, Start, -Vector3.UnitY, Distance, out SweepHit hit, Culling));
        AssertHitAt(hit, floor, FloorT);
    }

    [Fact]
    public void UpFacingTriangleIsHitWithCulling()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new TriangleMeshShape(Quad(0f, facesUp: true), Indices), Pose.Identity);
        StaticHandle upper = world.AddStatic(new TriangleMeshShape(Quad(0.1f, facesUp: true), Indices), Pose.Identity);

        Assert.True(world.SweepCapsule(Capsule, Start, -Vector3.UnitY, Distance, out SweepHit hit, Culling));
        AssertHitAt(hit, upper, UpperT);
    }

    // A wall triangle in the plane z 0 with its top edge at Y 0.1 straight under the capsule axis. Its normal is
    // along Z either way it is wound, so its dot with the down sweep is exactly zero. A triangle that does not face
    // against the sweep is culled, so the probe passes the edge and meets the floor after 0.4.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerticalTriangleIsCulledWithCulling(bool towardPlusZ)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floor = world.AddStatic(new TriangleMeshShape(Quad(0f, facesUp: true), Indices), Pose.Identity);
        Vector3 a = new(-1, 0.1f, 0), b = new(1, 0.1f, 0), c = new(0, -1, 0);
        Vector3[] wall = towardPlusZ ? [a, b, c] : [a, c, b];
        world.AddStatic(new TriangleMeshShape(wall, [0, 1, 2]), Pose.Identity);

        Assert.True(world.SweepCapsule(Capsule, Pose.At(new Vector3(0, 0.605f, 0)), -Vector3.UnitY, Distance,
            out SweepHit hit, Culling));
        AssertHitAt(hit, floor, FloorT);
    }

    [Fact]
    public void WithoutCullingBackFacesStillBlock()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new TriangleMeshShape(Quad(0f, facesUp: true), Indices), Pose.Identity);
        StaticHandle ceiling = world.AddStatic(new TriangleMeshShape(Quad(0.1f, facesUp: false), Indices), Pose.Identity);

        Assert.True(world.SweepCapsule(Capsule, Start, -Vector3.UnitY, Distance, out SweepHit hit, QueryFilter.StaticsOnly));
        AssertHitAt(hit, ceiling, UpperT);
        Assert.True(world.SweepCapsule(Capsule, Start, -Vector3.UnitY, Distance, out SweepHit defaulted));
        AssertHitAt(defaulted, ceiling, UpperT);
    }

    // The MeshSweepNearestHitTests scenes with culling on. Both meshes face up, so nothing is culled and the
    // nearer floor still wins at t 0.4 whichever static was added first, and a short sweep still reaches a lone
    // mesh within its distance.
    static readonly Vector3[] NearestFloor =
    [
        new(-2, 0, -2), new(0, 0, -2), new(-2, 0, 2),
        new(0, 0, -2), new(0, 0, 2), new(-2, 0, 2),
    ];

    static Vector3[] NearestRamp(float farY) =>
    [
        new(0, 0, -2), new(2, farY, -2), new(0, 0, 2),
        new(2, farY, -2), new(2, farY, 2), new(0, 0, 2),
    ];

    [Theory]
    [InlineData(-0.5f, true)]
    [InlineData(-0.5f, false)]
    [InlineData(0.5f, true)]
    [InlineData(0.5f, false)]
    public void MeshNearestHitHoldsWithCulling(float rampFarY, bool floorFirst)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floor;
        if (floorFirst)
        {
            floor = world.AddStatic(new TriangleMeshShape(NearestFloor, Indices), Pose.Identity);
            world.AddStatic(new TriangleMeshShape(NearestRamp(rampFarY), Indices), Pose.Identity);
        }
        else
        {
            world.AddStatic(new TriangleMeshShape(NearestRamp(rampFarY), Indices), Pose.Identity);
            floor = world.AddStatic(new TriangleMeshShape(NearestFloor, Indices), Pose.Identity);
        }

        Pose start = Pose.At(new Vector3(-0.1f, 0.605f, 0));
        Assert.True(world.SweepCapsule(Capsule, start, -Vector3.UnitY, Distance, out SweepHit hit, Culling));
        AssertHitAt(hit, floor, FloorT);

        // The floor 0.05 below a sweep of 0.1.
        Pose near = Pose.At(new Vector3(-0.1f, 0.255f, 0));
        Assert.True(world.SweepCapsule(Capsule, near, -Vector3.UnitY, 0.1f, out SweepHit shortHit, Culling));
        Assert.Equal(floor, shortHit.Body);
        Assert.InRange(shortHit.Distance, 0.05f - Ulp, 0.05f + ConvergenceT + Ulp);
    }
}
