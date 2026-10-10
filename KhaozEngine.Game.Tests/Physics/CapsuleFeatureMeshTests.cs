using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuMesh = BepuPhysics.Collidables.Mesh;
using BepuSim = BepuPhysics.Simulation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CapsuleFeatureMeshOracle;

namespace KhaozEngine.Tests.Physics;

// Independent finite mesh expectations retain every raw incident face.
public class CapsuleFeatureMeshTests
{
    static CapsuleShape Capsule() => new(0.25f, 0.5f);
    static Control Up(float x, float z, float y = 0) => new(new Vector3(x, y, z), Vector3.UnitY);
    static Control Down(float x, float z, float y = 0) => new(new Vector3(x, y, z), -Vector3.UnitY);
    static TriangleMeshShape Triangle(float y = 0) =>
        new([new(0, y, -2), new(2, y, -2), new(0, y, 2)], [0, 1, 2]);
    static TriangleMeshShape Rectangle() =>
        new([new(0, 0, -2), new(2, 0, -2), new(0, 0, 2), new(2, 0, 2)], [0, 1, 2, 1, 3, 2]);

    [Fact]
    public void SingleFaceReturnsTheIsolatedLowerEndpointAndRetainsSelection()
    {
        // Catches mesh blanket refusal, wrong endpoint, or a selected view falling back to its owner.
        using var scene = new Scene(Triangle(), Pose.Identity, Up(0.5f, -1));
        AssertContact(scene, Capsule(), Pose.At(new Vector3(0.5f, 0.5f, -1)), CapsuleFeatureKind.FaceInterior,
            Point(0.5, 0.25, -1), Point(0.5, 0, -1), Direction(0, 1, 0), [Direction(0, 1, 0)]);
        using IPhysicsWorldQueryView hidden = scene.World.CreateQueryViewExcludingStatics([scene.Target]);
        try
        {
            var capability = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(hidden);
            Assert.False(hidden.Raycast(new Vector3(0.5f, 1, -1), -Vector3.UnitY, 2, out _, QueryFilter.StaticsOnly));
            CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
            CapsuleFeatureResult result = Invoke(capability, scene.Lease, scene.Target, Capsule(),
                Pose.At(new Vector3(0.5f, 0.5f, -1)), faces);
            AssertRefused(result, faces, original, CapsuleFeatureStatus.Unavailable);
        }
        finally
        {
            scene.ReleaseLease();
        }
    }

    [Fact]
    public void FrontOpenBoundaryReturnsTheSlantedSeparationAndGeometricFaceNormal()
    {
        // Catches substitution of the triangle normal or its infinite plane for the finite edge.
        using var scene = new Scene(Triangle(), Pose.Identity, Up(0.5f, -1));
        AssertContact(scene, new CapsuleShape(5f / 32, 0.5f), Pose.At(new Vector3(-3f / 32, 3f / 8, 0)),
            CapsuleFeatureKind.OpenBoundary, Point(-3.0 / 32, 1.0 / 8, 0), Point(0, 0, 0),
            Direction(-3, 4, 0, 5), [Direction(0, 1, 0)]);
    }

    [Fact]
    public void ExactCoplanarSeamReturnsOnePatchWithBothIncidentTriangles()
    {
        // Catches treating a proved internal seam as an open boundary or dropping incident raw triangles.
        using var scene = new Scene(Rectangle(), Pose.Identity, Up(0.5f, -1), Up(1.5f, 1));
        AssertContact(scene, Capsule(), Pose.At(new Vector3(1, 0.5f, 0)), CapsuleFeatureKind.FaceInterior,
            Point(1, 0.25, 0), Point(1, 0, 0), Direction(0, 1, 0), [Direction(0, 1, 0), Direction(0, 1, 0)]);
        CapsuleIncidentFace[] shortFaces = [Sentinels()[0]], original = (CapsuleIncidentFace[])shortFaces.Clone();
        CapsuleFeatureResult capacity = scene.Query(Capsule(), Pose.At(new Vector3(1, 0.5f, 0)), shortFaces);
        AssertRefused(capacity, shortFaces, original, CapsuleFeatureStatus.CapacityExceeded, requiredCapacity: 2);
    }

