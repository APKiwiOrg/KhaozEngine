using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Movement.CornerFeatureOracle;

namespace KhaozEngine.Tests.Movement;

// Real installed Bepu geometry shared by correspondence and the separately proposed consumer tests.
internal sealed class CornerFeatureControlScene : IDisposable
{
    internal BepuPhysicsWorld World { get; } = new(Vector3.Zero);
    internal IPhysicsWorldQueryView View { get; private set; } = null!;
    internal IPhysicsCapsuleFeatures Features { get; private set; } = null!;
    internal IPhysicsQueryLease Lease { get; private set; } = null!;
    internal StaticHandle Target { get; private set; }
    internal CapsuleShape Capsule { get; private set; } = new(13f / 32, 0.5f);
    internal Pose Candidate { get; private set; } = Pose.At(new Vector3(-5f / 32, 5f / 8, 0));
    internal CapsuleFeatureStatus ExpectedStatus { get; private set; }
    internal CapsuleFeatureKind ExpectedKind { get; private set; }
    internal bool ExpectedEligibility { get; private set; }
    ExactVector _axis = V(-5, 12, 0, 32), _geometry = V(0, 0, 0);
    ExactVector[] _normals = [];

    internal CornerFeatureControlScene(string fixture)
    {
        PhysicsShape shape;
        Triangle[]? triangles = null;
        RayControl[] controls;
        ExpectedStatus = CapsuleFeatureStatus.Complete;
        ExpectedKind = CapsuleFeatureKind.OpenBoundary;
        switch (fixture)
        {
            case "open":
                triangles = [Top()];
                shape = Mesh(triangles);
                controls = [TopControl()];
                _normals = [V(0, 1, 0)];
                ExpectedEligibility = true;
                break;
            case "convex":
                triangles = [Top(), new(new(0, 0, -2), new(0, 0, 2), new(0, -2, -2))];
                shape = Mesh(triangles);
                controls = [TopControl(), new(new(0, -0.5f, -1), -Vector3.UnitX)];
                _normals = [V(0, 1, 0), V(-1, 0, 0)];
                ExpectedKind = CapsuleFeatureKind.ConvexCrease;
                ExpectedEligibility = true;
                break;
            case "concave":
                triangles = [Top(), new(new(0, 0, -2), new(0, 0, 2), new(0, 2, -2))];
                shape = Mesh(triangles);
                controls = [TopControl(), new(new(0, 0.5f, -1), Vector3.UnitX)];
                // The same proposed corner is front-side to the top and strictly behind the concave wall.
                // Its mixed neighborhood has no supported one-sided closest-feature proof.
                ExpectedStatus = CapsuleFeatureStatus.Unresolved;
                break;
            case "competing":
                triangles =
                [
                    new(new(-2, 0, -2), new(-5f / 32, 0, -2), new(-5f / 32, 0, 2)),
                    new(new(5f / 32, 0, -2), new(2, 0, -2), new(5f / 32, 0, 2)),
                ];
                shape = Mesh(triangles);
                controls = [new(new(-0.5f, 0, -1), Vector3.UnitY), new(new(0.5f, 0, -1), Vector3.UnitY)];
                Candidate = Pose.At(new Vector3(0, 5f / 8, 0));
                ExactVector axis = V(0, 12, 0, 32);
                R radius = R.From(Capsule.Radius);
                Assert.Equal(radius * radius, (axis - V(-5, 0, 0, 32)).LengthSquared());
                Assert.Equal(radius * radius, (axis - V(5, 0, 0, 32)).LengthSquared());
                ExpectedStatus = CapsuleFeatureStatus.Ambiguous;
                break;
            case "underside":
                Triangle top = Top();
                triangles = [new(top.A, top.C, top.B)];
                shape = Mesh(triangles);
                controls = [new(new(0.5f, 0, -1), -Vector3.UnitY)];
                Capsule = new(0.25f, 0.5f);
                Candidate = Pose.At(new Vector3(0.5f, -0.5f, -1));
                _axis = new(new R(1, 2), new R(-1, 4), new R(-1, 1));
                _geometry = new(new R(1, 2), R.Zero, new R(-1, 1));
                _normals = [V(0, -1, 0)];
                ExpectedKind = CapsuleFeatureKind.FaceInterior;
                Assert.Equal(AxisUpper(Capsule, Candidate), _axis);
                break;
            case "wall":
                triangles = [new(new(0, -2, -2), new(0, 2, -2), new(0, -2, 2))];
                shape = Mesh(triangles);
                controls = [new(new(0, -1, -1), -Vector3.UnitX)];
                Capsule = new(0.25f, 0.5f);
                Candidate = Pose.At(new Vector3(-0.25f, -0.5f, -0.5f));
                // Both endpoint projections lie strictly inside this finite triangle, giving a continuum.
                ExpectedStatus = CapsuleFeatureStatus.Ambiguous;
                break;
            case "dome":
                shape = new SphereShape(1);
                controls = [new(new(0, 1, 0), Vector3.UnitY)];
                Capsule = new(0.25f, 0.5f);
                Candidate = Pose.At(new Vector3(0, 1.5f, 0));
                ExpectedStatus = CapsuleFeatureStatus.Unsupported;
                break;
            case "unsupported-compound":
                shape = new CompoundShape(
                    [new(new BoxShape(new Vector3(0.5f)), Pose.Identity),
                     new(new SphereShape(0.25f), Pose.At(new Vector3(3, 0, 0)))]);
                controls = [new(new(0, 0.5f, 0), Vector3.UnitY)];
                Capsule = new(0.25f, 0.5f);
                Candidate = Pose.At(new Vector3(0, 1, 0));
                ExpectedStatus = CapsuleFeatureStatus.Unsupported;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(fixture));
        }
        Install(shape, triangles, controls);
    }

    internal CornerFeatureControlScene(Triangle[] triangles, CapsuleShape capsule, Pose candidate,
        params RayControl[] controls)
    {
        Capsule = capsule;
        Candidate = candidate;
        Install(Mesh(triangles), triangles, controls);
    }

    void Install(PhysicsShape shape, Triangle[]? triangles, RayControl[] controls)
    {
        try
        {
            Target = World.AddStatic(shape, Pose.Identity);
            View = World.CreateQueryViewExcludingStatics([]);
            Features = Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(View);
            Lease = Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(View).AcquireQueryReadLease();
            Lease.AssertCurrent();
            Assert.Same(World, Lease.SourceWorld);
            Assert.Same(World, View.SourceWorld);
            Assert.Equal(Vector3.Zero, Lease.Origin);
            if (triangles is not null) AssertInstalledMesh(World, Pose.Identity, triangles);
            Assert.NotEmpty(controls);
            foreach (RayControl control in controls)
            {
                Assert.True(View.Raycast(control.Point + control.Normal * 0.125f, -control.Normal, 0.25f,
                    out RayHit hit, QueryFilter.StaticsOnly), "Ordinary installed front-side setup must pass first.");
                Assert.Equal(Target, hit.Body);
                AssertVectorWithin(hit.Point, Point(control.Point), PositionCeiling, "ordinary placement control");
                AssertVectorWithin(hit.Normal, Point(control.Normal), NormalCeiling, "ordinary front control");
            }
        }
        catch { Dispose(); throw; }
    }

    internal CapsuleFeatureResult Query(CapsuleIncidentFace[] faces) =>
        Features.QueryCapsuleFeature(Lease, Target, Capsule, Candidate, 0, faces, QueryFilter.StaticsOnly);

    internal CapsuleFeatureResult AssertCorrespondence(out CapsuleIncidentFace[] faces)
    {
        faces = Sentinels();
        CapsuleIncidentFace[] original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = Query(faces);
        if (ExpectedStatus != CapsuleFeatureStatus.Complete)
            AssertRefused(result, faces, original, ExpectedStatus);
        else
        {
            R radius = R.From(Capsule.Radius);
            Assert.Equal(radius * radius, (_axis - _geometry).LengthSquared());
            AssertComplete(View, World, Features, Lease, Target, Capsule, result, faces, original,
                ExpectedKind, _axis, _geometry, _normals);
        }
        Lease.AssertCurrent();
        return result;
    }

    internal static Triangle Top() => new(new(0, 0, -2), new(2, 0, -2), new(0, 0, 2));
    static RayControl TopControl() => new(new(0.5f, 0, -1), Vector3.UnitY);
    internal static TriangleMeshShape Mesh(Triangle[] triangles)
    {
        var vertices = new Vector3[triangles.Length * 3];
        var indices = new int[vertices.Length];
        for (int i = 0; i < triangles.Length; i++)
        {
            vertices[3 * i] = triangles[i].A;
            vertices[3 * i + 1] = triangles[i].B;
            vertices[3 * i + 2] = triangles[i].C;
        }
        for (int i = 0; i < indices.Length; i++) indices[i] = i;
        return new(vertices, indices);
    }

    public void Dispose()
    {
        try { Lease?.Dispose(); }
        finally { try { View?.Dispose(); } finally { World.Dispose(); } }
    }

    internal readonly record struct RayControl(Vector3 Point, Vector3 Normal);
}
