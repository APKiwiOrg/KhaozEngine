using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuPhysics.Collidables;
using BepuUtilities;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;
using BepuCompound = BepuPhysics.Collidables.Compound;
using BepuMesh = BepuPhysics.Collidables.Mesh;
using BepuSim = BepuPhysics.Simulation;
using RigidPose = BepuPhysics.RigidPose;
using CompoundChild = KhaozEngine.Physics.CompoundChild;

namespace KhaozEngine.Tests.Physics;

// Raw source/API premises stay separate from the independent oracle. The scene below uses
// authorized owned-test reflection only to read _sim, then checks public registry data under its lease.
internal static class InstalledPoseSourceChecks
{
    internal static void Coefficients(Quaternion q)
    {
        M expected = Represented(q);
        Matrix3x3.CreateFromQuaternion(q, out Matrix3x3 scalar);
        Assert.Equal(expected, Read(scalar));
        var wideQuaternion = new QuaternionWide
        {
            X = new Vector<float>(q.X), Y = new Vector<float>(q.Y),
            Z = new Vector<float>(q.Z), W = new Vector<float>(q.W),
        };
        Matrix3x3Wide.CreateFromQuaternion(wideQuaternion, out Matrix3x3Wide wide);
        // Every broadcast lane is checked against the independent per-operation binary32 graph.
        for (int i = 0; i < Vector<float>.Count; i++)
        {
            var actual = new M(new(R.From(wide.X.X[i]), R.From(wide.X.Y[i]), R.From(wide.X.Z[i])),
                new(R.From(wide.Y.X[i]), R.From(wide.Y.Y[i]), R.From(wide.Y.Z[i])),
                new(R.From(wide.Z.X[i]), R.From(wide.Z.Y[i]), R.From(wide.Z.Z[i])));
            Assert.Equal(expected, actual);
        }
    }

    internal static void PointOperations(Quaternion q, Vector3 translation, Vector3 local)
    {
        V expected = RoundedPoint(Represented(q), V.From(translation), V.From(local));
        Matrix3x3.CreateFromQuaternion(q, out Matrix3x3 scalar);
        Matrix3x3.Transform(local, scalar, out Vector3 scalarOutput);
        Assert.Equal(expected, V.From(scalarOutput + translation));
        var wideQuaternion = new QuaternionWide
        {
            X = new Vector<float>(q.X), Y = new Vector<float>(q.Y),
            Z = new Vector<float>(q.Z), W = new Vector<float>(q.W),
        };
        Matrix3x3Wide.CreateFromQuaternion(wideQuaternion, out Matrix3x3Wide wide);
        Vector3Wide.Broadcast(local, out Vector3Wide wideLocal);
        Vector3Wide.Broadcast(translation, out Vector3Wide wideTranslation);
        Matrix3x3Wide.TransformWithoutOverlap(wideLocal, wide, out Vector3Wide rotated);
        Vector3Wide.Add(rotated, wideTranslation, out Vector3Wide output);
        for (int i = 0; i < Vector<float>.Count; i++)
            Assert.Equal(expected, new V(R.From(output.X[i]), R.From(output.Y[i]), R.From(output.Z[i])));
    }

    internal static Pose Composition(Pose child, Pose root)
    {
        Pose expected = Composed(child, root);
        var childRaw = new RigidPose(child.Position, child.Orientation);
        var rootRaw = new RigidPose(root.Position, root.Orientation);
        BepuCompound.GetWorldPose(childRaw, rootRaw, out RigidPose raw);
        Assert.Equal(expected.Position, raw.Position);
        Assert.Equal(expected.Orientation, raw.Orientation);
        Coefficients(raw.Orientation);
        return expected;
    }

    static M Read(Matrix3x3 matrix) => new(V.From(matrix.X), V.From(matrix.Y), V.From(matrix.Z));
}

internal sealed class InstalledPoseScene : IDisposable
{
    internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
    internal IPhysicsWorldQueryView View { get; private set; } = null!;
    internal IPhysicsCapsuleFeatures Features { get; private set; } = null!;
    internal IPhysicsQueryLease Lease { get; private set; } = null!;
    internal StaticHandle Target { get; private set; }
    internal Pose? InstalledHullLocalPose { get; private set; }
    internal Pose? InstalledHullPose { get; private set; }