    [Fact]
    public void DisconnectedCoplanarEqualMinimaAreAmbiguous()
    {
        // Catches joining disjoint triangles merely because the plane and certified distance agree.
        var mesh = new TriangleMeshShape(
            [new(-2, 0, -2), new(-3f / 32, 0, -2), new(-3f / 32, 0, 2),
             new(3f / 32, 0, -2), new(2, 0, -2), new(3f / 32, 0, 2)], [0, 1, 2, 3, 4, 5]);
        using var scene = new Scene(mesh, Pose.Identity, Up(-0.5f, -1), Up(0.5f, -1));
        AssertTangency(Point(0, 1.0 / 8, 0), Point(-3.0 / 32, 0, 0), Direction(3, 4, 0, 5), 5f / 32);
        AssertTangency(Point(0, 1.0 / 8, 0), Point(3.0 / 32, 0, 0), Direction(-3, 4, 0, 5), 5f / 32);
        AssertRefusal(scene, new CapsuleShape(5f / 32, 0.5f), Pose.At(new Vector3(0, 3f / 8, 0)),
            CapsuleFeatureStatus.Ambiguous);
    }

    [Fact]
    public void FiniteTriangleDomainRejectsTheInfinitePlaneFalsePositive()
    {
        // Every finite point has x >= 0 and y = 0. The axis has x = -1 and y >= 1/4.
        using var scene = new Scene(Triangle(), Pose.Identity, Up(0.5f, -1));
        Rational minimumSquared = new Rational(1, 1) + new Rational(1, 16);
        Assert.True(minimumSquared > Rational.From(Capsule().Radius) * Rational.From(Capsule().Radius));
        AssertTangency(Point(-1, 0.25, 0), Point(-1, 0, 0), Direction(0, 1, 0), 0.25f);
        AssertRefusal(scene, Capsule(), Pose.At(new Vector3(-1, 0.5f, 0)), CapsuleFeatureStatus.NoFeature);
    }

    [Fact]
    public void BacksideCapsuleCannotUseTheUpwardOneSidedTriangle()
    {
        // The whole axis is strictly behind the only oriented face, although its upper cap is tangent.
        using var scene = new Scene(Triangle(), Pose.Identity, Up(0.5f, -1));
        AssertTangency(Point(0.5, -0.25, -1), Point(0.5, 0, -1), Direction(0, -1, 0), 0.25f);
        AssertRefusal(scene, Capsule(), Pose.At(new Vector3(0.5f, -0.5f, -1)), CapsuleFeatureStatus.NoFeature);
    }

    [Fact]
    public void ReversedWindingAcceptsItsActualDownwardFront()
    {
        // Catches globally assuming every authored horizontal triangle faces upward.
        TriangleMeshShape mesh = Triangle();
        (mesh.Indices[1], mesh.Indices[2]) = (mesh.Indices[2], mesh.Indices[1]);
        using var scene = new Scene(mesh, Pose.Identity, Down(0.5f, -1));
        AssertContact(scene, Capsule(), Pose.At(new Vector3(0.5f, -0.5f, -1)), CapsuleFeatureKind.FaceInterior,
            Point(0.5, -0.25, -1), Point(0.5, 0, -1), Direction(0, -1, 0), [Direction(0, -1, 0)]);
    }

