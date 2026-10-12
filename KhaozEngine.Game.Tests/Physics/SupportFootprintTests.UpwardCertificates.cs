using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using Proof = KhaozEngine.Tests.Physics.SupportFootprintCurvedProofOracle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void CurvedExactBoundaryPublicationKeepsIndependentUpwardCertificate()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CapsuleShape(0.25f, 1),
            new(Vector3.Zero, new Quaternion(0, 0, 0.5f, MathF.Sqrt(3) / 2)));
        Proof.InstalledPart capsule = Assert.Single(Proof.Installed(scene.World, owner));
        Proof.InstalledPart side = capsule with { Tangent = capsule.Tangent with { Surface = OracleSurface.CylinderSide } };
        var request = Request(z: 0.29f, cosine: 0.8f);
        Proof.Boundary exact = Proof.WalkableBoundary(side, request);
        Assert.True(exact.Derivative.Upper < R.Zero);
        Assert.True(exact.AxialCoordinate.Lower > -side.Half && exact.AxialCoordinate.Upper < side.Half);
        Assert.Equal(R.From(request.CosMaxSlope), exact.Normal.Y.Lower);
        Assert.Equal(exact.Normal.Y.Lower, exact.Normal.Y.Upper);
        GeometryVector Enclose(P point)
        {
            GeometryInterval Component(B bounds)
            {
                var published = Proof.Publish(bounds);
                return GeometryInterval.Enclose(published.Lower, published.Upper);
            }
            return new(Component(point.X), Component(point.Y), Component(point.Z));
        }
        Assert.True(SupportFootprintPublication.TryWitness(SupportWitnessRole.Footprint,
            Enclose(exact.Witness), Enclose(exact.Normal), out var witness));
        ContainsCurvedVector(witness.Normal, witness.NormalError, exact.Normal);
        ContainsCurvedVector(witness.Position, witness.PositionErrorMetres, exact.Witness);
        Assert.True(witness.NormalError > 0);
        Assert.Equal((double)request.CosMaxSlope, witness.UpwardLower);
        Assert.True(witness.UpwardLower > (double)witness.Normal.Y - witness.NormalError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PolygonUpwardCertificateKeepsSourceBackedSlopeEquality(bool installed)
    {
        using var scene = new SupportFootprintScene();
        var request = Request(cosine: 1);
        if (installed)
            scene.Install(new BoxShape(new(1, 0.2f, 1)), new(new(0, -0.1f, 0),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.37f)));
        else
            scene.Install(new BoxShape(new(1, 0.2f, 1)), Pose.At(new(0, -0.1f, 0)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, new ulong[1024]);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var top = Assert.Single(members.Take(result.Members), member => member.HasDiscMaximum);
        Assert.Equal(1, top.UpwardLower);
        Assert.Equal(top.UpwardLower, top.First.UpwardLower);
    }

    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CurvedCrestUpwardCertificateIsIndependentOfNormalError(string shape)
    {
        using var scene = new SupportFootprintScene();
        PhysicsShape solid = shape switch
        {
            "sphere" => new SphereShape(0.25f),
            "capsule" => new CapsuleShape(0.25f, 1),
            _ => new CylinderShape(0.25f, 1)
        };
        scene.Install(solid, Pose.At(new(0, -0.25f, 0)));
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var result = Capability(scene.World).QuerySupportFootprint(lease, Request(cosine: 1), members, new ulong[1024]);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var top = Assert.Single(members.Take(result.Members), member => member.HasDiscMaximum);
        Assert.Equal(1, top.UpwardLower);
        Assert.Equal(1, top.First.UpwardLower);
        Assert.Equal(1, top.Second.UpwardLower);
    }
}