    internal InstalledPoseScene(PhysicsShape shape, Pose targetPose)
    {
        try
        {
            PhysicsShape frozen = Freeze(shape);
            StaticHandle floor = World.AddStatic(new BoxShape(new Vector3(8, 0.5f, 8)), Pose.At(new Vector3(0, -16, 0)));
            Target = World.AddStatic(shape, targetPose);
            View = World.CreateQueryViewExcludingStatics([floor]);
            Features = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(View);
            Lease = Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(View).AcquireQueryReadLease();
            Lease.AssertCurrent();
            Assert.Same(World, View.SourceWorld);
            Assert.Same(World, Lease.SourceWorld);
            Assert.Equal(Vector3.Zero, Lease.Origin);
            Assert.Equal(World.Origin, Lease.Origin);
            AssertInstalledIdentity(frozen, targetPose);
            // A receiver/selection control uses no ray witness as expected feature geometry.
            Assert.True(World.Raycast(new Vector3(0, -14, 0), -Vector3.UnitY, 3, out RayHit hit, QueryFilter.StaticsOnly));
            Assert.Equal(floor, hit.Body);
            Assert.False(View.Raycast(new Vector3(0, -14, 0), -Vector3.UnitY, 3, out _, QueryFilter.StaticsOnly));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    static PhysicsShape Freeze(PhysicsShape source) => source switch
    {
        BoxShape box => new BoxShape(box.HalfExtents),
        ConvexHullShape hull => new ConvexHullShape((Vector3[])hull.Points.Clone()),
        TriangleMeshShape mesh => new TriangleMeshShape((Vector3[])mesh.Vertices.Clone(), (int[])mesh.Indices.Clone()),
        CompoundShape compound => new CompoundShape(Array.ConvertAll(compound.Children,
            child => new CompoundChild(Freeze(child.Shape), child.Local))),
        _ => throw new ArgumentException("This bounded fixture requires box, hull, mesh or box-compound data.", nameof(source)),
    };

    void AssertInstalledIdentity(PhysicsShape frozen, Pose expectedPose)
    {
        // Only the owned registry field is reflected. All raw pose/shape reads below use public APIs.
        // This method checks subject identity, never an expected feature witness or classifier result.
        Lease.AssertCurrent();
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(World));
        Assert.Equal(2, simulation.Statics.Count);
        Pose floorPose = Pose.At(new Vector3(0, -16, 0));
        var floorRaw = new RigidPose(floorPose.Position, floorPose.Orientation);
        Assert.False(SamePose(floorRaw, expectedPose));
        int targetIndex = -1, floorIndex = -1;
        for (int i = 0; i < simulation.Statics.Count; i++)
        {
            ref var body = ref simulation.Statics[i];
            if (SamePose(body.Pose, floorPose))
            {
                Assert.Equal(-1, floorIndex);
                floorIndex = i;
                AssertBox(simulation, body.Shape, new Vector3(8, 0.5f, 8));
            }
            else
            {
                Assert.True(SamePose(body.Pose, expectedPose),
                    "The only non-floor static must match the frozen target pose bit for bit.");
                Assert.Equal(-1, targetIndex);
                targetIndex = i;
            }
        }
        Assert.True(targetIndex >= 0 && floorIndex >= 0 && targetIndex != floorIndex);
        // Dense registry indices are discovered above, never inferred from the seam handle value.
        ref var target = ref simulation.Statics[targetIndex];
        AssertPose(target.Pose, expectedPose);
        switch (frozen)
        {
            case BoxShape box:
                AssertBox(simulation, target.Shape, box.HalfExtents);
                break;
            case TriangleMeshShape mesh:
                AssertMesh(simulation, target.Shape, mesh);
                break;
            case ConvexHullShape hull:
                AssertHull(simulation, target.Shape, target.Pose, expectedPose, hull);
                break;
            case CompoundShape compound:
                AssertCompound(simulation, target.Shape, target.Pose, expectedPose, compound);
                break;
            default: throw new ArgumentException("Unknown frozen fixture shape.", nameof(frozen));
        }
        Lease.AssertCurrent();
    }

    static void AssertBox(BepuSim simulation, TypedIndex index, Vector3 expectedHalfExtents)
    {
        Assert.Equal(default(Box).TypeId, index.Type);
        ref Box installed = ref simulation.Shapes.GetShape<Box>(index.Index);
        Assert.Equal(BitConverter.SingleToInt32Bits(expectedHalfExtents.X), BitConverter.SingleToInt32Bits(installed.HalfWidth));
        Assert.Equal(BitConverter.SingleToInt32Bits(expectedHalfExtents.Y), BitConverter.SingleToInt32Bits(installed.HalfHeight));
        Assert.Equal(BitConverter.SingleToInt32Bits(expectedHalfExtents.Z), BitConverter.SingleToInt32Bits(installed.HalfLength));
    }

    static void AssertMesh(BepuSim simulation, TypedIndex index, TriangleMeshShape frozen)
    {
        Assert.Equal(default(BepuMesh).TypeId, index.Type);
        ref BepuMesh installed = ref simulation.Shapes.GetShape<BepuMesh>(index.Index);
        Assert.Equal(Bits(Vector3.One), Bits(installed.Scale));
        Assert.Equal(3, frozen.Indices.Length); // The approved package contains one triangle per mesh.
        Assert.Equal(1, installed.Triangles.Length);
        ref var triangle = ref installed.Triangles[0];
        Assert.Equal(Bits(frozen.Vertices[frozen.Indices[0]]), Bits(triangle.A));
        Assert.Equal(Bits(frozen.Vertices[frozen.Indices[1]]), Bits(triangle.B));
        Assert.Equal(Bits(frozen.Vertices[frozen.Indices[2]]), Bits(triangle.C));
    }

    void AssertHull(BepuSim simulation, TypedIndex index, RigidPose root, Pose expectedRoot, ConvexHullShape frozen)
    {
        Assert.Equal(default(BepuCompound).TypeId, index.Type);
        ref BepuCompound wrapper = ref simulation.Shapes.GetShape<BepuCompound>(index.Index);
        Assert.Equal(1, wrapper.Children.Length);
        ref var child = ref wrapper.Children[0];
        Assert.Equal(default(ConvexHull).TypeId, child.ShapeIndex.Type);
        // The floating hull builder's centroid is installed data, not the ideal cuboid centre.
        // Exact source restoration below proves the centered vertices and this offset together.
        Assert.Equal(Quaternion.Identity, child.LocalPose.Orientation);
        Assert.Equal(0f, child.LocalPose.Position.X);
        Assert.Equal(0f, child.LocalPose.Position.Z);
        Pose actualLocal = new(child.LocalPose.Position, child.LocalPose.Orientation);
        BepuCompound.GetWorldPose(child.LocalPose, root, out RigidPose installedLeafPose);
        AssertPose(installedLeafPose, Composed(actualLocal, expectedRoot));
        InstalledHullLocalPose = actualLocal;
        InstalledHullPose = new(installedLeafPose.Position, installedLeafPose.Orientation);
        ref ConvexHull installed = ref simulation.Shapes.GetShape<ConvexHull>(child.ShapeIndex.Index);
        Assert.Equal(8, frozen.Points.Length);
        Assert.Equal(6, installed.FaceToVertexIndicesStart.Length);
        var expected = new HashSet<V>();
        foreach (Vector3 vertex in frozen.Points) expected.Add(V.From(vertex));
        var actual = new HashSet<V>();
        for (int face = 0; face < installed.FaceToVertexIndicesStart.Length; face++)
        {
            installed.GetVertexIndicesForFace(face, out var indices);
            Assert.Equal(4, indices.Length);
            for (int i = 0; i < indices.Length; i++)
            {
                installed.GetPoint(indices[i], out Vector3 rawVertex);
                // Exact restoration checks the raw centered data and wrapper, not a feature point.
                actual.Add(V.From(rawVertex) + V.From(child.LocalPose.Position));
            }
        }
        Assert.Equal(8, expected.Count);
        Assert.Equal(8, actual.Count);
        Assert.True(expected.SetEquals(actual),
            "Actual installed hull vertices plus its centroid wrapper must equal the frozen source cuboid.");
    }

    static void AssertCompound(BepuSim simulation, TypedIndex index, RigidPose root, Pose expectedRoot, CompoundShape frozen)
    {
        Assert.Equal(default(BepuCompound).TypeId, index.Type);
        ref BepuCompound installed = ref simulation.Shapes.GetShape<BepuCompound>(index.Index);
        Assert.Single(frozen.Children);
        Assert.Equal(frozen.Children.Length, installed.Children.Length);
        ref var child = ref installed.Children[0];
        CompoundChild expectedChild = frozen.Children[0];
        BoxShape expectedBox = Assert.IsType<BoxShape>(expectedChild.Shape);
        AssertBox(simulation, child.ShapeIndex, expectedBox.HalfExtents);
        AssertPose(child.LocalPose, expectedChild.Local);
        BepuCompound.GetWorldPose(child.LocalPose, root, out RigidPose installedLeafPose);
        AssertPose(installedLeafPose, Composed(expectedChild.Local, expectedRoot));
    }

    static (int X, int Y, int Z) Bits(Vector3 value) =>
        (BitConverter.SingleToInt32Bits(value.X), BitConverter.SingleToInt32Bits(value.Y), BitConverter.SingleToInt32Bits(value.Z));

    static (int X, int Y, int Z, int W) Bits(Quaternion value) =>
        (BitConverter.SingleToInt32Bits(value.X), BitConverter.SingleToInt32Bits(value.Y),
            BitConverter.SingleToInt32Bits(value.Z), BitConverter.SingleToInt32Bits(value.W));

    static bool SamePose(RigidPose actual, Pose expected) =>
        Bits(actual.Position) == Bits(expected.Position) && Bits(actual.Orientation) == Bits(expected.Orientation);

    static void AssertPose(RigidPose actual, Pose expected)
    {
        Assert.Equal(Bits(expected.Position), Bits(actual.Position));
        Assert.Equal(Bits(expected.Orientation), Bits(actual.Orientation));
    }

    internal void CheckTargetRay(Vector3 origin, Vector3 direction)
    {
        Lease.AssertCurrent();
        Assert.True(View.Raycast(origin, Vector3.Normalize(direction), 2, out RayHit hit, QueryFilter.StaticsOnly));
        Assert.Equal(Target, hit.Body);
        Lease.AssertCurrent();
    }

    internal CapsuleFeatureResult Query(Vector3 proposal, CapsuleIncidentFace[] faces)
    {
        CapsuleIncidentFace[] before = (CapsuleIncidentFace[])faces.Clone();
        try
        {
            return Features.QueryCapsuleFeature(Lease, Target, new CapsuleShape(0.25f, 0), Pose.At(proposal),
                0.001f, faces, QueryFilter.StaticsOnly);
        }
        catch
        {
            Assert.Equal(before, faces);
            throw;
        }
    }

    internal void AssertComplete(Vector3 proposal, params Face[] expected)
    {
        foreach (Face face in expected) AssertContactBand(face);
        CapsuleIncidentFace[] faces = Sentinels(), before = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = Query(proposal, faces);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Same(View, result.QueryWorld);
        Assert.Same(World, result.SourceWorld);
        Assert.Same(Lease, result.Lease);
        Assert.Equal(Lease.Origin, result.Origin);
        Assert.Equal(Lease.GeometryGeneration, result.GeometryGeneration);
        Assert.Equal(Target, result.Target);
        Assert.Equal(0, result.LeafId);
        Assert.True(result.FeatureId >= 0);
        Assert.Equal(CapsuleFeatureKind.FaceInterior, result.Kind);
        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.RequiredCapacity);
        Assert.True(faces[0].FaceId >= 0);
        Assert.Equal(CapsuleFeatureKind.FaceInterior, faces[0].Incidence);
        AssertPublication(result, faces[0], expected);
        for (int i = result.Written; i < faces.Length; i++) Assert.Equal(before[i], faces[i]);
        Features.AssertFeatureCurrent(result, Lease);
        Lease.AssertCurrent();
    }

    internal void AssertRefused(Vector3 proposal, CapsuleFeatureStatus status)
    {
        CapsuleIncidentFace[] faces = Sentinels(), before = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = Query(proposal, faces);
        Assert.Equal(status, result.Status);
        Assert.Equal(before, faces);
        Assert.Equal(0, result.Written);
        Assert.Equal(0, result.RequiredCapacity);
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
        Lease.AssertCurrent();
    }

    static CapsuleIncidentFace[] Sentinels() =>
    [
        new(700, new Vector3(7, -11, 13), 0.125f, CapsuleFeatureKind.Vertex),
        new(701, new Vector3(7, -11, 13), 0.125f, CapsuleFeatureKind.Vertex),
        new(702, new Vector3(7, -11, 13), 0.125f, CapsuleFeatureKind.Vertex),
        new(703, new Vector3(7, -11, 13), 0.125f, CapsuleFeatureKind.Vertex),
    ];

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
