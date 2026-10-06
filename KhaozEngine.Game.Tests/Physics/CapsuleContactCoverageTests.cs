using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class CapsuleContactCoverageTests
{
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);
    static readonly Pose AtBed = Pose.At(new Vector3(0f, 0.75f, 0f));
    static readonly BoxShape Floor = new(new Vector3(4f, 0.25f, 4f));
    static readonly BoxShape Wall = new(new Vector3(0.1f, 2f, 4f));

    [Fact]
    public void ADeepBedContactCannotHideAShallowerSideWall()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle bed = world.AddStatic(Floor, Pose.At(new Vector3(0f, 0.05f, 0f)));
        StaticHandle wall = world.AddStatic(Wall, Pose.At(new Vector3(0.3f, 1f, 0f)));
        Captured result = Query(world, Capsule, AtBed);
        Complete(result);
        Assert.Contains(result.Contacts, c => c.BodyHandle == bed.Value && c.Normal.Y > 0.99f &&
            MathF.Abs(c.Separation + 0.3f) <= result.Error);
        Assert.Contains(result.Contacts, c => c.BodyHandle == wall.Value && c.Normal.X < -0.99f &&
            MathF.Abs(c.Separation + 0.1f) <= result.Error);
    }

    [Fact]
    public void LowCeilingAndPostBothConstrainTheCapsule()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Floor, Pose.At(new Vector3(0f, 1.65f, 0f)));
        world.AddStatic(Wall, Pose.At(new Vector3(0.35f, 1f, 0f)));
        Captured result = Query(world, Capsule, AtBed);
        Complete(result);
        Assert.Contains(result.Contacts, c => c.Normal.Y < -0.99f);
        Assert.Contains(result.Contacts, c => c.Normal.X < -0.99f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothThinWallConstraintsAreCollectedAtTheFirstSweptImpact(bool reverse)
    {
        using var world = Corner(reverse);
        Vector3 start = new(-0.5f, 1f, -0.5f);
        Vector3 direction = Vector3.Normalize(new Vector3(1f, 0f, 1f));
        Assert.True(world.SweepCapsule(Capsule, Pose.At(start), direction, 2f, out SweepHit hit));
        Captured result = Query(world, Capsule, Pose.At(start + direction * hit.Distance));
        Complete(result);
        Assert.Contains(result.Contacts, c => c.Normal.X < -0.99f);
        Assert.Contains(result.Contacts, c => c.Normal.Z < -0.99f);
        Assert.False(world.ComputePenetration(Capsule, Pose.At(start + direction * 2f), out _));
    }

    [Fact]
    public void GeometryInsertionOrderDoesNotChangeTheConstraintSet()
    {
        using var a = Corner(false);
        using var b = Corner(true);
        Captured first = Query(a, Capsule, Pose.At(new Vector3(0f, 1f, 0f)));
        Captured second = Query(b, Capsule, Pose.At(new Vector3(0f, 1f, 0f)));
        Complete(first);
        Complete(second);
        Assert.Equal(Normals(first), Normals(second));
        Assert.Equal(new[] { -Vector3.UnitX, -Vector3.UnitZ }, Normals(first));
    }

    [Theory]
    [InlineData(1.002f, true)]
    [InlineData(1.0045f, false)]
    public void RequestedSeparationMarginIncludesNearContactButNotDistantGeometry(float centreX, bool included)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new Vector3(0.5f, 2f, 2f)), Pose.At(new Vector3(centreX, 1f, 0f)));
        Captured result = Query(world, new CapsuleShape(0.5f, 1f), Pose.At(new Vector3(0f, 1f, 0f)));
        Complete(result);
        if (included)
        {
            Assert.Contains(result.Contacts, c => c.Normal.X < -0.99f && c.Separation > 0f);
            Assert.All(result.Contacts, c => Assert.InRange(c.Separation, 0.0019f, 0.0021f));
        }
        else Assert.Empty(result.Contacts);
    }

    [Fact]
    public void CompoundChildrenRetainBothOwnersOfDistinctConstraints()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(Floor, Pose.At(new Vector3(0f, 0.05f, 0f))),
            new CompoundChild(Wall, Pose.At(new Vector3(0.3f, 1f, 0f))),
        });
        StaticHandle owner = world.AddStatic(compound, Pose.Identity);
        Captured result = Query(world, Capsule, AtBed);
        Complete(result);
        Assert.All(result.Contacts, c => Assert.Equal(owner.Value, c.BodyHandle));
        Assert.Contains(result.Contacts, c => c.ChildIndex == 0 && c.Normal.Y > 0.99f);
        Assert.Contains(result.Contacts, c => c.ChildIndex == 1 && c.Normal.X < -0.99f);
    }

    [Fact]
    public void CompoundChildSelectionIncludesThePositiveSeparationMargin()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(new BoxShape(new Vector3(0.1f, 2f, 4f)),
                Pose.At(new Vector3(0.401f, 1f, 0f))),
        });
        world.AddStatic(compound, Pose.Identity);
        Captured result = Query(world, Capsule, Pose.At(new Vector3(0f, 1f, 0f)));
        Complete(result);
        Assert.Contains(result.Contacts, c => c.ChildIndex == 0 && c.Normal.X < -0.99f &&
            MathF.Abs(c.Separation - 0.001f) <= result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void InsufficientCapacityExposesNoUsablePrefix(int capacity)
    {
        using var world = Corner(false);
        Captured result = Query(world, Capsule, Pose.At(new Vector3(0f, 1f, 0f)), capacity: capacity);
        Assert.False(result.Complete);
        Assert.Equal(0, result.Written);
        Assert.True(result.RequiredCapacity >= 2);
        Assert.Empty(result.Contacts);
        Assert.True(result.DestinationUntouched);
    }

    [Fact]
    public void RestrictedViewsExcludeContactsBeforeCompletenessIsReported()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle bed = world.AddStatic(Floor, Pose.At(new Vector3(0f, 0.05f, 0f)));
        StaticHandle wall = world.AddStatic(Wall, Pose.At(new Vector3(0.3f, 1f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics(new[] { bed });
        Captured result = Query(view, Capsule, AtBed);
        Complete(result);
        Assert.NotEmpty(result.Contacts);
        Assert.All(result.Contacts, c => Assert.Equal(wall.Value, c.BodyHandle));
        Assert.DoesNotContain(result.Contacts, c => c.Normal.Y > 0.99f);
    }

    [Fact]
    public void MobilityFiltersPreserveDynamicAndStaticProvenance()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle solid = world.AddStatic(Wall, Pose.At(new Vector3(0.3f, 1f, 0f)));
        DynamicBodyHandle moving = world.AddDynamic(Wall, Pose.At(new Vector3(-0.3f, 1f, 0f)),
            new DynamicBodyDescription(1f));
        Captured statics = Query(world, Capsule, AtBed, filter: QueryFilter.StaticsOnly);
        Captured dynamics = Query(world, Capsule, AtBed, filter: QueryFilter.DynamicsOnly);
        Complete(statics);
        Complete(dynamics);
        Assert.NotEmpty(statics.Contacts);
        Assert.NotEmpty(dynamics.Contacts);
        Assert.All(statics.Contacts, c => { Assert.False(c.Dynamic); Assert.Equal(solid.Value, c.BodyHandle); });
        Assert.All(dynamics.Contacts, c => { Assert.True(c.Dynamic); Assert.Equal(moving.Value, c.BodyHandle); });
        Assert.Contains(statics.Contacts, c => c.Normal.X < -0.99f);
        Assert.Contains(dynamics.Contacts, c => c.Normal.X > 0.99f);
    }

    [Fact]
    public void AContactQueryInsideTheReadLeaseDoesNotMutateTheWorld()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Wall, Pose.At(new Vector3(0.3f, 1f, 0f)));
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)world).AcquireQueryReadLease();
        long generation = lease.GeometryGeneration;
        Complete(Query(world, Capsule, AtBed));
        lease.AssertCurrent();
        Assert.Equal(generation, lease.GeometryGeneration);
        Captured clear = Query(world, Capsule, Pose.At(new Vector3(10f, 10f, 10f)));
        Complete(clear);
        Assert.Empty(clear.Contacts);
        Assert.Equal(0, clear.RequiredCapacity);
    }

    [Fact]
    public void ConvexHullFaceMatchesItsAnalyticWallPlane()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Vector3[] vertices =
        [
            new(-0.1f, -2f, -4f), new(0.1f, -2f, -4f), new(-0.1f, 2f, -4f), new(0.1f, 2f, -4f),
            new(-0.1f, -2f, 4f), new(0.1f, -2f, 4f), new(-0.1f, 2f, 4f), new(0.1f, 2f, 4f),
        ];
        world.AddStatic(new ConvexHullShape(vertices), Pose.At(new Vector3(0.3f, 1f, 0f)));
        Captured result = Query(world, Capsule, AtBed);
        Complete(result);
        Assert.Contains(result.Contacts, c => c.Normal.X < -0.99f &&
            MathF.Abs(c.Separation + 0.1f) <= result.Error);
    }

    [Fact]
    public void FlatMeshSeamDoesNotInventALateralObstacle()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Vector3[] vertices = [new(-4f, 0f, -4f), new(-4f, 0f, 4f), new(4f, 0f, 4f), new(4f, 0f, -4f)];
        // Direct physics meshes use Bepu's clockwise front, the same reversal TerrainChunkCollision applies.
        world.AddStatic(new TriangleMeshShape(vertices, [0, 2, 1, 0, 3, 2]), Pose.Identity);
        Assert.True(world.Raycast(new Vector3(0.02f, 2f, 0f), -Vector3.UnitY, 3f, out RayHit floor));
        Assert.True(floor.Normal.Y > 0.99f);
        Captured result = Query(world, Capsule, Pose.At(new Vector3(0.02f, 0.751f, 0f)));
        Complete(result);
        Assert.NotEmpty(result.Contacts);
        Assert.All(result.Contacts, c => Assert.True(c.Normal.Y > 0.9999f,
            $"Coplanar triangles introduced lateral constraint {c.Normal}."));
    }

    [Fact]
    public void UnsupportedLayerMaskCannotSilentlyClaimCompleteFiltering()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Assert.Throws<NotSupportedException>(() =>
            Query(world, Capsule, AtBed, filter: new QueryFilter(QueryMobility.All, 1u)));
    }

    [Theory]
    [InlineData(-640f)]
    [InlineData(640f)]
    public void BoxSeparationStaysWithinTheBudgetAtBothVerticalQueryEdges(float y)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Wall, Pose.At(new Vector3(0.3f, y, 0f)));
        Captured result = Query(world, Capsule, Pose.At(new Vector3(0f, y, 0f)));
        Complete(result);
        Assert.Contains(result.Contacts, c => c.Normal.X < -0.99f &&
            MathF.Abs(c.Separation + 0.1f) <= result.Error);
    }

    [Theory]
    [InlineData(0.5f, -0.1f)]
    [InlineData(0.602f, 0.002f)]
    public void CapsulePairMatchesAnalyticSeparation(float x, float separation)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Capsule, Pose.At(new Vector3(x, 1f, 0f)));
        Captured result = Query(world, Capsule, Pose.At(new Vector3(0f, 1f, 0f)));
        Complete(result);
        Assert.Contains(result.Contacts, c => c.Normal.X < -0.99f &&
            MathF.Abs(c.Separation - separation) <= result.Error);
    }

    [Fact]
    public void ExcessiveCoordinateErrorRefusesWithoutTouchingDestination()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Captured result = Query(world, Capsule, Pose.At(new Vector3(100000f, 1f, 0f)));
        Assert.False(result.Complete);
        Assert.Equal(0, result.Written);
        Assert.True(result.DestinationUntouched);
    }

    [Fact]
    public void MeshBeyondTheSmoothingLimitRefusesWithoutAPrefix()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Vector3[] vertices = [new(-1f, 0f, -1f), new(1f, 0f, 1f), new(-1f, 0f, 1f)];
        var indices = new int[1025 * 3];
        for (int i = 0; i < 1025; i++)
        {
            indices[i * 3] = 0;
            indices[i * 3 + 1] = 1;
            indices[i * 3 + 2] = 2;
        }
        world.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity);
        Captured result = Query(world, Capsule, Pose.At(new Vector3(0f, 0.75f, 0f)));
        Assert.False(result.Complete);
        Assert.Equal(0, result.Written);
        Assert.True(result.DestinationUntouched);
    }

    static BepuPhysicsWorld Corner(bool reverse)
    {
        var world = new BepuPhysicsWorld(Vector3.Zero);
        var x = new BoxShape(new Vector3(0.01f, 2f, 2f));
        var z = new BoxShape(new Vector3(2f, 2f, 0.01f));
        if (reverse)
        {
            world.AddStatic(z, Pose.At(new Vector3(0f, 1f, 0.311f)));
            world.AddStatic(x, Pose.At(new Vector3(0.311f, 1f, 0f)));
        }
        else
        {
            world.AddStatic(x, Pose.At(new Vector3(0.311f, 1f, 0f)));
            world.AddStatic(z, Pose.At(new Vector3(0f, 1f, 0.311f)));
        }
        return world;
    }

    static void Complete(Captured result)
    {
        Assert.True(result.Complete);
        Assert.Equal(result.Written, result.Contacts.Length);
        Assert.True(float.IsFinite(result.Error));
        Assert.InRange(result.Error, 0f, 0.001f);
        Assert.All(result.Contacts, c =>
        {
            Assert.True(float.IsFinite(c.Separation));
            Assert.InRange(c.Normal.Length(), 0.999f, 1.001f);
        });
    }

    static Vector3[] Normals(Captured result) => result.Contacts.Select(c =>
        new Vector3(MathF.Round(c.Normal.X, 3), MathF.Round(c.Normal.Y, 3), MathF.Round(c.Normal.Z, 3)))
        .Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ThenBy(v => v.Z).ToArray();

    sealed record Captured(bool Complete, int Written, int RequiredCapacity, float Error,
        CapsuleContact[] Contacts, bool DestinationUntouched);

    static Captured Query(IPhysicsWorld world, CapsuleShape capsule, Pose pose,
        float margin = 0.002f, int capacity = 32, QueryFilter filter = default)
    {
        var query = Assert.IsAssignableFrom<IPhysicsCapsuleContacts>(world);
        var destination = new CapsuleContact[capacity];
        var sentinel = new CapsuleContact(Vector3.UnitY, 7f, false, 37, 0, 0);
        Array.Fill(destination, sentinel);
        CapsuleContactResult result = query.QueryCapsuleContacts(capsule, pose, margin, destination, filter);
        return new Captured(result.Complete, result.Written, result.RequiredCapacity,
            result.CertifiedErrorMetres, destination[..result.Written],
            destination.All(value => value == sentinel));
    }
}
