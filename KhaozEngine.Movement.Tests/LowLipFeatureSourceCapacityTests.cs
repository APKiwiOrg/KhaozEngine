using System.Numerics;
using System.Reflection;
using BepuMesh = BepuPhysics.Collidables.Mesh;
using BepuSim = BepuPhysics.Simulation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Movement.CornerFeatureOracle;

namespace KhaozEngine.Tests.Movement;

public class LowLipFeatureSourceCapacityTests
{
    [Fact]
    public void ActualSixtyFiveThousandFiveHundredThirtySeventhSourceTriangleRefusesAtomically()
    {
        // One fixed functional fixture at the first refused source count, not a throughput or stress test.
        // Every triangle is an upward right triangle in its own 1/8 m cell, with side 1/16 m.
        // Distinct cells have disjoint closed triangle bounds separated by at least 1/16 m.
        const int triangleCount = 65537, columns = 257;
        const float spacing = 1f / 8, side = 1f / 16;
        var vertices = new Vector3[triangleCount * 3];
        var indices = new int[vertices.Length];
        for (int i = 0; i < triangleCount; i++)
        {
            // All integer products and sums below are exactly representable binary32 dyadics.
            float x = (i % columns) * spacing, z = (i / columns) * spacing;
            int first = i * 3;
            vertices[first] = new(x, 0, z);
            vertices[first + 1] = new(x + side, 0, z);
            vertices[first + 2] = new(x, 0, z + side);
            indices[first] = first;
            indices[first + 1] = first + 1;
            indices[first + 2] = first + 2;
        }
        Assert.True(spacing > side && side > 0);
        Assert.InRange((columns - 1) * spacing + side, 0f, 64f);
        Assert.InRange(((triangleCount - 1) / columns) * spacing + side, 0f, 64f);
        Assert.Equal(new R(1, 256), R.From(side) * R.From(side));

        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle installed = world.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        var features = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(view);
        using IPhysicsQueryLease lease = Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(view).AcquireQueryReadLease();
        lease.AssertCurrent();
        Assert.Same(world, lease.SourceWorld);
        Assert.Same(world, view.SourceWorld);
        Assert.Equal(Vector3.Zero, lease.Origin);
        Assert.Equal(Vector3.Zero, view.Origin);

        // Owned test reflection inspects the real installed allocation and real tree, without modifying either.
        FieldInfo? simulationField = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo? generationField = typeof(BepuPhysicsWorld).GetField("_queryGeneration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(simulationField);
        Assert.NotNull(generationField);
        BepuSim simulation = Assert.IsType<BepuSim>(simulationField.GetValue(world));
        Assert.Equal(1, simulation.Statics.Count);
        ref var body = ref simulation.Statics[0];
        Assert.Equal(default(BepuMesh).TypeId, body.Shape.Type);
        Assert.Equal(Vector3.Zero, body.Pose.Position);
        Assert.Equal(Quaternion.Identity, body.Pose.Orientation);
        ref BepuMesh mesh = ref simulation.Shapes.GetShape<BepuMesh>(body.Shape.Index);
        Assert.Equal(Vector3.One, mesh.Scale);
        Assert.Equal(triangleCount, mesh.Triangles.Length);
        Assert.Equal(triangleCount, mesh.Tree.LeafCount);
        Assert.Equal(vertices[0], mesh.Triangles[0].A);
        Assert.Equal(vertices[1], mesh.Triangles[0].B);
        Assert.Equal(vertices[2], mesh.Triangles[0].C);
        int last = (triangleCount - 1) * 3;
        Assert.Equal(vertices[last], mesh.Triangles[triangleCount - 1].A);
        Assert.Equal(vertices[last + 1], mesh.Triangles[triangleCount - 1].B);
        Assert.Equal(vertices[last + 2], mesh.Triangles[triangleCount - 1].C);
        long generation = Assert.IsType<long>(generationField.GetValue(world));
        Assert.Equal(generation, lease.GeometryGeneration);

        // One ordinary ray in the strict interior of the first triangle validates usable front geometry.
        // It does not provide the expected status or any feature witness.
        Vector3 interior = new(side / 4, 0, side / 4);
        Assert.True(R.From(interior.X) > R.Zero && R.From(interior.Z) > R.Zero);
        Assert.True(R.From(interior.X) + R.From(interior.Z) < R.From(side));
        Assert.True(view.Raycast(interior + Vector3.UnitY, -Vector3.UnitY, 2,
            out RayHit hit, QueryFilter.StaticsOnly));
        Assert.Equal(installed, hit.Body);
        AssertVectorWithin(hit.Point, Point(interior), PositionCeiling, "ordinary installed interior");
        AssertVectorWithin(hit.Normal, V(0, 1, 0), NormalCeiling, "ordinary upward front");
        Assert.True(hit.Body.HasValue);
        StaticHandle target = hit.Body!.Value;
        var capsule = new CapsuleShape(0.25f, 0.5f);
        Pose candidate = Pose.At(interior + Vector3.UnitY * 0.5f);
        ExactVector delta = AxisLower(capsule, candidate) - Point(interior);
        Assert.Equal(R.From(capsule.Radius) * R.From(capsule.Radius), delta.LengthSquared());

        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        // Exactly one feature query. Capture must refuse the actual source count before enumerating geometry.
        CapsuleFeatureResult result = features.QueryCapsuleFeature(lease, target, capsule, candidate,
            0, faces, QueryFilter.StaticsOnly);
        AssertRefused(result, faces, original, CapsuleFeatureStatus.CapacityExceeded);
        Assert.Equal(generation, Assert.IsType<long>(generationField.GetValue(world)));
        Assert.Equal(generation, lease.GeometryGeneration);
        Assert.Equal(triangleCount, mesh.Triangles.Length);
        Assert.Equal(triangleCount, mesh.Tree.LeafCount);
        lease.AssertCurrent();
    }
}
