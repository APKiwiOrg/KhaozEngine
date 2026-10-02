using System;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PhysicsColumnProbeProgressTests
{
    [Theory]
    [InlineData(1000000f)]
    [InlineData(-1000000f)]
    public void LargeCoordinateSolid_KeepsRealSurfacesAndUndersideHeadroom(float top)
    {
        using var world = new BoundedColumnWorld(top);
        var probe = new PhysicsColumnProbe(world) { ProbeHeight = top + 1f, ProbeRange = 2f };
        Span<ColumnSurface> surfaces = stackalloc ColumnSurface[2];

        int count = probe.Sample(0f, 0f, surfaces);

        Assert.Equal(2, count);
        Assert.Equal(top - 0.5f, surfaces[0].Height);
        Assert.Equal(0.25f, surfaces[0].Headroom);
        Assert.Equal(top, surfaces[1].Height);
        Assert.True(float.IsPositiveInfinity(surfaces[1].Headroom));
    }

    [Fact]
    public void InsideSolid_RangeSmallerThanRepresentableDescent_StopsWithoutPhantomSurface()
    {
        using var world = new BoundedColumnWorld(1000000f);
        var probe = new PhysicsColumnProbe(world) { ProbeHeight = 1000000f, ProbeRange = 0.03125f };
        Span<ColumnSurface> surfaces = stackalloc ColumnSurface[1];

        Assert.Equal(0, probe.Sample(0f, 0f, surfaces));
    }

    [Fact]
    public void InsideSolid_NoFiniteLowerOrigin_StopsWithoutPhantomSurface()
    {
        using var world = new BoundedColumnWorld(float.MinValue);
        var probe = new PhysicsColumnProbe(world) { ProbeHeight = float.MinValue, ProbeRange = 1f };
        Span<ColumnSurface> surfaces = stackalloc ColumnSurface[1];

        Assert.Equal(0, probe.Sample(0f, 0f, surfaces));
    }

    [Fact]
    public void InsideSolid_UnrepresentableRangeReduction_StopsWithoutPhantomSurface()
    {
        using var world = new BoundedColumnWorld(1000000f);
        var probe = new PhysicsColumnProbe(world) { ProbeHeight = 1000000f, ProbeRange = float.MaxValue };
        Span<ColumnSurface> surfaces = stackalloc ColumnSurface[1];

        Assert.Equal(0, probe.Sample(0f, 0f, surfaces));
    }

    // A solid slab above a horizontal floor. The slab returns valid zero-distance origin hits
    // throughout its interior, matching the convex backend contract. The ray cap makes the old
    // non-progressing sweep fail synchronously instead of hanging the test process.
    sealed class BoundedColumnWorld(float top) : IPhysicsWorld
    {
        int _rayCalls;

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default)
        {
            if (++_rayCalls > 32)
                throw new InvalidOperationException("Column sweep exceeded the bounded ray budget.");

            if (origin.Y <= top && origin.Y >= top - 0.25f)
            {
                hit = new RayHit(0f, origin, Vector3.UnitY, new StaticHandle(1));
                return true;
            }

            float surfaceY = origin.Y > top ? top : top - 0.5f;
            float distance = origin.Y - surfaceY;
            if (distance < 0f || distance > maxDistance)
            {
                hit = default;
                return false;
            }

            hit = new RayHit(distance, new Vector3(origin.X, surfaceY, origin.Z), Vector3.UnitY, new StaticHandle(1));
            return true;
        }

        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => throw new NotSupportedException();
        public void RemoveStatic(StaticHandle handle) => throw new NotSupportedException();
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) => throw new NotSupportedException();
        public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
        public Pose GetDynamicPose(DynamicBodyHandle handle) => throw new NotSupportedException();
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) => throw new NotSupportedException();
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) => throw new NotSupportedException();
        public bool IsAwake(DynamicBodyHandle handle) => throw new NotSupportedException();
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();
        public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
        public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
        public void Step(float dt) => throw new NotSupportedException();
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) => throw new NotSupportedException();
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
