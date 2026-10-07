using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuCompound = BepuPhysics.Collidables.Compound;
using BepuHull = BepuPhysics.Collidables.ConvexHull;
using BepuSim = BepuPhysics.Simulation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Expectations are independently derived finite geometry, not backend query output.
public class CapsuleFeaturePolyhedronTests
{
    static readonly Rational PositionCeiling = new(1, 4000);
    static readonly Rational SeparationWidthCeiling = new(1, 10000);
    static readonly Rational NormalCeiling = new(1, 100000);

    [Fact]
    public void BoxTopFaceReturnsTheIsolatedLowerAxisEndpoint()
    {
        using var scene = new Scene(UnitBox(), Pose.Identity,
            new Vector3(0, 2, 0), -Vector3.UnitY, Point(0, 0.5, 0), Direction(0, 1, 0));

        AssertContact(scene, new CapsuleShape(0.25f, 1), Pose.At(new Vector3(0, 1.25f, 0)),
            CapsuleFeatureKind.FaceInterior, Point(0, 0.75, 0), Point(0, 0.5, 0), Direction(0, 1, 0),
            [Direction(0, 1, 0)]);
    }

    [Fact]
    public void BoxTopWestEdgeReturnsBothFiniteIncidentFaces()
    {
        using var scene = new Scene(UnitBox(), Pose.Identity,
            new Vector3(0, 2, 0), -Vector3.UnitY, Point(0, 0.5, 0), Direction(0, 1, 0));
        scene.CheckTargetRay(new Vector3(-2, 0, 0), Vector3.UnitX,
            Point(-0.5, 0, 0), Direction(-1, 0, 0));

        // Delta = (-3/8, 1/2, 0), distance = 5/8, direction = (-3/5, 4/5, 0).
        AssertContact(scene, new CapsuleShape(0.625f, 1), Pose.At(new Vector3(-0.875f, 1.5f, 0)),
            CapsuleFeatureKind.ConvexCrease, Point(-0.875, 1, 0), Point(-0.5, 0.5, 0), Direction(-3, 4, 0, 5),
            [Direction(0, 1, 0), Direction(-1, 0, 0)]);
    }

    [Fact]
    public void BoxTopWestNorthVertexReturnsAllThreeIncidentFaces()
    {
        using var scene = new Scene(UnitBox(), Pose.Identity,
            new Vector3(0, 2, 0), -Vector3.UnitY, Point(0, 0.5, 0), Direction(0, 1, 0));
        scene.CheckTargetRay(new Vector3(-2, 0, 0), Vector3.UnitX,
            Point(-0.5, 0, 0), Direction(-1, 0, 0));
        scene.CheckTargetRay(new Vector3(0, 0, -2), Vector3.UnitZ,
            Point(0, 0, -0.5), Direction(0, 0, -1));

        // Delta = (-1/4, 1/2, -1/2), distance = 3/4, direction = (-1/3, 2/3, -2/3).
        AssertContact(scene, new CapsuleShape(0.75f, 1), Pose.At(new Vector3(-0.75f, 1.5f, -1)),
            CapsuleFeatureKind.Vertex, Point(-0.75, 1, -1), Point(-0.5, 0.5, -0.5), Direction(-1, 2, -2, 3),
            [Direction(0, 1, 0), Direction(-1, 0, 0), Direction(0, 0, -1)]);
    }

    [Fact]
    public void TranslatedBoxUsesInstalledTargetPositionInTheQueryFrame()
    {
        using var scene = new Scene(UnitBox(), Pose.At(new Vector3(54, 1.5f, -98)),
            new Vector3(54, 4, -98), -Vector3.UnitY, Point(54, 2, -98), Direction(0, 1, 0));

        AssertContact(scene, new CapsuleShape(0.25f, 1), Pose.At(new Vector3(54, 2.75f, -98)),
            CapsuleFeatureKind.FaceInterior, Point(54, 2.25, -98), Point(54, 2, -98), Direction(0, 1, 0),
            [Direction(0, 1, 0)]);
    }

