// Real installed feature correspondence feeds the certification. Wider enclosures below remain
// truthful supersets of actual output, but must not satisfy an unproved support threshold.
using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using static KhaozEngine.Tests.Movement.CornerFeatureOracle;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class SupportCertificationTests
{
    static readonly float CosMaxSlope = MathF.Cos(MathF.PI / 4f);

    static Vector2 Axis(CornerFeatureControlScene scene) =>
        new(scene.Candidate.Position.X, scene.Candidate.Position.Z);

    static SupportContribution Certify(CornerFeatureControlScene scene, IPhysicsCapsuleFeatures capability,
        in CapsuleFeatureResult result, ReadOnlySpan<CapsuleIncidentFace> faces) =>
        SupportCertification.Certify(capability, scene.Lease, result, faces, Axis(scene), CosMaxSlope);

    [Theory]
    [InlineData("open", true)]
    [InlineData("convex", true)]
    [InlineData("concave", false)]
    [InlineData("competing", false)]
    [InlineData("underside", false)]
    [InlineData("wall", false)]
    [InlineData("dome", false)]
    [InlineData("unsupported-compound", false)]
    public void CertificationMatchesTheActualFiniteNeighborhood(string fixture, bool walkable)
    {
        using var scene = new CornerFeatureControlScene(fixture);
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        SupportContribution contribution = Certify(scene, scene.Features, result, faces.AsSpan(0, result.Written));
        Assert.Equal(walkable ? CertifiedSupportKind.Walkable : CertifiedSupportKind.Refused, contribution.Kind);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("separation")]
    [InlineData("direction")]
    [InlineData("wall")]
    public void CertificationRefusesWidenedEvidence(string wider)
    {
        using var scene = new CornerFeatureControlScene("convex");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        Assert.Equal(CertifiedSupportKind.Walkable,
            Certify(scene, scene.Features, result, faces.AsSpan(0, result.Written)).Kind);
        CapsuleIncidentFace[] copy = faces.AsSpan(0, result.Written).ToArray();
        if (wider == "wall")
            for (int i = 0; i < copy.Length; i++)
                if (copy[i].Normal.Y == 0)
                    copy[i] = new(copy[i].FaceId, copy[i].Normal, 0.000001f, copy[i].Incidence);
        CapsuleFeatureResult broadened = CapsuleFeatureResult.Completed(scene.View, scene.Lease, scene.Target,
            result.LeafId, result.FeatureId, result.Kind, result.AxisPoint, result.GeometryPoint,
            result.SeparationNormal, wider == "separation" ? -0.0002 : result.SeparationLower,
            wider == "separation" ? 0.0002 : result.SeparationUpper,
            wider == "position" ? 0.001f : result.PositionErrorMetres,
            wider == "direction" ? 1f : result.NormalError, copy);
        // Only enclosure widths changed. No invented geometry or scripted provider establishes correspondence.
        Assert.Equal(CertifiedSupportKind.Refused, Certify(scene, scene.Features, broadened, copy).Kind);
    }

    [Fact]
    public void CertificationDoesNotReviveAnExpiredQuery()
    {
        using var scene = new CornerFeatureControlScene("open");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        Assert.Equal(CertifiedSupportKind.Walkable,
            Certify(scene, scene.Features, result, faces.AsSpan(0, result.Written)).Kind);
        scene.Lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            Certify(scene, scene.Features, result, faces.AsSpan(0, result.Written)));
    }

    [Fact]
    public void CertificationRequiresTheOriginalReceiver()
    {
        using var scene = new CornerFeatureControlScene("open");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        Assert.Throws<InvalidOperationException>(() =>
            Certify(scene, scene.World, result, faces.AsSpan(0, result.Written)));
    }

    [Fact]
    public void CoplanarRawFacesSupportAsOnePatch()
    {
        CornerFeatureOracle.Triangle[] triangles =
        [
            CornerFeatureControlScene.Top(),
            new(new(2, 0, -2), new(2, 0, 2), new(0, 0, 2)),
        ];
        using var scene = new CornerFeatureControlScene(triangles, new CapsuleShape(0.25f, 0.5f),
            Pose.At(new Vector3(1, 0.5f, 0)),
            new CornerFeatureControlScene.RayControl(new(0.5f, 0, -1), Vector3.UnitY));
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = scene.Query(faces);
        AssertComplete(scene.View, scene.World, scene.Features, scene.Lease, scene.Target, scene.Capsule,
            result, faces, original, CapsuleFeatureKind.FaceInterior, V(4, 1, 0, 4), V(1, 0, 0),
            [V(0, 1, 0), V(0, 1, 0)]);
        SupportContribution contribution = Certify(scene, scene.Features, result, faces.AsSpan(0, result.Written));
        Assert.Equal(CertifiedSupportKind.Walkable, contribution.Kind);
        Assert.True(contribution.Lower <= 0 && 0 <= contribution.Upper);
    }

    [Fact]
    public void RidgeOfTwoWalkableTopsSupportsTheRidgeHeight()
    {
        using var scene = new CornerFeatureControlScene("ridge");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        Assert.Equal(2, result.Written);
        for (int i = 0; i < result.Written; i++)
            Assert.True(R.From(faces[i].Normal.Y) - R.From(faces[i].NormalError) >= R.From(CosMaxSlope));
        SupportContribution contribution = Certify(scene, scene.Features, result, faces.AsSpan(0, result.Written));
        Assert.Equal(CertifiedSupportKind.Walkable, contribution.Kind);
        // The ridge line is the fixture's shared edge at Y 0.
        Assert.True(contribution.Lower <= 0 && 0 <= contribution.Upper);
        Assert.Equal(result.FeatureId, contribution.FeatureId);
    }

    [Fact]
    public void ConvexStepEdgeContributesTheTopPlaneAtTheAxis()
    {
        using var scene = new CornerFeatureControlScene("convex");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        SupportContribution contribution = Certify(scene, scene.Features, result, faces.AsSpan(0, result.Written));
        Assert.Equal(CertifiedSupportKind.Walkable, contribution.Kind);
        // The top triangle lies at Y 0. The axis sits outside it, over the wall side of the edge.
        Assert.True(contribution.Lower <= 0 && 0 <= contribution.Upper);
        Assert.True(contribution.Upper - contribution.Lower <= 0.0005);
        Assert.True(contribution.Normal.Y > 0.99f);
        Assert.Equal(result.GeometryPoint, contribution.Witness);
    }

    [Fact]
    public void SteepFaceInteriorIsSteepNotRefused()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        // Rotating 60 degrees about Z tilts the top face normal to (-sin 60, cos 60, 0).
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3f);
        StaticHandle box = world.AddStatic(new BoxShape(new Vector3(2, 0.5f, 2)), new Pose(Vector3.Zero, tilt));
        var capsule = new CapsuleShape(0.25f, 0.5f);
        Vector3 top = Vector3.Transform(new Vector3(0, 0.5f, 0), tilt);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        Assert.True(world.SweepCapsule(capsule, Pose.At(new Vector3(top.X, 3, top.Z)), -Vector3.UnitY, 5,
            out SweepHit hit, QueryFilter.StaticsOnly));
        Assert.Equal(box, hit.Body);
        Pose settled = Pose.At(new Vector3(top.X, 3 - hit.Distance, top.Z));
        CapsuleIncidentFace[] faces = Sentinels();
        CapsuleFeatureResult result = world.QueryCapsuleFeature(lease, box, capsule, settled,
            SupportCertification.ContactBand, faces, QueryFilter.StaticsOnly);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        Assert.Equal(CapsuleFeatureKind.FaceInterior, result.Kind);
        SupportContribution contribution = SupportCertification.Certify(world, lease, result,
            faces.AsSpan(0, result.Written), new Vector2(top.X, top.Z), CosMaxSlope);
        Assert.Equal(CertifiedSupportKind.Steep, contribution.Kind);
        // The installed top plane through the float quaternion, evaluated in double at the axis.
        (double X, double Y, double Z) Rotate(double x, double y, double z)
        {
            double qx = tilt.X, qy = tilt.Y, qz = tilt.Z, qw = tilt.W;
            double tx = 2 * (qy * z - qz * y), ty = 2 * (qz * x - qx * z), tz = 2 * (qx * y - qy * x);
            return (x + qw * tx + (qy * tz - qz * ty), y + qw * ty + (qz * tx - qx * tz),
                z + qw * tz + (qx * ty - qy * tx));
        }
        var centre = Rotate(0, 0.5, 0);
        var normal = Rotate(0, 1, 0);
        double plane = centre.Y - (normal.X * (top.X - centre.X) + normal.Z * (top.Z - centre.Z)) / normal.Y;
        Assert.True(contribution.Lower <= plane && plane <= contribution.Upper,
            $"Plane {plane:R} outside [{contribution.Lower:R}, {contribution.Upper:R}]");
    }
}
