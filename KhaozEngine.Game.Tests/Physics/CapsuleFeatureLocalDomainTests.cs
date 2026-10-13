using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class CapsuleFeatureLocalDomainTests
{
    [Theory]
    [InlineData(2048f, CapsuleFeatureStatus.Complete, true)]
    [InlineData(2048.000244140625f, CapsuleFeatureStatus.Unsupported, false)]
    public void LocalVertexAdmissionUsesTheClosedRangeDomain(float magnitude, CapsuleFeatureStatus expected,
        bool correspondenceResolved)
    {
        Assert.True(InstalledPoseOperator.TryCreate(new(Vector3.Zero, Quaternion.Identity), out var transform));
        Vector3 vertex = new(magnitude, -magnitude, magnitude);
        Assert.Equal(correspondenceResolved, RepresentedGeometryTransforms.PosePoint(Pose.Identity, vertex).IsResolved);
        Assert.Equal(expected, CapsuleFeatureGeometry.TransformVertex(vertex, Pose.Identity, transform, out _));
    }

    [Fact]
    public void AdmittedLocalVertexStillRefusesAnOutOfDomainWorldEnclosure()
    {
        var pose = Pose.At(Vector3.UnitX);
        Assert.True(InstalledPoseOperator.TryCreate(new(pose.Position, pose.Orientation), out var transform));
        Vector3 vertex = new(2048, 0, 0);
        Assert.True(RepresentedGeometryTransforms.PosePoint(pose, vertex).IsResolved);
        Assert.Equal(CapsuleFeatureStatus.Unsupported,
            CapsuleFeatureGeometry.TransformVertex(vertex, pose, transform, out _));
    }

    [Fact]
    public void AdmittedRotatedBoxCornerRefusesTheUnchangedNormalCeiling()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var pose = new Pose(Vector3.Zero, new(0.6f, 0.8f, 0, 0));
        var target = world.AddStatic(new BoxShape(new(128, 1, 1)), pose);
        Assert.True(InstalledPoseOperator.TryCreate(new(pose.Position, pose.Orientation), out var transform));
        for (int i = 0; i < 8; i++)
        {
            Vector3 vertex = new((i & 1) == 0 ? -128 : 128, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
            Assert.Equal(CapsuleFeatureStatus.Complete,
                CapsuleFeatureGeometry.TransformVertex(vertex, pose, transform, out _));
        }
        Assert.Equal(CapsuleFeatureStatus.Complete,
            CapsuleFeatureGeometry.TransformVertex(new(-128, 1, 1), pose, transform, out var corner));
        // These represented coordinates are the independent blocking witness in the range proof.
        Vector3 axis = new(36.81000518798828125f, -122.600006103515625f, -1.0010001659393310546875f);
        FeaturePoint axisPoint = FeaturePoint.Exact(axis);
        FeaturePoint delta = FeaturePoint.Subtract(axisPoint, corner);
        var axisOutput = GeometryVectorOperations.Publish(axisPoint.Bounds);
        var cornerOutput = GeometryVectorOperations.Publish(corner.Bounds);
        var normalOutput = GeometryVectorOperations.Publish(GeometryVectorOperations.Normalize(delta.Bounds));
        Assert.True(axisOutput.IsResolved && (double)axisOutput.Error * 4000 <= 1);
        Assert.True(cornerOutput.IsResolved && (double)cornerOutput.Error * 4000 <= 1);
        Assert.True(normalOutput.IsResolved && (double)normalOutput.Error * 100000 > 1);
        using var lease = world.AcquireQueryReadLease();
        CapsuleIncidentFace sentinel = new(123, Vector3.UnitY, 0, CapsuleFeatureKind.FaceInterior);
        CapsuleIncidentFace[] faces = [sentinel, sentinel, sentinel];
        var published = CapsuleFeatureGeometryQuery.Publish(0, 6, CapsuleFeatureKind.Vertex, axisPoint, corner,
            FeaturePoint.Dot(delta, delta), [0], [FeaturePoint.Exact(Vector3.UnitY)], 256, world, lease, target,
            FeatureNumber.Exact(0.01f), faces);
        Assert.Equal(CapsuleFeatureStatus.Unresolved, published.Status);
        Assert.Equal(0, published.Written);
        var result = world.QueryCapsuleFeature(lease, target, new CapsuleShape(0.01f, 0), Pose.At(axis), 0.0001f,
            faces, QueryFilter.StaticsOnly);
        Assert.Equal(CapsuleFeatureStatus.Unresolved, result.Status);
        Assert.Equal(0, result.Written);
        Assert.All(faces, face => Assert.Equal(sentinel, face));
    }
}