    [Fact]
    public void ExactQuaternionCycleRotatesTheAsymmetricBoxExtents()
    {
        Quaternion cycle = new(0.5f, 0.5f, 0.5f, 0.5f);
        Rational x = Rational.From(cycle.X), y = Rational.From(cycle.Y);
        Rational z = Rational.From(cycle.Z), w = Rational.From(cycle.W);
        Assert.Equal(new Rational(1, 1), x * x + y * y + z * z + w * w);
        using var scene = new Scene(new BoxShape(new Vector3(1, 0.5f, 0.25f)),
            new Pose(Vector3.Zero, cycle), new Vector3(0, 2, 0), -Vector3.UnitY,
            Point(0, 1, 0), Direction(0, 1, 0));
        scene.CheckTargetRay(new Vector3(2, 0, 0), -Vector3.UnitX,
            Point(0.25, 0, 0), Direction(1, 0, 0));
        scene.CheckTargetRay(new Vector3(0, 0, 2), -Vector3.UnitZ,
            Point(0, 0, 0.5), Direction(0, 0, 1));

        // The exact rotation sends (x, y, z) to (z, x, y), so world half-extents are (1/4, 1, 1/2).
        AssertContact(scene, new CapsuleShape(0.25f, 1), Pose.At(new Vector3(0, 1.75f, 0)),
            CapsuleFeatureKind.FaceInterior, Point(0, 1.25, 0), Point(0, 1, 0), Direction(0, 1, 0),
            [Direction(0, 1, 0)]);
    }

    [Fact]
    public void HullCentroidWrapperPreservesTheSourceTopAtTwoMetres()
    {
        Vector3[] vertices =
        [
            new(-0.5f, 0, -0.5f), new(0.5f, 0, -0.5f),
            new(-0.5f, 0, 0.5f), new(0.5f, 0, 0.5f),
            new(-0.5f, 2, -0.5f), new(0.5f, 2, -0.5f),
            new(-0.5f, 2, 0.5f), new(0.5f, 2, 0.5f),
        ];
        using var scene = new Scene(new ConvexHullShape(vertices), Pose.Identity,
            new Vector3(0, 3, 0), -Vector3.UnitY, Point(0, 2, 0), Direction(0, 1, 0));
        scene.CheckTargetRay(new Vector3(0, -2, 0), Vector3.UnitY,
            Point(0, 0, 0), Direction(0, -1, 0));

        AssertInstalledHullMatchesSource(scene, vertices);

        // The owned registry check proved exact source correspondence, including the installed wrapper.
        AssertContact(scene, new CapsuleShape(0.25f, 1), Pose.At(new Vector3(0, 2.75f, 0)),
            CapsuleFeatureKind.FaceInterior, Point(0, 2.25, 0), Point(0, 2, 0), Direction(0, 1, 0),
            [Direction(0, 1, 0)]);
    }

    [Fact]
    public void CompoundChildTranslationDoesNotIncludeAnUnrelatedFarLeaf()
    {
        var compound = new CompoundShape(
        [
            new(UnitBox(), Pose.At(new Vector3(0, 3, 0))),
            new(UnitBox(), Pose.At(new Vector3(10, 0, 0))),
        ]);
        using var scene = new Scene(compound, Pose.Identity,
            new Vector3(0, 5, 0), -Vector3.UnitY, Point(0, 3.5, 0), Direction(0, 1, 0));
        scene.CheckTargetRay(new Vector3(10, 2, 0), -Vector3.UnitY,
            Point(10, 0.5, 0), Direction(0, 1, 0));

        AssertContact(scene, new CapsuleShape(0.25f, 1), Pose.At(new Vector3(0, 4.25f, 0)),
            CapsuleFeatureKind.FaceInterior, Point(0, 3.75, 0), Point(0, 3.5, 0), Direction(0, 1, 0),
            [Direction(0, 1, 0)]);
    }

