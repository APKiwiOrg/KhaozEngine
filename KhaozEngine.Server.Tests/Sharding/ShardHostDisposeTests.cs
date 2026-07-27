using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// <see cref="ShardHost.Dispose"/> disposes every live cell's physics world. Before this existed,
/// <c>CellSim.Retire</c> (the only <c>Physics?.Dispose()</c> call site) was reached only through eviction
/// (<see cref="ShardHost.RemoveCell"/>); a cell still live when the host itself went away - the ordinary shutdown
/// case, and any test that never evicts - leaked its physics world.
/// </summary>
public class ShardHostDisposeTests
{
    // Counts Dispose calls per instance, so a test can assert both "did it run" and "did it run exactly once" (a
    // double-dispose bug would double-free a backend that is not idempotent about it).
    private sealed class CountingFakePhysicsWorld : IPhysicsWorld
    {
        public int DisposeCount { get; private set; }
        public Vector3 Origin { get; init; }
        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => default;
        public void RemoveStatic(StaticHandle handle) { }
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) => default;
        public void RemoveDynamic(DynamicBodyHandle handle) { }
        public Pose GetDynamicPose(DynamicBodyHandle handle) => Pose.Identity;
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) { linear = default; angular = default; }
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) { }
        public bool IsAwake(DynamicBodyHandle handle) => false;
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => default;
        public void RemoveConstraint(ConstraintHandle handle) { }
        public void SetConstraintTarget(ConstraintHandle handle, float target) { }
        public void Step(float dt) { }
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default) { hit = default; return false; }
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) { hit = default; return false; }
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) { mtv = default; return false; }
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void Dispose_DisposesEveryLiveCellsPhysicsWorld_ExactlyOnce()
    {
        var built = new List<CountingFakePhysicsWorld>();
        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, registry: new ReplicationRegistry(),
            interestCellSize: 100f, overlapMargin: 0f,
            physicsFactory: _ =>
            {
                var world = new CountingFakePhysicsWorld { Origin = Vector3.Zero };
                built.Add(world);
                return world;
            },
            frameAnchoring: false);   // Origin 0 is the correct answer everywhere with anchoring off

        host.EnsureCell(new CellCoord(0, 0));
        host.EnsureCell(new CellCoord(1, 0));
        host.EnsureCell(new CellCoord(0, 1));

        Assert.Equal(3, built.Count);
        Assert.All(built, w => Assert.Equal(0, w.DisposeCount));

        host.Dispose();

        Assert.All(built, w => Assert.Equal(1, w.DisposeCount));

        // Idempotent: a second Dispose (e.g. a "using" plus an explicit earlier call) must not double-dispose
        // anything already retired.
        host.Dispose();
        Assert.All(built, w => Assert.Equal(1, w.DisposeCount));
    }

    [Fact]
    public void Dispose_LeavesNoLiveCells()
    {
        var host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, registry: new ReplicationRegistry(),
            interestCellSize: 100f, overlapMargin: 0f,
            physicsFactory: _ => new CountingFakePhysicsWorld(), frameAnchoring: false);

        host.EnsureCell(new CellCoord(0, 0));
        host.EnsureCell(new CellCoord(5, 5));
        Assert.Equal(2, host.CellCount);

        host.Dispose();

        Assert.Equal(0, host.CellCount);
        Assert.Empty(host.Cells);
    }
}
