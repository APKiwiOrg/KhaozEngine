using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using Proof = KhaozEngine.Tests.Physics.SupportFootprintCurvedProofOracle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CapsuleCrownAttainsTheGlobalHeightBeforeSideIsolation(bool aboutX, bool inverted)
    {
        using var scene = new SupportFootprintScene();
        float angle = inverted ? MathF.BitIncrement(MathF.PI / 2) : MathF.PI / 2;
        StaticHandle owner = InstallLyingLog(scene, true, 0.3f,
            Quaternion.CreateFromAxisAngle(aboutX ? Vector3.UnitX : Vector3.UnitZ, angle));
        var part = Assert.Single(Proof.Installed(scene.World, owner));
        int highEnd = part.Axis.Y.Lower > R.Zero ? 1 : -1;
        Assert.True(highEnd > 0 ? part.Axis.Y.Lower > R.Zero : part.Axis.Y.Upper < R.Zero);
        B end = (B)new R(highEnd, 1) * (B)part.Half;
        B projected = (aboutX ? part.Axis.Z : part.Axis.X) * end;
        float along = MathF.CopySign(0.7f, (float)projected.Middle);
        var query = new FootSupportQuery(aboutX ? new(0, along) : new(along, 0), 0.6f, 0.2f,
            0.4f, 0.4f, ContactFollowupTraversalTests.CosMaxSlope);
        var request = FootSupport.DescribeFootprint(query);
        var exact = Proof.Crest(part, request);
        Assert.Equal(1, CrownDiscClassification(exact.Witness, request));
        Assert.True(highEnd > 0 ? exact.Normal.Y.Lower * part.Axis.Y.Lower > R.Zero :
            exact.Normal.Y.Upper * part.Axis.Y.Upper < R.Zero);
        var work = NewCurvedWork();
        var geometry = new SupportTiltedCurvedGeometry(work);
        Assert.True(geometry.Begin(CrownLeaf(scene, owner), query.Axis.X, query.Axis.Y, query.FootRadius));
        bool crownCertified = geometry.TryGlobalCrown(query.CosMaxSlope, out var witness, out var maximum);
        Assert.True(crownCertified);
        Assert.Equal(1d, witness.Normal.Y.Lower);
        Assert.Equal(1d, witness.Normal.Y.Upper);
        Assert.True(witness.Normal.X.Lower <= 0 && witness.Normal.X.Upper >= 0);
        Assert.True(witness.Normal.Z.Lower <= 0 && witness.Normal.Z.Upper >= 0);
        B global = part.Centre.Y + (B)new R(highEnd, 1) * part.Axis.Y * (B)part.Half + (B)part.Radius;
        ContainsCurvedBounds(maximum.Lower, maximum.Upper, global);
        ContainsCurvedBounds(witness.Position.X.Lower, witness.Position.X.Upper, exact.Witness.X);
        ContainsCurvedBounds(witness.Position.Y.Lower, witness.Position.Y.Upper, global);
        ContainsCurvedBounds(witness.Position.Z.Lower, witness.Position.Z.Upper, exact.Witness.Z);
        Assert.Equal(GeometrySign.Positive, geometry.Round(false, query.CosMaxSlope, out var rounded, out var roundedMaximum));
        Assert.Equal(witness, rounded);
        Assert.Equal(maximum, roundedMaximum);
        Assert.Equal(0, work.Snapshot.RootCells);
        Assert.Equal(0, work.Snapshot.Refine);
        using var lease = scene.World.AcquireQueryReadLease();
        var capability = Capability(scene.World);
        SupportFootprintEquivalence.AssertMatchesReference(capability, lease, request);
        var members = new SupportFootprintMember[256];
        var result = capability.QuerySupportFootprint(lease, request, members, new ulong[1024]);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var member = Assert.Single(members.Take(result.Members));
        Assert.Equal(owner, member.Static);
        Assert.Equal(0, member.ElementId);
        Assert.Equal(2, member.WitnessCount);
        Assert.Equal(SupportWitnessRole.Axis, member.First.Role);
        Assert.Equal(SupportWitnessRole.Footprint, member.Second.Role);
        ContainsCurvedVector(member.Second.Position, member.Second.PositionErrorMetres, exact.Witness);
        Assert.True(member.First.Position.Y + member.First.PositionErrorMetres < maximum.Lower);
        SupportSample sample = SupportFootprintEquivalence.AssertMatchesReference(() =>
            FootSupport.Find(null, scene.World, lease, query));
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(owner, sample.Static);
        Assert.Equal(0, sample.FeatureId);
        Assert.InRange(sample.HeightError, 0, 0.0005f);
        ContainsCurvedBounds((double)sample.Height - sample.HeightError,
            (double)sample.Height + sample.HeightError, global);
    }

    [Fact]
    public void CapsuleCrownCannotPublishAfterItsWorkOwnerFails()
    {
        var leaf = ColumnLeaf(new(1, 0.75f, 0), half: 1.25) with { Shape = SupportCurvedShape.Capsule };
        var completeWork = NewCurvedWork();
        var complete = new SupportTiltedCurvedGeometry(completeWork);
        Assert.True(complete.Begin(leaf, 1.25, 0, 0.25));
        Assert.True(complete.TryGlobalCrown(0, out _, out _));
        var work = new SupportCurvedWork((int)completeWork.Snapshot.Consumed - 1);
        var denied = new SupportTiltedCurvedGeometry(work);
        Assert.True(denied.Begin(leaf, 1.25, 0, 0.25));
        Assert.False(denied.TryGlobalCrown(0, out var witness, out var maximum));
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, work.Snapshot.Failure);
        Assert.Equal(work.Snapshot.Limit, work.Snapshot.Consumed);
        Assert.Equal(default, witness);
        Assert.Equal(default, maximum);
        Assert.Equal(GeometrySign.Unresolved, denied.Round(false, 0, out witness, out maximum));
        Assert.Equal(default, witness);
        Assert.Equal(default, maximum);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapsuleCrownDeclinesWhenGlobalAttainmentIsUnproved(bool aboutX)
    {
        foreach (float y in new[] { 0.75f, -0.75f, 0f })
        {
            Vector3 column = aboutX ? new(0, y, 1) : new(1, y, 0);
            var leaf = ColumnLeaf(column, half: 1.25) with { Shape = SupportCurvedShape.Capsule };
            var part = ColumnPart(column, default, OracleSurface.Round, 0, half: 1.25);
            float direction = y < 0 ? -1 : 1;
            float boundary = y == 0 ? 1.5f : 1.25f;
            foreach (var (along, lateral) in new (float, float)[]
                { (MathF.BitDecrement(boundary), 0), (boundary, 0), (MathF.BitIncrement(boundary), 0),
                  (boundary, 0.125f), (1, 0.125f), (1, 0.3f) })
            {
                float a = direction * along;
                var request = Request(x: aboutX ? lateral : a, z: aboutX ? a : lateral,
                    radius: 0.25f, lower: -2, upper: 2);
                var exact = Proof.Crest(part, request);
                int classification = CrownDiscClassification(exact.Witness, request);
                bool expected = y != 0 && classification == 1;
                var geometry = new SupportTiltedCurvedGeometry(NewCurvedWork());
                Assert.True(geometry.Begin(leaf, request.Axis.X, request.Axis.Y, request.Radius));
                bool certified = geometry.TryGlobalCrown(request.CosMaxSlope, out var witness, out var maximum);
                _output.WriteLine($"crown aboutX={aboutX} y={y:R} axis={request.Axis} disc={classification} certified={certified}");
                Assert.Equal(expected, certified);
                if (!certified) { Assert.Equal(default, witness); Assert.Equal(default, maximum); }
            }
        }
        var kernel = new SupportTiltedCurvedGeometry(NewCurvedWork());
        Assert.True(kernel.Begin(ColumnLeaf(new(1, 0.75f, 0)), 0.4, 0, 0.25));
        Assert.False(kernel.TryGlobalCrown(Math.BitIncrement(1d), out _, out _));
        var work = new SupportCurvedWork(1);
        var exhausted = new SupportTiltedCurvedGeometry(work);
        Assert.False(exhausted.Begin(ColumnLeaf(new(1, 0.75f, 0)), 0.4, 0, 0.25));
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, work.Snapshot.Failure);
        Assert.False(exhausted.TryGlobalCrown(0, out var failed, out var failedMaximum));
        Assert.Equal(default, failed);
        Assert.Equal(default, failedMaximum);
        Assert.True(kernel.Begin(ColumnLeaf(new(1, 0, 0)), 0, 0, 0.25));
        Assert.False(kernel.TryGlobalCrown(0, out _, out _));
        Assert.Equal(GeometrySign.Positive, kernel.Round(false, 0, out _, out _));
    }

    [Fact]
    public void CapsuleCrownNeedsOriginalDiscAttainmentAndIndependentRoofClearance()
    {
        var leaf = ColumnLeaf(new(1, 0.75f, 0), half: 1.25) with { Shape = SupportCurvedShape.Capsule };
        var part = ColumnPart(leaf.SourceColumn, default, OracleSurface.Round, 0, half: 1.25);
        float outside = MathF.BitIncrement(1.25f);
        var request = Request(x: outside, radius: 0.25f, lower: -2, upper: 2);
        var exact = Proof.Crest(part, request);
        Assert.Equal(-1, CrownDiscClassification(exact.Witness, request));
        Assert.Equal(1, CrownDiscClassification(exact.Witness, request with { Radius = request.Radius + request.ContactBand }));
        var geometry = new SupportTiltedCurvedGeometry(NewCurvedWork());
        Assert.True(geometry.Begin(leaf, outside, 0, request.Radius));
        Assert.False(geometry.TryGlobalCrown(0, out _, out _));

        var (scene, obstacle, _) = ContactFollowupScenes.Log(true, 0.3f, false);
        using (scene)
        {
            var query = new FootSupportQuery(new(-0.7f, 0), 0.3f, 0.2f, 0.4f, 0.4f,
                ContactFollowupTraversalTests.CosMaxSlope);
            using (var clear = scene.World.AcquireQueryReadLease())
            {
                var sample = FootSupport.Find(null, scene.World, clear, query);
                Assert.Equal(SupportStatus.Walkable, sample.Status);
                Assert.Equal(obstacle, sample.Static);
            }
            scene.Add("roof", new BoxShape(new(0.5f, 0.125f, 0.5f)), Pose.At(new(-0.7f, 0.75f, 0)));
            using var lease = scene.World.AcquireQueryReadLease();
            var capability = (IPhysicsSupportFootprint)scene.World;
            var roofRequest = FootSupport.DescribeFootprint(query);
            var members = new SupportFootprintMember[256];
            SupportFootprintEquivalence.AssertMatchesReference(capability, lease, roofRequest);
            var result = capability.QuerySupportFootprint(lease, roofRequest, members, new ulong[1024]);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            Assert.Equal(SupportProposalStatus.InvalidStart, result.AxisVerdict.Status);
            Assert.Equal(SupportProposalStatus.InvalidStart, result.FootVerdict.Status);
            Assert.Contains(members.Take(result.Members), m => m.Static == scene["floor"] && m.HasDiscMaximum);
            var independentRoofSample = SupportFootprintEquivalence.AssertMatchesReference(() =>
                FootSupport.Find(null, scene.World, lease, query));
            Assert.Equal(SupportStatus.Refused, independentRoofSample.Status);
            Assert.Null(independentRoofSample.Static);
        }
    }

    static SupportCurvedLeaf CrownLeaf(SupportFootprintScene scene, StaticHandle owner)
    {
        var simulation = SimulationOf(scene.World);
        simulation.Statics.GetDescription(HandlesOf(scene.World)[owner.Value].Handle, out var description);
        var leaves = new List<SupportCurvedLeaf>();
        Assert.Equal(CapsuleFeatureStatus.Complete,
            SupportNeighborhoodCurved.Capture(simulation, description.Shape, description.Pose, leaves));
        return Assert.Single(leaves);
    }

    // Independent rational/radical endpoint membership. Zero means the oracle enclosure cannot decide.
    static int CrownDiscClassification(SupportFootprintExactOracle.Point crown, SupportFootprintRequest request)
    {
        B Square(B value) => new(value.Lower <= R.Zero && value.Upper >= R.Zero ? R.Zero :
            (value.Lower.Square() < value.Upper.Square() ? value.Lower.Square() : value.Upper.Square()),
            value.Lower.Square() > value.Upper.Square() ? value.Lower.Square() : value.Upper.Square());
        B gap = (B)R.From(request.Radius).Square() - Square(crown.X - (B)R.From(request.Axis.X)) -
            Square(crown.Z - (B)R.From(request.Axis.Y));
        return gap.Lower >= R.Zero ? 1 : gap.Upper < R.Zero ? -1 : 0;
    }
}