    [Fact]
    public void DisconnectedCompoundEdgesWithEqualMinimaAreAmbiguous()
    {
        var compound = new CompoundShape(
        [
            new(UnitBox(), Pose.At(new Vector3(-0.875f, 0, 0))),
            new(UnitBox(), Pose.At(new Vector3(0.875f, 0, 0))),
        ]);
        using var scene = new Scene(compound, Pose.Identity,
            new Vector3(-0.875f, 2, 0), -Vector3.UnitY, Point(-0.875, 0.5, 0), Direction(0, 1, 0));
        scene.CheckTargetRay(new Vector3(0.875f, 2, 0), -Vector3.UnitY,
            Point(0.875, 0.5, 0), Direction(0, 1, 0));
        var capsule = new CapsuleShape(0.625f, 1);
        AssertExactTangency(Point(0, 1, 0), Point(-0.375, 0.5, 0), Direction(3, 4, 0, 5), capsule.Radius);
        AssertExactTangency(Point(0, 1, 0), Point(0.375, 0.5, 0), Direction(-3, 4, 0, 5), capsule.Radius);

        AssertRefusal(scene, capsule, Pose.At(new Vector3(0, 1.5f, 0)), CapsuleFeatureStatus.Ambiguous);
    }

    [Fact]
    public void ZeroLengthCapsuleHasAnIsolatedWestWallFaceWitness()
    {
        using var scene = new Scene(UnitBox(), Pose.Identity,
            new Vector3(-2, 0, 0), Vector3.UnitX, Point(-0.5, 0, 0), Direction(-1, 0, 0));

        AssertContact(scene, new CapsuleShape(0.25f, 0), Pose.At(new Vector3(-0.75f, 0, 0)),
            CapsuleFeatureKind.FaceInterior, Point(-0.75, 0, 0), Point(-0.5, 0, 0), Direction(-1, 0, 0),
            [Direction(-1, 0, 0)]);
    }

    [Fact]
    public void BoxUndersideUsesTheUpperAxisEndpointAndDownwardFace()
    {
        using var scene = new Scene(UnitBox(), Pose.Identity,
            new Vector3(0, -2, 0), Vector3.UnitY, Point(0, -0.5, 0), Direction(0, -1, 0));

        AssertContact(scene, new CapsuleShape(0.25f, 1), Pose.At(new Vector3(0, -1.25f, 0)),
            CapsuleFeatureKind.FaceInterior, Point(0, -0.75, 0), Point(0, -0.5, 0), Direction(0, -1, 0),
            [Direction(0, -1, 0)]);
    }

    [Fact]
    public void AxisStrictlyInsideTheSolidReturnsUnresolvedWithoutOutput()
    {
        using var scene = new Scene(UnitBox(), Pose.Identity,
            new Vector3(0, 2, 0), -Vector3.UnitY, Point(0, 0.5, 0), Direction(0, 1, 0));

        // Length 1/2 puts both endpoints at Y = +/-1/4, strictly inside all six faces.
        AssertRefusal(scene, new CapsuleShape(0.25f, 0.5f), Pose.Identity, CapsuleFeatureStatus.Unresolved);
    }

    [Fact]
    public void CurvedSphereTargetReturnsUnsupportedWithoutOutput()
    {
        using var scene = new Scene(new SphereShape(0.5f), Pose.Identity,
            new Vector3(0, 2, 0), -Vector3.UnitY, Point(0, 0.5, 0), Direction(0, 1, 0));

        AssertRefusal(scene, new CapsuleShape(0.25f, 1), Pose.At(new Vector3(0, 1.25f, 0)),
            CapsuleFeatureStatus.Unsupported);
    }

    [Fact]
    public void ZeroFaceCapacityReportsOneRequiredFaceWithoutAnyWrite()
    {
        using var scene = new Scene(UnitBox(), Pose.Identity,
            new Vector3(0, 2, 0), -Vector3.UnitY, Point(0, 0.5, 0), Direction(0, 1, 0));
        var capsule = new CapsuleShape(0.25f, 1);
        AssertExactTangency(Point(0, 0.75, 0), Point(0, 0.5, 0), Direction(0, 1, 0), capsule.Radius);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();

        CapsuleFeatureResult result = scene.Features.QueryCapsuleFeature(scene.Lease, scene.Target, capsule,
            Pose.At(new Vector3(0, 1.25f, 0)), 0, faces.AsSpan(3, 0), QueryFilter.StaticsOnly);

        AssertRefusedResult(scene, result, CapsuleFeatureStatus.CapacityExceeded, faces, original, requiredCapacity: 1);
    }

