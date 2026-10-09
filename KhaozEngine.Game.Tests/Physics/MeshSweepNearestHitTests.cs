using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// A capsule swept down beside the shared edge of two mesh statics must report the nearer one. The floor is
// hit flat at t 0.4. The ramp is only reached at its shared edge point, later at t 0.42679. Every vertex is
// exactly representable in binary32.
public class MeshSweepNearestHitTests
{
    // The capsule's lower sphere starts at Y 0.605f - 0.005f and meets the floor after 0.4 of travel. Bepu
    // reports an intersecting sample t1 that converged to within its convergence threshold of the true time.
    // Simulation.Sweep sets that threshold to 1e-5 of the shape size estimate over the speed: the capsule
    // radius 0.2 at unit speed gives 2e-6. One binary32 ulp near 0.4 (about 3e-8) covers the input rounding.
    const float FloorT = 0.4f;
    const float ConvergenceT = 1e-5f * 0.2f;
    const float Ulp = 3e-8f;

    static readonly CapsuleShape Capsule = new(0.2f, 0.01f);
    static readonly Pose Start = Pose.At(new Vector3(-0.1f, 0.605f, 0)); // lowest point at Y 0.4
    static readonly int[] Indices = [0, 1, 2, 3, 4, 5];

    // Level quad x in [-2, 0], z in [-2, 2] at Y 0. Faces up.
    static readonly Vector3[] Floor =
    [
        new(-2, 0, -2), new(0, 0, -2), new(-2, 0, 2),
        new(0, 0, -2), new(0, 0, 2), new(-2, 0, 2),
    ];

    // From the floor's edge at x 0 to x 2 at the given far height. Faces up. Shares the floor's edge exactly.
    static Vector3[] Ramp(float farY) =>
    [
        new(0, 0, -2), new(2, farY, -2), new(0, 0, 2),
        new(2, farY, -2), new(2, farY, 2), new(0, 0, 2),
    ];

    [Theory]
    [InlineData(-0.5f, true)]
    [InlineData(-0.5f, false)]
    [InlineData(0.5f, true)]
    [InlineData(0.5f, false)]
    public void CapsuleSweepKeepsTheNearerOfTwoEdgeSharingMeshes(float rampFarY, bool floorFirst)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floorHandle, rampHandle;
        if (floorFirst)
        {
            floorHandle = world.AddStatic(new TriangleMeshShape(Floor, Indices), Pose.Identity);
            rampHandle = world.AddStatic(new TriangleMeshShape(Ramp(rampFarY), Indices), Pose.Identity);
        }
        else
        {
            rampHandle = world.AddStatic(new TriangleMeshShape(Ramp(rampFarY), Indices), Pose.Identity);
            floorHandle = world.AddStatic(new TriangleMeshShape(Floor, Indices), Pose.Identity);
        }

        Vector3 direction = -Vector3.UnitY;
        const float distance = 0.8f;

        // Isolated, each static is reported on its own.
        using (IPhysicsWorldQueryView floorOnly = world.CreateQueryViewExcludingStatics([rampHandle]))
        {
            Assert.True(floorOnly.SweepCapsule(Capsule, Start, direction, distance, out SweepHit hit, QueryFilter.StaticsOnly));
            Assert.Equal(floorHandle, hit.Body);
            Assert.InRange(hit.Distance, FloorT - Ulp, FloorT + ConvergenceT + Ulp);
        }
        using (IPhysicsWorldQueryView rampOnly = world.CreateQueryViewExcludingStatics([floorHandle]))
        {
            Assert.True(rampOnly.SweepCapsule(Capsule, Start, direction, distance, out SweepHit hit, QueryFilter.StaticsOnly));
            Assert.Equal(rampHandle, hit.Body);
            Assert.True(hit.Distance > FloorT + 0.02f, $"ramp t {hit.Distance}");
        }

        // Combined, the floor is nearer.
        Assert.True(world.SweepCapsule(Capsule, Start, direction, distance, out SweepHit combined, QueryFilter.StaticsOnly));
        Assert.Equal(floorHandle, combined.Body);
        Assert.InRange(combined.Distance, FloorT - Ulp, FloorT + ConvergenceT + Ulp);
    }

    // The same Bepu child search reaches only maxDistance squared, so a lone mesh swept less than 1 is missed
    // between that reach and the requested distance. Here the floor is 0.05 below and the sweep is 0.1.
    [Fact]
    public void ShortCapsuleSweepReachesALoneMeshWithinItsDistance()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floorHandle = world.AddStatic(new TriangleMeshShape(Floor, Indices), Pose.Identity);
        Pose start = Pose.At(new Vector3(-0.1f, 0.255f, 0)); // lowest point at Y 0.05

        Assert.True(world.SweepCapsule(Capsule, start, -Vector3.UnitY, 0.1f, out SweepHit hit, QueryFilter.StaticsOnly));
        Assert.Equal(floorHandle, hit.Body);
        Assert.InRange(hit.Distance, 0.05f - Ulp, 0.05f + ConvergenceT + Ulp);
    }
}
