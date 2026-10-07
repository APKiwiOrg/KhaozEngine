using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Structural publication checks only. Scripted witnesses do not certify backend geometry.
public class CapsuleFeatureCompletedTests
{
    delegate CapsuleFeatureResult CompleteCall(IPhysicsWorld queryWorld, IPhysicsQueryLease lease,
        StaticHandle target, int leafId, int featureId, CapsuleFeatureKind kind, Vector3 axisPoint,
        Vector3 geometryPoint, Vector3 separationNormal, double separationLower, double separationUpper,
        float positionErrorMetres, float normalError, ReadOnlySpan<CapsuleIncidentFace> faces);

    [Fact]
    public void CompleteRetainsTheExactReceiverAndOriginalLeaseMetadata()
    {
        using var scene = new Scene();
        CapsuleFeatureResult result = scene.Publish();
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Same(scene.View, result.QueryWorld);
        Assert.Same(scene.World, result.SourceWorld);
        Assert.Same(scene.Lease, result.Lease);
        Assert.Equal(scene.Lease.Origin, result.Origin);
        Assert.Equal(scene.Lease.GeometryGeneration, result.GeometryGeneration);
        Assert.Equal(scene.Target, result.Target);
        Assert.Equal(3, result.LeafId);
        Assert.Equal(7, result.FeatureId);
        Assert.Equal(CapsuleFeatureKind.FaceInterior, result.Kind);
        Assert.Equal(Vector3.UnitY, result.AxisPoint);
        Assert.Equal(Vector3.Zero, result.GeometryPoint);
        Assert.Equal(Vector3.UnitY, result.SeparationNormal);
        Assert.Equal(-0.00001, result.SeparationLower);
        Assert.Equal(0.00001, result.SeparationUpper);
        Assert.Equal(0.00002f, result.PositionErrorMetres);
        Assert.Equal(0.000001f, result.NormalError);
        Assert.Equal(1, result.Written);
        Assert.Equal(0, result.RequiredCapacity);
    }

    [Fact]
    public void CompleteCountsAllIncidentFacesWithoutKeepingTheCallerArray()
    {
        using var scene = new Scene();
        CapsuleIncidentFace[] faces = [Face(0), Face(1, Vector3.UnitX)];
        CapsuleFeatureResult result = scene.Publish(kind: CapsuleFeatureKind.ConvexCrease, faces: faces);
        faces[0] = default;
        Assert.Equal(2, result.Written);
        Assert.Equal(CapsuleFeatureKind.ConvexCrease, result.Kind);
    }

    [Fact]
    public void CompleteRejectsInvertedAndNonfiniteSeparationBounds()
    {
        using var scene = new Scene();
        foreach ((double lower, double upper) in new[] { (1d, 0d), (double.NaN, 0d), (0d, double.PositiveInfinity) })
            Assert.ThrowsAny<ArgumentException>(() => scene.Publish(lower: lower, upper: upper));
    }

    [Fact]
    public void CompleteRejectsNonfiniteWitnessesAndZeroOrNonfiniteNormal()
    {
        using var scene = new Scene();
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(axis: new Vector3(float.NaN, 0, 0)));
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(geometry: new Vector3(0, float.PositiveInfinity, 0)));
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(normal: Vector3.Zero));
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(normal: new Vector3(0, 0, float.NaN)));
    }

    [Fact]
    public void CompleteRejectsNegativeAndNonfiniteErrorBounds()
    {
        using var scene = new Scene();
        foreach (float error in new[] { -float.Epsilon, float.NaN, float.PositiveInfinity })
        {
            Assert.ThrowsAny<ArgumentException>(() => scene.Publish(positionError: error));
            Assert.ThrowsAny<ArgumentException>(() => scene.Publish(normalError: error));
        }
    }

    [Fact]
    public void CompleteRejectsMissingFacesDuplicateIdsAndInvalidFaceFields()
    {
        using var scene = new Scene();
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(faces: []));
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(faces: [Face(0), Face(0)]));
        foreach (CapsuleIncidentFace face in new[]
        {
            Face(-1), Face(0, Vector3.Zero), Face(0, new Vector3(float.NaN, 0, 0)),
            new(0, Vector3.UnitY, -1, CapsuleFeatureKind.FaceInterior),
            new(0, Vector3.UnitY, float.PositiveInfinity, CapsuleFeatureKind.FaceInterior),
            new(0, Vector3.UnitY, 0, CapsuleFeatureKind.None),
            new(0, Vector3.UnitY, 0, (CapsuleFeatureKind)255)
        })
            Assert.ThrowsAny<ArgumentException>(() => scene.Publish(faces: [face]));
    }

    [Fact]
    public void CompleteRejectsInvalidLocalIdentitiesAndKind()
    {
        using var scene = new Scene();
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(leafId: -1));
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(featureId: -1));
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(kind: CapsuleFeatureKind.None));
        Assert.ThrowsAny<ArgumentException>(() => scene.Publish(kind: (CapsuleFeatureKind)255));
    }

    [Fact]
    public void ExpiredOriginalLeaseCannotPublishNewOutput()
    {
        using var scene = new Scene();
        CompleteCall complete = Bind();
        scene.Lease.Dispose();
        Assert.ThrowsAny<InvalidOperationException>(() => complete(scene.View, scene.Lease, scene.Target,
            0, 0, CapsuleFeatureKind.FaceInterior, Vector3.UnitY, Vector3.Zero, Vector3.UnitY,
            0, 0, 0, 0, new[] { Face(0) }));
    }

    static CapsuleIncidentFace Face(int id, Vector3? normal = null) =>
        new(id, normal ?? Vector3.UnitY, 0, CapsuleFeatureKind.FaceInterior);

    static CompleteCall Bind()
    {
        MethodInfo? method = typeof(CapsuleFeatureResult).GetMethod("Completed", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return method.CreateDelegate<CompleteCall>();
    }

    sealed class Scene : IDisposable
    {
        internal readonly BepuPhysicsWorld World = new(Vector3.Zero);
        internal readonly IPhysicsWorldQueryView View;
        internal readonly IPhysicsQueryLease Lease;
        internal readonly StaticHandle Target;

        internal Scene()
        {
            Target = World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
            World.Rebase(new Vector3(32, 0, -32));
            View = World.CreateQueryViewExcludingStatics([]);
            Lease = ((IPhysicsQueryLeaseSource)View).AcquireQueryReadLease();
            Assert.Same(World, Lease.SourceWorld);
            Lease.AssertCurrent();
        }

        internal CapsuleFeatureResult Publish(int leafId = 3, int featureId = 7,
            CapsuleFeatureKind kind = CapsuleFeatureKind.FaceInterior, Vector3? axis = null,
            Vector3? geometry = null, Vector3? normal = null, double lower = -0.00001,
            double upper = 0.00001, float positionError = 0.00002f, float normalError = 0.000001f,
            CapsuleIncidentFace[]? faces = null) =>
            Bind()(View, Lease, Target, leafId, featureId, kind, axis ?? Vector3.UnitY,
                geometry ?? Vector3.Zero, normal ?? Vector3.UnitY, lower, upper, positionError,
                normalError, faces ?? new[] { Face(0) });

        public void Dispose()
        {
            Lease.Dispose();
            View.Dispose();
            World.Dispose();
        }
    }
}