    [Fact]
    public void SixtyFiveFiniteBoxLeavesExceedTheWorkCapWithoutPartialOutput()
    {
        var children = new CompoundChild[65];
        for (int i = 0; i < children.Length; i++)
            children[i] = new CompoundChild(UnitBox(), Pose.At(new Vector3(2 * (i % 9), 0, 2 * (i / 9))));
        Assert.Equal(65, children.Length);
        using var scene = new Scene(new CompoundShape(children), Pose.Identity,
            new Vector3(0, 2, 0), -Vector3.UnitY, Point(0, 0.5, 0), Direction(0, 1, 0));
        scene.CheckTargetRay(new Vector3(2, 2, 14), -Vector3.UnitY,
            Point(2, 0.5, 14), Direction(0, 1, 0));

        AssertRefusal(scene, new CapsuleShape(0.25f, 1), Pose.At(new Vector3(0, 1.25f, 0)),
            CapsuleFeatureStatus.CapacityExceeded);
    }

    static void AssertInstalledHullMatchesSource(Scene scene, Vector3[] source)
    {
        // This reads only the test's owned registry under its actual lease. It does not call a solver
        // or construct an expected witness from query output. Any rounding mismatch is setup failure.
        scene.Lease.AssertCurrent();
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(scene.World));
        Assert.Equal(2, simulation.Statics.Count); // The excluded floor and this one target.
        int target = -1;
        for (int i = 0; i < simulation.Statics.Count; i++)
            if (simulation.Statics[i].Shape.Type == default(BepuCompound).TypeId)
            {
                Assert.Equal(-1, target);
                target = i;
            }
        Assert.True(target >= 0);
        ref var body = ref simulation.Statics[target];
        Assert.Equal(Vector3.Zero, body.Pose.Position);
        Assert.Equal(Quaternion.Identity, body.Pose.Orientation);
        ref BepuCompound compound = ref simulation.Shapes.GetShape<BepuCompound>(body.Shape.Index);
        Assert.Equal(1, compound.Children.Length);
        ref var child = ref compound.Children[0];
        Assert.Equal(default(BepuHull).TypeId, child.ShapeIndex.Type);
        Assert.Equal(Quaternion.Identity, child.LocalPose.Orientation);
        ref BepuHull hull = ref simulation.Shapes.GetShape<BepuHull>(child.ShapeIndex.Index);
        Assert.InRange(hull.FaceToVertexIndicesStart.Length, 1, 256);
        var expected = new HashSet<ExactVector>();
        foreach (Vector3 point in source) expected.Add(Point(point.X, point.Y, point.Z));
        var actual = new HashSet<ExactVector>();
        for (int face = 0; face < hull.FaceToVertexIndicesStart.Length; face++)
        {
            hull.GetVertexIndicesForFace(face, out var indices);
            Assert.InRange(indices.Length, 3, 8);
            for (int i = 0; i < indices.Length; i++)
            {
                hull.GetPoint(indices[i], out Vector3 point);
                actual.Add(new ExactVector(Rational.From(point.X) + Rational.From(child.LocalPose.Position.X),
                    Rational.From(point.Y) + Rational.From(child.LocalPose.Position.Y),
                    Rational.From(point.Z) + Rational.From(child.LocalPose.Position.Z)));
            }
        }
        Assert.True(expected.SetEquals(actual), "Installed hull plus centroid wrapper must match the exact source cuboid.");
        scene.Lease.AssertCurrent();
    }

    static BoxShape UnitBox() => new(new Vector3(0.5f));
    static ExactVector Point(double x, double y, double z) =>
        new(Rational.From(x), Rational.From(y), Rational.From(z));
    static ExactVector Direction(int x, int y, int z, int denominator = 1) =>
        new(new Rational(x, denominator), new Rational(y, denominator), new Rational(z, denominator));

    static void AssertContact(Scene scene, CapsuleShape capsule, Pose pose, CapsuleFeatureKind kind,
        ExactVector expectedAxis, ExactVector expectedGeometry, ExactVector expectedNormal, ExactVector[] expectedFaces)
    {
        AssertExactTangency(expectedAxis, expectedGeometry, expectedNormal, capsule.Radius);
        Assert.Equal(Quaternion.Identity, pose.Orientation);
        Rational halfLength = Rational.From(capsule.Length) / new Rational(2, 1);
        Assert.Equal(Rational.From(pose.Position.X), expectedAxis.X);
        Assert.Equal(Rational.From(pose.Position.Z), expectedAxis.Z);
        Assert.True(expectedAxis.Y >= Rational.From(pose.Position.Y) - halfLength);
        Assert.True(expectedAxis.Y <= Rational.From(pose.Position.Y) + halfLength);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();

        CapsuleFeatureResult result = scene.Features.QueryCapsuleFeature(scene.Lease, scene.Target, capsule,
            pose, 0, faces, QueryFilter.StaticsOnly);

        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Same(scene.View, result.QueryWorld);
        Assert.Same(scene.World, result.SourceWorld);
        Assert.Same(scene.Lease, result.Lease);
        Assert.Equal(scene.Lease.Origin, result.Origin);
        Assert.Equal(scene.Lease.GeometryGeneration, result.GeometryGeneration);
        Assert.Equal(scene.Target, result.Target);
        Assert.True(result.LeafId >= 0);
        Assert.True(result.FeatureId >= 0);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(expectedFaces.Length, result.Written);
        scene.Features.AssertFeatureCurrent(result, scene.Lease);
        scene.Lease.AssertCurrent();

        Rational positionError = AssertError(result.PositionErrorMetres, PositionCeiling);
        AssertVectorWithin(result.AxisPoint, expectedAxis, positionError, "axis witness");
        AssertVectorWithin(result.GeometryPoint, expectedGeometry, positionError, "geometry witness");
        Rational normalError = AssertError(result.NormalError, NormalCeiling);
        AssertVectorWithin(result.SeparationNormal, expectedNormal, normalError, "separation normal");
        Rational lower = Rational.From(result.SeparationLower), upper = Rational.From(result.SeparationUpper);
        Assert.True(lower <= Rational.Zero && upper >= Rational.Zero, "Exact contact separation zero must be enclosed.");
        Assert.True(lower <= upper, "Separation bounds must be ordered.");
        Assert.True(upper - lower <= SeparationWidthCeiling, "Separation width must be at most 1/10000 metre.");

        var faceIds = new HashSet<int>();
        var matched = new bool[expectedFaces.Length];
        for (int i = 0; i < result.Written; i++)
        {
            CapsuleIncidentFace face = faces[i];
            Assert.True(face.FaceId >= 0);
            Assert.True(faceIds.Add(face.FaceId), "Incident face identities must be unique within the original lease.");
            Assert.Equal(kind, face.Incidence);
            Rational faceError = AssertError(face.NormalError, NormalCeiling);
            int match = -1;
            for (int candidate = 0; candidate < expectedFaces.Length; candidate++)
                if (VectorWithin(face.Normal, expectedFaces[candidate], faceError))
                {
                    Assert.Equal(-1, match);
                    match = candidate;
                }
            Assert.True(match >= 0, "Incident normal must enclose one exact expected geometric face normal.");
            Assert.False(matched[match], "An incident face must not replace another face with a duplicate normal.");
            matched[match] = true;
        }
        Assert.All(matched, value => Assert.True(value));
        for (int i = result.Written; i < faces.Length; i++)
            Assert.Equal(original[i], faces[i]);
    }

    static void AssertExactTangency(ExactVector axis, ExactVector geometry, ExactVector normal, float radius)
    {
        Rational r = Rational.From(radius);
        ExactVector delta = axis - geometry;
        Assert.Equal(new Rational(1, 1), normal.LengthSquared());
        Assert.Equal(r * r, delta.LengthSquared());
        Assert.Equal(normal * r, delta);
    }

    static void AssertRefusal(Scene scene, CapsuleShape capsule, Pose pose, CapsuleFeatureStatus expected)
    {
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = scene.Features.QueryCapsuleFeature(scene.Lease, scene.Target, capsule,
            pose, 0, faces, QueryFilter.StaticsOnly);
        AssertRefusedResult(scene, result, expected, faces, original);
    }

    static void AssertRefusedResult(Scene scene, CapsuleFeatureResult result, CapsuleFeatureStatus expected,
        CapsuleIncidentFace[] faces, CapsuleIncidentFace[] original, int? requiredCapacity = null)
    {
        Assert.Equal(expected, result.Status);
        Assert.Equal(0, result.Written);
        Assert.Equal(original, faces);
        Assert.Null(result.QueryWorld);
        Assert.Null(result.SourceWorld);
        Assert.Null(result.Lease);
        Assert.Equal(CapsuleFeatureKind.None, result.Kind);
        Assert.Equal(Vector3.Zero, result.AxisPoint);
        Assert.Equal(Vector3.Zero, result.GeometryPoint);
        Assert.Equal(Vector3.Zero, result.SeparationNormal);
        Assert.Equal(0d, result.SeparationLower);
        Assert.Equal(0d, result.SeparationUpper);
        Assert.Equal(0f, result.PositionErrorMetres);
        Assert.Equal(0f, result.NormalError);
        Assert.True(result.RequiredCapacity >= 0);
        if (requiredCapacity.HasValue)
            Assert.Equal(requiredCapacity.Value, result.RequiredCapacity);
        else if (expected != CapsuleFeatureStatus.CapacityExceeded)
            Assert.Equal(0, result.RequiredCapacity);
        scene.Lease.AssertCurrent();
    }

    static CapsuleIncidentFace[] Sentinels()
    {
        var faces = new CapsuleIncidentFace[8];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = new CapsuleIncidentFace(900 + i, new Vector3(7, -11, 13), 0.125f, CapsuleFeatureKind.OpenBoundary);
        return faces;
    }

    static Rational AssertError(float value, Rational ceiling)
    {
        Rational exact = Rational.From(value);
        Assert.True(exact >= Rational.Zero && exact <= ceiling, "Reported error must be finite, nonnegative and within the exact ceiling.");
        return exact;
    }

    static bool VectorWithin(Vector3 actual, ExactVector expected, Rational bound) =>
        (Point(actual.X, actual.Y, actual.Z) - expected).LengthSquared() <= bound * bound;

    static void AssertVectorWithin(Vector3 actual, ExactVector expected, Rational bound, string field) =>
        Assert.True(VectorWithin(actual, expected, bound), $"The claimed {field} error must enclose its exact Euclidean vector error.");

    sealed class Scene : IDisposable
    {
        internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
        internal IPhysicsWorldQueryView View { get; private set; } = null!;
        internal IPhysicsQueryLease Lease { get; private set; } = null!;
        internal IPhysicsCapsuleFeatures Features { get; private set; } = null!;
        internal StaticHandle Target { get; private set; }

        internal Scene(PhysicsShape shape, Pose targetPose, Vector3 rayOrigin, Vector3 rayDirection,
            ExactVector expectedPoint, ExactVector expectedNormal)
        {
            try
            {
                StaticHandle floor = World.AddStatic(new BoxShape(new Vector3(64, 0.5f, 64)),
                    Pose.At(new Vector3(0, -16, 0)));
                StaticHandle installed = World.AddStatic(shape, targetPose);
                View = World.CreateQueryViewExcludingStatics([floor]);
                Features = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(View);
                Lease = Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(View).AcquireQueryReadLease();
                Lease.AssertCurrent();
                Assert.Same(World, Lease.SourceWorld);
                Assert.Equal(Vector3.Zero, Lease.Origin);

                Vector3 floorRay = new(-32, -14, -32);
                Assert.True(World.Raycast(floorRay, -Vector3.UnitY, 4, out RayHit floorHit, QueryFilter.StaticsOnly));
                Assert.Equal(floor, floorHit.Body);
                AssertRay(floorHit, Point(-32, -15.5, -32), Direction(0, 1, 0));
                Assert.False(View.Raycast(floorRay, -Vector3.UnitY, 4, out _, QueryFilter.StaticsOnly));

                Assert.True(View.Raycast(rayOrigin, rayDirection, 4, out RayHit hit, QueryFilter.StaticsOnly));
                Assert.True(hit.Body.HasValue, "Target handle must be captured from the selected view under its live lease.");
                Target = hit.Body.GetValueOrDefault();
                Assert.Equal(installed, Target);
                AssertRay(hit, expectedPoint, expectedNormal);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void CheckTargetRay(Vector3 origin, Vector3 direction, ExactVector expectedPoint, ExactVector expectedNormal)
        {
            Lease.AssertCurrent();
            Assert.True(View.Raycast(origin, direction, 4, out RayHit hit, QueryFilter.StaticsOnly));
            Assert.Equal(Target, hit.Body);
            AssertRay(hit, expectedPoint, expectedNormal);
        }

        static void AssertRay(RayHit hit, ExactVector point, ExactVector normal)
        {
            // Setup controls use the contract ceilings. Raycasts never construct correspondence expectations.
            AssertVectorWithin(hit.Point, point, PositionCeiling, "installed placement control");
            AssertVectorWithin(hit.Normal, normal, NormalCeiling, "installed normal control");
        }

        public void Dispose()
        {
            try { Lease?.Dispose(); }
            finally
            {
                try { View?.Dispose(); }
                finally { World.Dispose(); }
            }
        }
    }

    readonly record struct ExactVector(Rational X, Rational Y, Rational Z)
    {
        internal Rational LengthSquared() => X * X + Y * Y + Z * Z;
        public static ExactVector operator -(ExactVector a, ExactVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static ExactVector operator *(ExactVector a, Rational b) => new(a.X * b, a.Y * b, a.Z * b);
    }

    // Independent test-only rational arithmetic. Decoding IEEE bits preserves every represented input/output exactly.
    readonly record struct Rational
    {
        readonly BigInteger _numerator;
        readonly BigInteger _denominator;
        internal static Rational Zero => new(0, 1);

        internal Rational(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero) throw new ArgumentOutOfRangeException(nameof(denominator));
            if (denominator.Sign < 0)
            {
                numerator = -numerator;
                denominator = -denominator;
            }
            BigInteger divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            _numerator = numerator / divisor;
            _denominator = denominator / divisor;
        }

        internal static Rational From(double value)
        {
            Assert.True(double.IsFinite(value), "Numeric witnesses, bounds and errors must be finite.");
            ulong bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            int encodedExponent = (int)((bits >> 52) & 0x7ff);
            BigInteger numerator = bits & 0x000f_ffff_ffff_ffffUL;
            if (encodedExponent != 0) numerator += BigInteger.One << 52;
            int exponent = encodedExponent == 0 ? -1074 : encodedExponent - 1023 - 52;
            if ((bits >> 63) != 0) numerator = -numerator;
            return exponent >= 0
                ? new Rational(numerator << exponent, BigInteger.One)
                : new Rational(numerator, BigInteger.One << -exponent);
        }

        public static Rational operator +(Rational a, Rational b) =>
            new(a._numerator * b._denominator + b._numerator * a._denominator, a._denominator * b._denominator);
        public static Rational operator -(Rational a, Rational b) =>
            new(a._numerator * b._denominator - b._numerator * a._denominator, a._denominator * b._denominator);
        public static Rational operator *(Rational a, Rational b) => new(a._numerator * b._numerator, a._denominator * b._denominator);
        public static Rational operator /(Rational a, Rational b) => new(a._numerator * b._denominator, a._denominator * b._numerator);
        public static bool operator <=(Rational a, Rational b) => a._numerator * b._denominator <= b._numerator * a._denominator;
        public static bool operator >=(Rational a, Rational b) => a._numerator * b._denominator >= b._numerator * a._denominator;
        public override string ToString() => $"{_numerator}/{_denominator}";
    }
}
