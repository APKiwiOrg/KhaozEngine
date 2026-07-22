using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PhysicsColumnProbeTests
{
    // A fake IPhysicsWorld backed by a scripted list of horizontal planes (Y plus normal), all sharing
    // one XZ column since every test here samples a single point. Only Raycast is exercised by
    // PhysicsColumnProbe. Every other member is never called by it, so each throws NotSupportedException
    // to fail loudly if that assumption ever changes.
    sealed class ScriptedColumnWorld : IPhysicsWorld
    {
        readonly IReadOnlyList<(float Y, Vector3 Normal)> _planes;
        public ScriptedColumnWorld(IReadOnlyList<(float Y, Vector3 Normal)> planes) => _planes = planes;

        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default)
        {
            // Returns the highest scripted plane strictly below origin.Y and within maxDistance,
            // matching a downward sweep's next hit.
            bool found = false;
            float bestY = float.NegativeInfinity;
            Vector3 bestNormal = default;

            foreach ((float y, Vector3 normal) in _planes)
            {
                if (y >= origin.Y) continue;
                float distance = origin.Y - y;
                if (distance > maxDistance) continue;
                if (!found || y > bestY)
                {
                    found = true;
                    bestY = y;
                    bestNormal = normal;
                }
            }

            if (!found)
            {
                hit = default;
                return false;
            }

            hit = new RayHit(origin.Y - bestY, new Vector3(origin.X, bestY, origin.Z), bestNormal, default);
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

    static readonly Vector3 Up = Vector3.UnitY;
    static readonly Vector3 Down = -Vector3.UnitY;

    [Fact]
    public void BridgeColumn_TwoSurfacesAscending_CorrectHeadroom()
    {
        // Deck top at 2.0 (open sky above), its underside at 1.8 (a ceiling, not standable itself since
        // it faces down), ground at 0 (headroom to the underside above it).
        var world = new ScriptedColumnWorld(new (float, Vector3)[]
        {
            (2.0f, Up),
            (1.8f, Down),
            (0f, Up),
        });
        var probe = new PhysicsColumnProbe(world);

        Span<ColumnSurface> surfaces = stackalloc ColumnSurface[4];
        int count = probe.Sample(0f, 0f, surfaces);

        Assert.Equal(2, count);
        Assert.Equal(0f, surfaces[0].Height, 3);
        Assert.Equal(1.8f, surfaces[0].Headroom, 3);
        Assert.Equal(2.0f, surfaces[1].Height, 3);
        Assert.True(float.IsPositiveInfinity(surfaces[1].Headroom));
    }

    [Fact]
    public void SteepFace_NotStandable_StillActsAsCeiling()
    {
        // A face 60 degrees off vertical (Y component cos(60) = 0.5) fails the default 50-degree gate
        // (cos(50) ~ 0.643), so it must not appear as its own surface, but it still bounds the headroom
        // of the standable ground beneath it.
        float angle = 60f * MathF.PI / 180f;
        var steepNormal = new Vector3(MathF.Sin(angle), MathF.Cos(angle), 0f);

        var world = new ScriptedColumnWorld(new (float, Vector3)[]
        {
            (3.0f, steepNormal),
            (0f, Up),
        });
        var probe = new PhysicsColumnProbe(world);

        Span<ColumnSurface> surfaces = stackalloc ColumnSurface[4];
        int count = probe.Sample(0f, 0f, surfaces);

        Assert.Equal(1, count);
        Assert.Equal(0f, surfaces[0].Height, 3);
        Assert.Equal(3.0f, surfaces[0].Headroom, 3);
    }

    [Fact]
    public void Overflow_KeepsLowestSurfaces_DropsHighest()
    {
        var world = new ScriptedColumnWorld(new (float, Vector3)[]
        {
            (0f, Up),
            (1f, Up),
            (3f, Up),
        });
        var probe = new PhysicsColumnProbe(world);

        Span<ColumnSurface> surfaces = stackalloc ColumnSurface[2];
        int count = probe.Sample(0f, 0f, surfaces);

        Assert.Equal(2, count);
        Assert.Equal(0f, surfaces[0].Height, 3);
        Assert.Equal(1f, surfaces[0].Headroom, 3);
        Assert.Equal(1f, surfaces[1].Height, 3);
        Assert.Equal(2f, surfaces[1].Headroom, 3);
    }

    [Fact]
    public void EmptyBuffer_ReturnsZero()
    {
        var world = new ScriptedColumnWorld(new (float, Vector3)[] { (0f, Up) });
        var probe = new PhysicsColumnProbe(world);

        int count = probe.Sample(0f, 0f, Span<ColumnSurface>.Empty);

        Assert.Equal(0, count);
    }

    [Fact]
    public void NoHits_ReturnsZero()
    {
        var world = new ScriptedColumnWorld(Array.Empty<(float, Vector3)>());
        var probe = new PhysicsColumnProbe(world);

        Span<ColumnSurface> surfaces = stackalloc ColumnSurface[4];
        int count = probe.Sample(0f, 0f, surfaces);

        Assert.Equal(0, count);
    }

    [Fact]
    public void NullWorld_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PhysicsColumnProbe(null!));
    }
}
