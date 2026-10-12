using System;
using System.Linq;
using System.Numerics;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedTiltedThirtyDegreeFootprintPublishesProvedRoles(bool capsule)
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(capsule ? new CapsuleShape(0.3f, 2) : new CylinderShape(0.3f, 2),
            new(new(0, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)));
        Proof.InstalledPart[] parts = Proof.Installed(scene.World, owner).ToArray();
        var request = Request(x: (float)parts[0].Tangent.Centre.X,
            z: (float)parts[0].Tangent.Centre.Z + 0.1f, lower: -2, upper: 2);
        FootprintOracleMember[] expected = parts.Select(p => SupportFootprintOracle.Curved(p.Tangent, request))
            .OfType<FootprintOracleMember>().ToArray();
        Assert.Contains(expected, member => member.Witnesses.Any(w => w.Role == SupportWitnessRole.Footprint));
        foreach (var part in parts)
        {
            var reading = SupportFootprintOracle.PerPointSupremum(part.Tangent, request);
            _output.WriteLine($"kind={capsule} part={part.Tangent.ElementId} perPoint=[{reading.Lower:R}, {reading.Upper:R}]");
        }
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var actual = members.Take(result.Members).Where(m => m.Static == owner).OrderBy(m => m.ElementId).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++) CheckTiltedMember(actual[i], expected[i]);
        _output.WriteLine(TiltedStatistics(scene.World));
    }

    [Fact]
    public void CurvedTiltedExactBoundaryCarriesRequestCosine()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CapsuleShape(0.25f, 1),
            new(Vector3.Zero, new Quaternion(0, 0, 0.5f, MathF.Sqrt(3) / 2)));
        Proof.InstalledPart capsule = Assert.Single(Proof.Installed(scene.World, owner));
        Proof.InstalledPart side = capsule with { Tangent = capsule.Tangent with { Surface = OracleSurface.CylinderSide } };
        var request = Request(z: 0.29f, cosine: 0.8f);
        Proof.Boundary exact = Proof.WalkableBoundary(side, request);
        Assert.Equal(R.From(request.CosMaxSlope), exact.Normal.Y.Lower);
        Assert.Equal(exact.Normal.Y.Lower, exact.Normal.Y.Upper);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, new ulong[1024]);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var member = Assert.Single(members.Take(result.Members), m => m.Static == owner);
        var witness = member.First.Role == SupportWitnessRole.Footprint ? member.First : member.Second;
        ContainsCurvedVector(witness.Normal, witness.NormalError, exact.Normal);
        ContainsCurvedVector(witness.Position, witness.PositionErrorMetres, exact.Witness);
        Assert.Equal((double)request.CosMaxSlope, witness.UpwardLower);
    }
}