    [Fact]
    public void SignedZeroVerticesJoinTheExactCoplanarSeam()
    {
        // Catches using raw sign-bit equality instead of represented geometric equality for adjacency.
        float negativeZero = BitConverter.Int32BitsToSingle(int.MinValue);
        var mesh = new TriangleMeshShape(
            [new(0, 0, -1), new(1, 0, -1), new(0, 0, 1),
             new(negativeZero, negativeZero, -1), new(negativeZero, negativeZero, 1), new(-1, 0, -1)],
            [0, 1, 2, 3, 4, 5]);
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(mesh.Vertices[3].X));
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(mesh.Vertices[4].Y));
        using var scene = new Scene(mesh, Pose.Identity, Up(0.25f, -0.5f), Up(-0.25f, -0.5f));
        AssertContact(scene, Capsule(), Pose.At(new Vector3(0, 0.5f, 0)), CapsuleFeatureKind.FaceInterior,
            Point(0, 0.25, 0), Point(0, 0, 0), Direction(0, 1, 0), [Direction(0, 1, 0), Direction(0, 1, 0)]);
    }

    [Fact]
    public void OneRepresentableValueCrackNeverBecomesAnInternalSeam()
    {
        // Exact squared minima differ by 2^-46. A proof may refuse ordering, never epsilon-weld it.
        float next = MathF.BitIncrement(1f);
        var mesh = new TriangleMeshShape(
            [new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1),
             new(next, 0, -1), new(3, 0, -1), new(next, 0, 1)], [0, 1, 2, 3, 4, 5]);
        Rational crack = Rational.From(next) - Rational.From(1f);
        Assert.Equal(new Rational(1, BigInteger.One << 23), crack);
        Assert.True(new Rational(1, 16) + crack * crack > new Rational(1, 16));
        using var scene = new Scene(mesh, Pose.Identity, Up(0.5f, -0.5f), Up(1.5f, -0.5f));
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        Pose pose = Pose.At(new Vector3(1, 0.5f, 0));
        CapsuleFeatureResult result = scene.Query(Capsule(), pose, faces);
        if (result.Status == CapsuleFeatureStatus.Unresolved)
            AssertRefused(result, faces, original, CapsuleFeatureStatus.Unresolved);
        else
            AssertComplete(scene, result, faces, original, Capsule(), pose, CapsuleFeatureKind.OpenBoundary,
                Point(1, 0.25, 0), Point(1, 0, 0), Direction(0, 1, 0), [Direction(0, 1, 0)]);
    }

    [Fact]
    public void DuplicateTriangleNeighborhoodRefusesWithoutPublishingAPrefix()
    {
        // Catches returning a first duplicate as though unique topology had been proved.
        TriangleMeshShape triangle = Triangle();
        var mesh = new TriangleMeshShape(triangle.Vertices, [0, 1, 2, 0, 1, 2]);
        using var scene = new Scene(mesh, Pose.Identity, Up(0.5f, -1));
        AssertTopologyRefusal(scene, Pose.At(new Vector3(0.5f, 0.5f, -1)));
    }

    [Fact]
    public void ThreeIncidentTrianglesRefuseTheNonmanifoldSharedEdge()
    {
        // Catches considering only the first pair in a three-triangle edge neighborhood.
        var mesh = new TriangleMeshShape(
            [new(0, 0, -1), new(1, 0, -1), new(0, 0, 1), new(-1, 0, -1), new(2, 0, 1)],
            [0, 1, 2, 0, 2, 3, 0, 4, 2]);
        using var scene = new Scene(mesh, Pose.Identity,
            Up(0.25f, -0.5f), Up(-0.25f, -0.5f), Up(0.5f, 0.5f));
        AssertTopologyRefusal(scene, Pose.At(new Vector3(0, 0.5f, 0)));
    }

    [Fact]
    public void InconsistentWindingRefusesTheSharedNeighborhood()
    {
        // Catches ignoring a back-facing neighbor and inventing an open top edge at its shared seam.
        TriangleMeshShape rectangle = Rectangle();
        var mesh = new TriangleMeshShape(rectangle.Vertices, [0, 1, 2, 1, 2, 3]);
        using var scene = new Scene(mesh, Pose.Identity, Up(0.5f, -1), Down(1.5f, 1));
        AssertTopologyRefusal(scene, Pose.At(new Vector3(1, 0.5f, 0)));
    }

    [Fact]
    public void SameStaticLowerTopAndUpperRoofCompetingMinimaAreAmbiguous()
    {
        // Both cap endpoints are exactly tangent to disconnected opposing faces on this one static.
        var mesh = new TriangleMeshShape(
            [new(0, 0, -2), new(2, 0, -2), new(0, 0, 2),
             new(0, 1, -2), new(2, 1, -2), new(0, 1, 2)], [0, 1, 2, 3, 5, 4]);
        using var scene = new Scene(mesh, Pose.Identity, Up(0.5f, -1), Down(0.5f, -1, 1));
        AssertTangency(Point(0.5, 0.25, -1), Point(0.5, 0, -1), Direction(0, 1, 0), 0.25f);
        AssertTangency(Point(0.5, 0.75, -1), Point(0.5, 1, -1), Direction(0, -1, 0), 0.25f);
        AssertRefusal(scene, Capsule(), Pose.At(new Vector3(0.5f, 0.5f, -1)), CapsuleFeatureStatus.Ambiguous);
    }

    [Fact]
    public void FreshRebaseUsesActualNewPoseAndDoesNotReviveOldOutput()
    {
        // Catches cached world witnesses or lease metadata surviving a physical frame change.
        using var scene = new Scene(Triangle(), Pose.At(new Vector3(96, 8, -160)), Up(0.5f, -1));
        CapsuleFeatureResult old = AssertInterior(scene, new Vector3(96, 8, -160));
        IPhysicsQueryLease oldLease = scene.Lease;
        scene.ReleaseLease();
        scene.World.Rebase(new Vector3(64, 4, -128));
        scene.Acquire(Pose.At(new Vector3(32, 4, -32)));
        Assert.Equal(new Vector3(64, 4, -128), scene.Lease.Origin);
        Assert.True(scene.Lease.GeometryGeneration > old.GeometryGeneration);
        Assert.NotSame(oldLease, scene.Lease);
        AssertInterior(scene, new Vector3(32, 4, -32));
        Assert.Throws<InvalidOperationException>(() => scene.Features.AssertFeatureCurrent(old, scene.Lease));
        AssertExpiredQuery(scene, oldLease, old.Target);
    }

    [Fact]
    public void RemovalAndReaddUseNewInstalledTrianglesWithoutRevivingOldOutput()
    {
        // Catches adjacency cached by a recycled backend slot instead of its live installed lifetime.
        using var scene = new Scene(Triangle(), Pose.Identity, Up(0.5f, -1));
        CapsuleFeatureResult old = AssertInterior(scene, Vector3.Zero);
        IPhysicsQueryLease oldLease = scene.Lease;
        StaticHandle removed = scene.Target;
        scene.ReleaseLease();
        scene.World.RemoveStatic(removed);
        scene.AcquireWithoutTarget();
        Assert.False(scene.View.Raycast(new Vector3(0.5f, 1, -1), -Vector3.UnitY, 2, out _, QueryFilter.StaticsOnly));
        scene.ReleaseLease();
        scene.InstallReplacement(Triangle(1), Pose.Identity, Up(0.5f, -1, 1));
        AssertContact(scene, Capsule(), Pose.At(new Vector3(0.5f, 1.5f, -1)), CapsuleFeatureKind.FaceInterior,
            Point(0.5, 1.25, -1), Point(0.5, 1, -1), Direction(0, 1, 0), [Direction(0, 1, 0)]);
        Assert.Throws<InvalidOperationException>(() => scene.Features.AssertFeatureCurrent(old, scene.Lease));
        AssertExpiredQuery(scene, oldLease, removed);
    }

    [Fact]
    public void CallerArrayMutationCannotChangeTheInstalledMeshAuthority()
    {
        // Catches using caller descriptors to construct later adjacency or correspondence witnesses.
        TriangleMeshShape shape = Triangle();
        using var scene = new Scene(shape, Pose.Identity, Up(0.5f, -1));
        long generation = scene.Lease.GeometryGeneration;
        for (int i = 0; i < shape.Vertices.Length; i++) shape.Vertices[i] += new Vector3(8, 4, 0);
        shape.Indices[0] = int.MaxValue;
        scene.ProveInstalledGeometry();
        scene.CheckControls();
        Assert.Equal(generation, scene.Lease.GeometryGeneration);
        AssertInterior(scene, Vector3.Zero);
    }

    static CapsuleFeatureResult AssertInterior(Scene scene, Vector3 offset) =>
        AssertContact(scene, Capsule(), Pose.At(offset + new Vector3(0.5f, 0.5f, -1)), CapsuleFeatureKind.FaceInterior,
            Point(offset) + Point(0.5, 0.25, -1), Point(offset) + Point(0.5, 0, -1),
            Direction(0, 1, 0), [Direction(0, 1, 0)]);

    static CapsuleFeatureResult AssertContact(Scene scene, CapsuleShape capsule, Pose pose, CapsuleFeatureKind kind,
        ExactVector axis, ExactVector geometry, ExactVector normal, ExactVector[] incidentNormals)
    {
        AssertTangency(axis, geometry, normal, capsule.Radius);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = scene.Query(capsule, pose, faces);
        AssertComplete(scene, result, faces, original, capsule, pose, kind, axis, geometry, normal, incidentNormals);
        return result;
    }

    static void AssertComplete(Scene scene, CapsuleFeatureResult result, CapsuleIncidentFace[] faces,
        CapsuleIncidentFace[] original, CapsuleShape capsule, Pose pose, CapsuleFeatureKind kind,
        ExactVector axis, ExactVector geometry, ExactVector normal, ExactVector[] incidentNormals)
    {
        AssertTangency(axis, geometry, normal, capsule.Radius);
        Assert.Equal(Quaternion.Identity, pose.Orientation);
        Rational halfLength = Rational.From(capsule.Length) / new Rational(2, 1);
        Assert.Equal(Rational.From(pose.Position.X), axis.X);
        Assert.Equal(Rational.From(pose.Position.Z), axis.Z);
        Assert.True(axis.Y >= Rational.From(pose.Position.Y) - halfLength);
        Assert.True(axis.Y <= Rational.From(pose.Position.Y) + halfLength);
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
        Assert.Equal(incidentNormals.Length, result.Written);
        Assert.Equal(0, result.RequiredCapacity);
        scene.Features.AssertFeatureCurrent(result, scene.Lease);
        Assert.Throws<InvalidOperationException>(() =>
            Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(scene.World).AssertFeatureCurrent(result, scene.Lease));
        scene.Lease.AssertCurrent();
        Rational positionError = AssertError(result.PositionErrorMetres, PositionCeiling);
        AssertVectorWithin(result.AxisPoint, axis, positionError, "axis witness");
        AssertVectorWithin(result.GeometryPoint, geometry, positionError, "finite geometry witness");
        Rational normalError = AssertError(result.NormalError, NormalCeiling);
        AssertVectorWithin(result.SeparationNormal, normal, normalError, "separation direction");
        Rational lower = Rational.From(result.SeparationLower), upper = Rational.From(result.SeparationUpper);
        Assert.True(lower <= Rational.Zero && upper >= Rational.Zero, "Exact tangency separation must be enclosed.");
        Assert.True(lower <= upper && upper - lower <= SeparationWidthCeiling);
        var ids = new HashSet<int>();
        var matched = new bool[incidentNormals.Length];
        for (int i = 0; i < result.Written; i++)
        {
            CapsuleIncidentFace face = faces[i];
            Assert.True(face.FaceId >= 0 && ids.Add(face.FaceId));
            Assert.Equal(kind, face.Incidence);
            Rational error = AssertError(face.NormalError, NormalCeiling);
            int match = -1;
            for (int candidate = 0; candidate < incidentNormals.Length; candidate++)
                if (!matched[candidate] && VectorWithin(face.Normal, incidentNormals[candidate], error))
                {
                    match = candidate;
                    break;
                }
            Assert.True(match >= 0, "Incident oriented normals must match the expected multiset, including coplanar repeats.");
            matched[match] = true;
        }
        Assert.All(matched, value => Assert.True(value));
        for (int i = result.Written; i < faces.Length; i++) Assert.Equal(original[i], faces[i]);
    }

    static void AssertRefusal(Scene scene, CapsuleShape capsule, Pose pose, CapsuleFeatureStatus status)
    {
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        AssertRefused(scene.Query(capsule, pose, faces), faces, original, status);
        scene.Lease.AssertCurrent();
    }

    static void AssertTopologyRefusal(Scene scene, Pose pose)
    {
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = scene.Query(Capsule(), pose, faces);
        Assert.True(result.Status is CapsuleFeatureStatus.Ambiguous or CapsuleFeatureStatus.Unresolved,
            "Observed invalid topology must refuse. Incomplete topology proof may remain unresolved.");
        AssertRefused(result, faces, original, result.Status);
        scene.Lease.AssertCurrent();
    }

    static void AssertRefused(CapsuleFeatureResult result, CapsuleIncidentFace[] faces,
        CapsuleIncidentFace[] original, CapsuleFeatureStatus status, int requiredCapacity = 0)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(0, result.Written);
        Assert.Equal(requiredCapacity, result.RequiredCapacity);
        Assert.Equal(original, faces);
        Assert.Null(result.QueryWorld);
        Assert.Null(result.SourceWorld);
        Assert.Null(result.Lease);
        Assert.Equal(Vector3.Zero, result.Origin);
        Assert.Equal(0L, result.GeometryGeneration);
        Assert.Equal(default(StaticHandle), result.Target);
        Assert.Equal(0, result.LeafId);
        Assert.Equal(0, result.FeatureId);
        Assert.Equal(CapsuleFeatureKind.None, result.Kind);
        Assert.Equal(Vector3.Zero, result.AxisPoint);
        Assert.Equal(Vector3.Zero, result.GeometryPoint);
        Assert.Equal(Vector3.Zero, result.SeparationNormal);
        Assert.Equal(0d, result.SeparationLower);
        Assert.Equal(0d, result.SeparationUpper);
        Assert.Equal(0f, result.PositionErrorMetres);
        Assert.Equal(0f, result.NormalError);
    }

    static void AssertExpiredQuery(Scene scene, IPhysicsQueryLease oldLease, StaticHandle target)
    {
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        Assert.Throws<ObjectDisposedException>(() => Invoke(scene.Features, oldLease, target, Capsule(), Pose.Identity, faces));
        Assert.Equal(original, faces);
    }

    static CapsuleFeatureResult Invoke(IPhysicsCapsuleFeatures features, IPhysicsQueryLease lease, StaticHandle target,
        CapsuleShape capsule, Pose pose, CapsuleIncidentFace[] faces)
    {
        CapsuleIncidentFace[] original = (CapsuleIncidentFace[])faces.Clone();
        try { return features.QueryCapsuleFeature(lease, target, capsule, pose, 0, faces, QueryFilter.StaticsOnly); }
        catch
        {
            Assert.Equal(original, faces);
            throw;
        }
    }

    static CapsuleIncidentFace[] Sentinels()
    {
        var faces = new CapsuleIncidentFace[8];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = new CapsuleIncidentFace(900 + i, new Vector3(7, -11, 13), 0.125f, CapsuleFeatureKind.Vertex);
        return faces;
    }

    readonly record struct Control(Vector3 LocalPoint, Vector3 Normal);
    readonly record struct ExactTriangle(ExactVector A, ExactVector B, ExactVector C);

    sealed class Scene : IDisposable
    {
        internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
        internal IPhysicsWorldQueryView View { get; private set; } = null!;
        internal IPhysicsCapsuleFeatures Features { get; private set; } = null!;
        internal IPhysicsQueryLease Lease { get; private set; } = null!;
        internal StaticHandle Target { get; private set; }
        StaticHandle _installed, _floor;
        Pose _pose;
        Control[] _controls = [];
        ExactTriangle[] _triangles = [];

        internal Scene(TriangleMeshShape shape, Pose pose, params Control[] controls)
        {
            try
            {
                _floor = World.AddStatic(new BoxShape(new Vector3(64, 0.5f, 64)), Pose.At(new Vector3(0, -16, 0)));
                Snapshot(shape, controls);
                _installed = World.AddStatic(shape, pose);
                View = World.CreateQueryViewExcludingStatics([_floor]);
                Features = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(View);
                Acquire(pose);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        void Snapshot(TriangleMeshShape shape, Control[] controls)
        {
            Assert.InRange(shape.Indices.Length / 3, 1, 3);
            Assert.Equal(0, shape.Indices.Length % 3);
            Assert.InRange(controls.Length, 1, 3);
            _controls = (Control[])controls.Clone();
            _triangles = new ExactTriangle[shape.Indices.Length / 3];
            for (int i = 0; i < _triangles.Length; i++)
                _triangles[i] = new ExactTriangle(Point(shape.Vertices[shape.Indices[3 * i]]),
                    Point(shape.Vertices[shape.Indices[3 * i + 1]]), Point(shape.Vertices[shape.Indices[3 * i + 2]]));
        }

        internal void Acquire(Pose pose)
        {
            _pose = pose;
            AcquireWithoutTarget();
            ProveInstalledGeometry();
            CheckControls();
        }

        internal void AcquireWithoutTarget()
        {
            Lease = Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(View).AcquireQueryReadLease();
            Lease.AssertCurrent();
            Assert.Same(World, Lease.SourceWorld);
            Assert.Same(World, View.SourceWorld);
            Assert.Equal(World.Origin, Lease.Origin);
            Vector3 floorPoint = new Vector3(-32, -15.5f, -32) - Lease.Origin;
            Assert.True(World.Raycast(floorPoint + Vector3.UnitY, -Vector3.UnitY, 2, out RayHit hit, QueryFilter.StaticsOnly));
            Assert.Equal(_floor, hit.Body);
            AssertVectorWithin(hit.Point, Point(floorPoint), PositionCeiling, "excluded floor placement");
            Assert.False(View.Raycast(floorPoint + Vector3.UnitY, -Vector3.UnitY, 2, out _, QueryFilter.StaticsOnly));
        }

        internal void ReleaseLease() => Lease.Dispose();

        internal void InstallReplacement(TriangleMeshShape shape, Pose pose, params Control[] controls)
        {
            Snapshot(shape, controls);
            _installed = World.AddStatic(shape, pose);
            Acquire(pose);
        }

        internal void CheckControls()
        {
            Lease.AssertCurrent();
            foreach (Control control in _controls)
            {
                // All fixtures use identity rotation and exact dyadic translation. No ray supplies an expected witness.
                Vector3 point = _pose.Position + control.LocalPoint;
                ExactVector exact = Point(_pose.Position) + Point(control.LocalPoint);
                Assert.True(View.Raycast(point + control.Normal * 0.125f, -control.Normal, 0.25f,
                    out RayHit hit, QueryFilter.StaticsOnly), "The installed face must accept its actual front-side control.");
                Assert.True(hit.Body.HasValue, "Capture the handle from this selected receiver under the current lease.");
                Target = hit.Body.GetValueOrDefault();
                Assert.Equal(_installed, Target);
                AssertVectorWithin(hit.Point, exact, PositionCeiling, "installed triangle placement control");
                AssertVectorWithin(hit.Normal, Point(control.Normal), NormalCeiling, "installed winding control");
                Assert.False(View.Raycast(point - control.Normal * 0.125f, control.Normal, 0.25f,
                    out _, QueryFilter.StaticsOnly), "The strictly interior opposite-side control must pass through.");
            }
            Lease.AssertCurrent();
        }

        internal void ProveInstalledGeometry()
        {
            // Setup only. Inspect owned public Bepu buffers under the lease, never its correspondence implementation.
            Lease.AssertCurrent();
            Assert.Equal(Quaternion.Identity, _pose.Orientation);
            FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(World));
            Assert.Equal(2, simulation.Statics.Count);
            int meshStatic = -1;
            for (int i = 0; i < simulation.Statics.Count; i++)
                if (simulation.Statics[i].Shape.Type == default(BepuMesh).TypeId)
                {
                    Assert.Equal(-1, meshStatic);
                    meshStatic = i;
                }
            Assert.True(meshStatic >= 0);
            ref var body = ref simulation.Statics[meshStatic];
            Assert.Equal(_pose.Position, body.Pose.Position);
            Assert.Equal(_pose.Orientation, body.Pose.Orientation);
            ref BepuMesh mesh = ref simulation.Shapes.GetShape<BepuMesh>(body.Shape.Index);
            Assert.Equal(Vector3.One, mesh.Scale);
            Assert.Equal(_triangles.Length, mesh.Triangles.Length);
            var matched = new bool[_triangles.Length];
            for (int i = 0; i < mesh.Triangles.Length; i++)
            {
                ref var t = ref mesh.Triangles[i];
                var actual = new ExactTriangle(Point(t.A), Point(t.B), Point(t.C));
                int match = -1;
                for (int candidate = 0; candidate < _triangles.Length; candidate++)
                {
                    ExactTriangle expected = _triangles[candidate];
                    bool same = actual == expected || actual == new ExactTriangle(expected.B, expected.C, expected.A) ||
                        actual == new ExactTriangle(expected.C, expected.A, expected.B);
                    if (!matched[candidate] && same)
                    {
                        match = candidate;
                        break;
                    }
                }
                Assert.True(match >= 0, "Every installed finite oriented triangle must equal the frozen source multiset.");
                matched[match] = true;
            }
            Assert.All(matched, value => Assert.True(value));
            Lease.AssertCurrent();
        }

        internal CapsuleFeatureResult Query(CapsuleShape capsule, Pose pose, CapsuleIncidentFace[] faces) =>
            Invoke(Features, Lease, Target, capsule, pose, faces);

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
}
