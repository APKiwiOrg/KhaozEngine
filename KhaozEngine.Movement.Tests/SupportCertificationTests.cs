// Real installed support neighborhoods feed the certification. Wider enclosures below remain truthful supersets
// of actual output, so they must widen the certified interval or drop the member, never narrow it or invent support.
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
    const int Capacity = SupportNeighborhoodResult.MaximumElements;

    sealed record Neighborhood(SupportNeighborhoodResult Result, SupportElement[] Elements, ulong[] Joins);

    static Vector2 Axis(Pose pose) => new(pose.Position.X, pose.Position.Z);

    static Neighborhood Query(IPhysicsSupportNeighborhood features, IPhysicsQueryLease lease, CapsuleShape probe,
        Pose pose)
    {
        var elements = new SupportElement[Capacity];
        var joins = new ulong[Capacity * SupportNeighborhoodResult.JoinWordsFor(Capacity)];
        SupportNeighborhoodResult result = features.QuerySupportNeighborhood(lease, probe, pose,
            SupportCertification.ContactBand, elements, joins, QueryFilter.StaticsOnly);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        return new(result, elements[..result.Elements], joins);
    }

    static Neighborhood Query(CornerFeatureControlScene scene) =>
        Query(scene.Neighborhoods, scene.Lease, scene.Capsule, scene.Candidate);

    static SupportContribution[] Certify(CornerFeatureControlScene scene, IPhysicsSupportNeighborhood capability,
        Neighborhood neighborhood, SupportElement[]? elements = null)
    {
        var contributions = new SupportContribution[Capacity];
        int count = SupportCertification.CertifyNeighborhood(capability, scene.Lease, neighborhood.Result,
            elements ?? neighborhood.Elements, neighborhood.Joins, Axis(scene.Candidate), CosMaxSlope, contributions);
        Assert.Equal(neighborhood.Result.Elements, count);
        return contributions[..count];
    }

    static int IndexOfTop(SupportElement[] elements) =>
        Array.FindIndex(elements, e => e.Normal.Y > 0.99f);

    static bool Encloses(in SupportContribution contribution, double height) =>
        contribution.Lower <= height && height <= contribution.Upper;

    [Theory]
    [InlineData("position")]
    [InlineData("separation")]
    [InlineData("direction")]
    [InlineData("wall")]
    public void WidenedEvidenceWidensOrDropsTheContribution(string wider)
    {
        using var scene = new CornerFeatureControlScene("convex");
        Neighborhood neighborhood = Query(scene);
        SupportContribution[] original = Certify(scene, scene.Neighborhoods, neighborhood);
        int top = IndexOfTop(neighborhood.Elements);
        Assert.Equal(CertifiedSupportKind.Walkable, original[top].Kind);
        SupportElement[] copy = (SupportElement[])neighborhood.Elements.Clone();
        for (int i = 0; i < copy.Length; i++)
            copy[i] = wider switch
            {
                "position" => copy[i] with { PositionErrorMetres = 0.001f },
                "separation" => copy[i] with { SeparationLower = -0.0002, SeparationUpper = 0.0002 },
                "direction" => copy[i] with { NormalError = 1f },
                _ => copy[i].Normal.Y == 0 ? copy[i] with { NormalError = 0.000001f } : copy[i],
            };
        // Only enclosure widths changed. No invented geometry or scripted provider establishes correspondence.
        SupportContribution[] widened = Certify(scene, scene.Neighborhoods, neighborhood, copy);
        switch (wider)
        {
            case "position":
                // The witness height alone spans 0.002, and the plane at the axis carries the same witness box.
                Assert.Equal(CertifiedSupportKind.Walkable, widened[top].Kind);
                Assert.True(widened[top].Lower <= original[top].Lower && original[top].Upper <= widened[top].Upper);
                Assert.True(widened[top].Upper - widened[top].Lower >= 0.0019, $"{widened[top]}");
                break;
            case "direction":
                // A normal that may point anywhere has no upward bound, so no member supports anything.
                Assert.All(widened, c => Assert.Equal(CertifiedSupportKind.Refused, c.Kind));
                break;
            default:
                // Separation decides membership only, and a wall that may lean back supports nothing either way.
                Assert.Equal(original, widened);
                break;
        }
    }

    [Fact]
    public void NonFiniteMemberRefusesTheNeighborhood()
    {
        using var scene = new CornerFeatureControlScene("open");
        Neighborhood neighborhood = Query(scene);
        SupportElement[] copy = (SupportElement[])neighborhood.Elements.Clone();
        copy[0] = copy[0] with { Witness = new Vector3(float.NaN, 0, 0) };
        Assert.Equal(-1, SupportCertification.CertifyNeighborhood(scene.Neighborhoods, scene.Lease, neighborhood.Result,
            copy, neighborhood.Joins, Axis(scene.Candidate), CosMaxSlope, new SupportContribution[Capacity]));
    }

    [Fact]
    public void CertificationRejectsShortSpans()
    {
        using var scene = new CornerFeatureControlScene("convex");
        Neighborhood neighborhood = Query(scene);
        Assert.True(neighborhood.Result.Elements > 1);
        Assert.Throws<ArgumentException>(() => SupportCertification.CertifyNeighborhood(scene.Neighborhoods, scene.Lease,
            neighborhood.Result, neighborhood.Elements, neighborhood.Joins, Axis(scene.Candidate), CosMaxSlope,
            new SupportContribution[1]));
    }

    [Fact]
    public void CertificationDoesNotReviveAnExpiredNeighborhood()
    {
        using var scene = new CornerFeatureControlScene("open");
        Neighborhood neighborhood = Query(scene);
        Assert.Equal(CertifiedSupportKind.Walkable, Certify(scene, scene.Neighborhoods, neighborhood)[0].Kind);
        scene.Lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Certify(scene, scene.Neighborhoods, neighborhood));
    }

    [Fact]
    public void CertificationRequiresTheOriginalReceiver()
    {
        using var scene = new CornerFeatureControlScene("open");
        Neighborhood neighborhood = Query(scene);
        Assert.Throws<InvalidOperationException>(() => Certify(scene, scene.World, neighborhood));
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
        // The probe touches the shared diagonal, so both raw triangles are members and both support Y 0.
        Neighborhood neighborhood = Query(scene);
        SupportContribution[] contributions = Certify(scene, scene.Neighborhoods, neighborhood);
        Assert.Equal(2, contributions.Length);
        Assert.All(contributions, c => Assert.Equal(CertifiedSupportKind.Walkable, c.Kind));
        Assert.All(contributions, c => Assert.True(Encloses(c, 0), $"{c}"));
    }

    [Fact]
    public void RidgeOfTwoWalkableTopsSupportsTheRidgeHeight()
    {
        using var scene = new CornerFeatureControlScene("ridge");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        Assert.Equal(2, result.Written);
        for (int i = 0; i < result.Written; i++)
            Assert.True(R.From(faces[i].Normal.Y) - R.From(faces[i].NormalError) >= R.From(CosMaxSlope));
        Neighborhood neighborhood = Query(scene);
        Assert.Equal(2, neighborhood.Result.Elements);
        // The two tops meet at the convex ridge, so they are joined.
        Assert.NotEqual(0UL, neighborhood.Joins[0] & 2UL);
        SupportContribution[] contributions = Certify(scene, scene.Neighborhoods, neighborhood);
        for (int i = 0; i < contributions.Length; i++)
        {
            Assert.Equal(CertifiedSupportKind.Walkable, contributions[i].Kind);
            // The ridge line is the fixture's shared edge at Y 0, under the axis.
            Assert.True(Encloses(contributions[i], 0), $"{contributions[i]}");
            Assert.Equal(neighborhood.Elements[i].ElementId, contributions[i].FeatureId);
        }
    }

    [Fact]
    public void ConvexStepEdgeContributesTheTopPlaneAtTheAxis()
    {
        using var scene = new CornerFeatureControlScene("convex");
        Neighborhood neighborhood = Query(scene);
        SupportContribution[] contributions = Certify(scene, scene.Neighborhoods, neighborhood);
        int top = IndexOfTop(neighborhood.Elements);
        SupportContribution contribution = contributions[top];
        Assert.Equal(CertifiedSupportKind.Walkable, contribution.Kind);
        // The top triangle lies at Y 0. The axis sits outside it, over the wall side of the edge.
        Assert.True(Encloses(contribution, 0), $"{contribution}");
        Assert.True(contribution.Upper - contribution.Lower <= 0.0005);
        Assert.True(contribution.Normal.Y > 0.99f);
        Assert.Equal(neighborhood.Elements[top].Witness, contribution.Witness);
        // The vertical wall has no bounded plane at the axis and contributes nothing.
        for (int i = 0; i < contributions.Length; i++)
            if (i != top) Assert.Equal(CertifiedSupportKind.Refused, contributions[i].Kind);
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
        Neighborhood neighborhood = Query(world, lease, capsule, settled);
        var contributions = new SupportContribution[Capacity];
        int count = SupportCertification.CertifyNeighborhood(world, lease, neighborhood.Result,
            neighborhood.Elements, neighborhood.Joins, Axis(settled), CosMaxSlope, contributions);
        Assert.Equal(neighborhood.Result.Elements, count);
        SupportContribution contribution = Assert.Single(contributions[..count],
            c => c.Kind != CertifiedSupportKind.Refused);
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
        Assert.True(Encloses(contribution, plane),
            $"Plane {plane:R} outside [{contribution.Lower:R}, {contribution.Upper:R}]");
    }
}
