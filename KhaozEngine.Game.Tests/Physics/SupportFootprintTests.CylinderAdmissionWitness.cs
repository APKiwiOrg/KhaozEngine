using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(0xbf333332u)]
    [InlineData(0xbf333333u)]
    [InlineData(0xbf333334u)]
    public void CylinderEndTangencyHasAnAttainedFiniteAdmissionWitness(uint bits)
    {
        float x = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        var interior = ContactFollowupEndWorkTests.Observe(false, -0.25f);
        var row = ContactFollowupEndWorkTests.Observe(false, x);
        _output.WriteLine($"interior x=-0.25 {interior.Work}");
        long bound = 10L * ContactFollowupEndWorkTests.PreAdmissionWitnessInteriorConsumed;
        _output.WriteLine($"cylinder bits={bits:x8} {row.Work} bound={bound}");
        foreach (bool capsule in new[] { false, true })
            Assert.Equal(CylinderAdmissionSnapshot.Before(capsule, bits), CylinderAdmissionSnapshot.Read(capsule, x));
        Assert.Equal(SupportStatus.Walkable, interior.Sample.Status);
        Assert.Equal(ContactFollowupEndWorkTests.CylinderEndStatus(x), row.Sample.Status);
        Assert.Equal(CapsuleFeatureStatus.Complete, row.Work.FootprintStatus);
        Assert.Equal(SupportCurvedWorkFailure.None, row.Work.Failure);
        Assert.Equal(CapsuleFeatureStatus.Complete, interior.Work.FootprintStatus);
        Assert.Equal(SupportCurvedWorkFailure.None, interior.Work.Failure);
        Assert.InRange(interior.Work.Consumed, 1, ContactFollowupEndWorkTests.PreAdmissionWitnessInteriorConsumed);
        Assert.True(row.Work.Consumed <= bound, $"seam {row.Work.Consumed} exceeds fixed interior bound {bound}");
    }

    [Fact]
    public void CylinderAdmissionWitnessKeepsTheInteriorPublicResult()
    {
        Assert.Equal(CylinderAdmissionSnapshot.InteriorBefore(), CylinderAdmissionSnapshot.Read(false, -0.25f));
    }

    [Theory]
    [InlineData(false, 0.75f)]
    [InlineData(false, -0.75f)]
    [InlineData(true, 0.75f)]
    [InlineData(true, -0.75f)]
    public void CylinderAdmissionWitnessNeverCertifiesAbsenceOrTheWrongPart(bool aboutX, float y)
    {
        var leaf = ColumnLeaf(aboutX ? new(0, y, 1) : new(1, y, 0), half: 1.25);
        var work = NewCurvedWork();
        var geometry = new SupportTiltedCurvedGeometry(work);
        var admission = new SupportCurvedSurfaceAdmission(work);
        Assert.True(geometry.Begin(leaf, 0, 0, 0.01));
        bool Certify(int cap, double along, double lateral, double radius, double bottom, double top, bool up = false) =>
            geometry.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, cap,
                aboutX ? lateral : along, aboutX ? along : lateral, radius, bottom, top, up);
        GeometrySign Traverse(int cap, double along, double lateral, double radius, double bottom, double top)
        {
            admission.ResetStatistics();
            GeometrySign sign = admission.Measure(geometry, SupportCurvedShape.Cylinder, cap,
                aboutX ? lateral : along, aboutX ? along : lateral, GeometryInterval.Exact(radius), bottom, top);
            Assert.True(admission.TotalCells > 0);
            return sign;
        }
        // Independent 3-4-5 normalization. The represented axis rises as s * along, and the radial
        // direction w moves a cap point by -s * 0.15 * a along and 0.2 * a up.
        int s = Math.Sign(y);
        foreach (int end in new[] { -1, 1 })
        {
            double height = end * s * 0.75;
            int cap = geometry.CapKind(end > 0 ? SupportNeighborhoodCurved.TopCap : SupportNeighborhoodCurved.BottomCap);
            int opposite = cap == 2 ? 3 : 2;
            Assert.True(Certify(cap, end, 0, 0.01, height - 0.01, height + 0.01));
            Assert.False(Certify(opposite, end, 0, 0.01, height - 0.01, height + 0.01));
            Assert.False(Certify(-1, end, 0, 0.01, height - 0.01, height + 0.01));
            Assert.Equal(end * y > 0, Certify(cap, end, 0, 0.01, height - 0.01, height + 0.01, true));
            Assert.False(Certify(cap, end, 0, 0.01, height + 1, height + 2));
            Assert.False(Certify(cap, end, 1, 0.01, -2, 2));
            Assert.True(Certify(cap, end, 0.25, 0.01, height - 0.01, height + 0.01));
            Assert.True(Certify(cap, end, -0.25, 0.01, height - 0.01, height + 0.01));
            // The vertical projection inside the disc is offered itself. No fixed centre or rim point meets it.
            foreach (double lateral in new[] { -0.075, 0.075 })
            {
                double insideAlong = end - s * 0.06, insideY = height + 0.08;
                Assert.True(Certify(cap, insideAlong, lateral, 0.001, insideY - 0.001, insideY + 0.001));
                Assert.False(Certify(opposite, insideAlong, lateral, 0.001, insideY - 0.001, insideY + 0.001));
                Assert.False(Certify(-1, insideAlong, lateral, 0.001, insideY - 0.001, insideY + 0.001));
                Assert.Equal(end * y > 0, Certify(cap, insideAlong, lateral, 0.001, insideY - 0.001, insideY + 0.001, true));
            }
            // Outside the disc, only the centre-directed rim point is offered, never the outside parameter.
            Assert.True(Certify(cap, end, 0.35, 0.11, height - 0.01, height + 0.01));
            Assert.True(Certify(cap, end, -0.35, 0.11, height - 0.01, height + 0.01));
            Assert.False(Certify(cap, end, 0.35, 0.09, height - 0.01, height + 0.01));
            foreach (int rim in new[] { -1, 1 })
            {
                double outsideAlong = end - s * 0.21 * rim, rimY = height + 0.2 * rim, outsideY = height + 0.28 * rim;
                Assert.True(Certify(cap, outsideAlong, 0, 0.07, rimY - 0.01, rimY + 0.01));
                Assert.False(Certify(cap, outsideAlong, 0, 0.07, outsideY - 0.01, outsideY + 0.01));
                Assert.False(Certify(opposite, outsideAlong, 0, 0.07, rimY - 0.01, rimY + 0.01));
            }
            double barrelX = end - s * 0.15;
            double barrelY = height + 0.2;
            Assert.True(Certify(-1, barrelX, 0, 0.01, barrelY - 0.01, barrelY + 0.01, true));
            Assert.True(Certify(cap, barrelX, 0, 0.01, barrelY - 0.01, barrelY + 0.01));
            // Only the upward top line is offered. The lower line needs the complete traversal.
            double lowerX = end + s * 0.15;
            double lowerY = height - 0.2;
            Assert.False(Certify(-1, lowerX, 0, 0.01, lowerY - 0.01, lowerY + 0.01));
            Assert.Equal(GeometrySign.Positive, Traverse(-1, lowerX, 0, 0.01, lowerY - 0.01, lowerY + 0.01));
            Assert.True(Certify(cap, lowerX, 0, 0.01, lowerY - 0.01, lowerY + 0.01));
            Assert.False(Certify(-1, lowerX, 0, 0.01, lowerY - 0.01, lowerY + 0.01, true));
            // The top-line parameter clamps to the closed end. An unclamped a = 1.5 would certify a point
            // beyond the finite barrel, which the complete traversal proves absent.
            int axial = end * s;
            double endAlong = s * (axial - 0.15), endY = 0.75 * axial + 0.2;
            Assert.True(Certify(-1, endAlong + s * axial * 0.005, 0, 0.01, endY - 0.01, endY + 0.01, true));
            double beyondAlong = s * (1.5 * axial - 0.15), beyondY = 1.125 * axial + 0.2;
            Assert.False(Certify(-1, beyondAlong, 0, 0.01, beyondY - 0.01, beyondY + 0.01));
            Assert.Equal(GeometrySign.Negative, Traverse(-1, beyondAlong, 0, 0.01, beyondY - 0.01, beyondY + 0.01));
        }
        // The nearest top-line point attains an interior column directly above the axis.
        Assert.True(Certify(-1, -s * 0.15, 0, 0.01, 0.19, 0.21, true));
        // Off the top line the candidate misses. The complete traversal still finds the barrel at b = 0.6.
        foreach (double lateral in new[] { -0.15, 0.15 })
        {
            Assert.False(Certify(-1, -s * 0.12, lateral, 0.01, 0.15, 0.17));
            Assert.Equal(GeometrySign.Positive, Traverse(-1, -s * 0.12, lateral, 0.01, 0.15, 0.17));
        }
        Assert.False(Certify(-1, 10, 0, 0.01, -2, 2));
        Assert.Equal(GeometrySign.Negative, Traverse(-1, 10, 0, 0.01, -2, 2));
        Assert.False(geometry.HasFiniteAdmissionWitness(SupportCurvedShape.Capsule, -1, 0, 0, 2, -2, 2, false));
        Assert.Equal(SupportCurvedWorkFailure.None, work.Snapshot.Failure);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CylinderAdmissionWitnessNeverCertifiesTheWrongInstalledQuarterTurnPart(bool aboutX, bool inverted)
    {
        using var scene = new SupportFootprintScene();
        float angle = inverted ? MathF.BitIncrement(MathF.PI / 2) : MathF.PI / 2;
        StaticHandle owner = InstallLyingLog(scene, false, 0.3f,
            Quaternion.CreateFromAxisAngle(aboutX ? Vector3.UnitX : Vector3.UnitZ, angle));
        var part = SupportFootprintCurvedProofOracle.Installed(scene.World, owner)[0];
        int ySign = inverted ? -1 : 1;
        Assert.True(ySign > 0 ? part.Axis.Y.Lower > R.Zero : part.Axis.Y.Upper < R.Zero);
        B along = aboutX ? part.Axis.Z : part.Axis.X;
        int alongSign = along.Lower > R.Zero ? 1 : -1;
        B u = (B)new R(alongSign, 1) * along;
        var geometry = new SupportTiltedCurvedGeometry(NewCurvedWork());
        Assert.True(geometry.Begin(CrownLeaf(scene, owner), 0, 0, 0.01));
        foreach (int end in new[] { -1, 1 })
        {
            B t = new R(end, 1) * part.Half;
            B x = part.Centre.X + t * part.Axis.X;
            B z = part.Centre.Z + t * part.Axis.Z;
            B y = part.Centre.Y + t * part.Axis.Y;
            int cap = geometry.CapKind(end > 0 ? SupportNeighborhoodCurved.TopCap : SupportNeighborhoodCurved.BottomCap);
            bool Certify(int kind, B px, B py, B pz, bool up = false) =>
                geometry.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, kind, px.Middle, pz.Middle,
                    0.0001, py.Middle - 0.0001, py.Middle + 0.0001, up);
            Assert.True(Certify(cap, x, y, z));
            Assert.False(Certify(cap == 2 ? 3 : 2, x, y, z));
            Assert.False(Certify(-1, x, y, z));
            Assert.Equal(end * ySign > 0, Certify(cap, x, y, z, true));
            B radial = -(B)new R(alongSign, 1) * part.Axis.Y * (B)part.Radius;
            B barrelX = aboutX ? x : x + radial, barrelZ = aboutX ? z + radial : z;
            B barrelY = y + u * (B)part.Radius;
            Assert.True(Certify(-1, barrelX, barrelY, barrelZ, true));
        }
    }

    [Fact]
    public void CylinderAdmissionWitnessIncludesClosedEndpointsAndCannotSurviveOwnerFailure()
    {
        var leaf = ColumnLeaf(new(1, 0, 0));
        var work = NewCurvedWork();
        var geometry = new SupportTiltedCurvedGeometry(work);
        Assert.True(geometry.Begin(leaf, 0, 0, 0.25));
        Assert.True(geometry.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, -1, 0.5, 0.25, 0.25, 0.25, 0.25, true));
        Assert.False(geometry.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, -1, 0.5,
            Math.BitIncrement(0.25), 0.25, 0.25, 0.25, true));
        Assert.True(geometry.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, 2, -0.5, 0.25, 0, 0, 0, true));
        Assert.False(geometry.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, 3, -0.5, 0.25, 0, 0, 0, true));
        var completeWork = NewCurvedWork();
        var complete = new SupportTiltedCurvedGeometry(completeWork);
        Assert.True(complete.Begin(ColumnLeaf(new(1, 0.75f, 0), half: 1.25), 1, 0, 0.01));
        Assert.True(complete.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, 2, 1, 0, 0, 0.75, 0.75, false));
        var deniedWork = new SupportCurvedWork((int)completeWork.Snapshot.Consumed - 1);
        var denied = new SupportTiltedCurvedGeometry(deniedWork);
        Assert.True(denied.Begin(ColumnLeaf(new(1, 0.75f, 0), half: 1.25), 1, 0, 0.01));
        Assert.False(denied.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, 2, 1, 0, 0, 0.75, 0.75, false));
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, deniedWork.Snapshot.Failure);
        Assert.False(denied.HasFiniteAdmissionWitness(SupportCurvedShape.Cylinder, 2, 1, 0, 1, -2, 2, false));
        Assert.Equal(GeometrySign.Unresolved, new SupportCurvedSurfaceAdmission(deniedWork).Measure(denied,
            SupportCurvedShape.Cylinder, 2, 1, 0, GeometryInterval.Exact(1), -2, 2));
    }

    [Fact]
    public void CylinderAdmissionWitnessRetainsIndependentRoofAndProposalGuards()
    {
        var (scene, obstacle, _) = ContactFollowupScenes.Log(false, 0.3f, false);
        using (scene)
        {
            var query = new FootSupportQuery(new(-0.25f, 0), 0.3f, 0.2f, 0.4f, 0.4f,
                ContactFollowupTraversalTests.CosMaxSlope);
            using (var clear = scene.World.AcquireQueryReadLease())
            {
                var sample = FootSupport.Find(null, scene.World, clear, query);
                Assert.Equal(SupportStatus.Walkable, sample.Status);
                Assert.Equal(obstacle, sample.Static);
            }
            scene.Add("roof", new BoxShape(new(0.5f, 0.125f, 0.5f)), Pose.At(new(-0.25f, 0.75f, 0)));
            using var lease = scene.World.AcquireQueryReadLease();
            var request = FootSupport.DescribeFootprint(query);
            var capability = (IPhysicsSupportFootprint)scene.World;
            var result = SupportFootprintEquivalence.AssertMatchesReference(capability, lease, request);
            var members = new SupportFootprintMember[256];
            result = capability.QuerySupportFootprint(lease, request, members, new ulong[1024]);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            Assert.Equal(SupportProposalStatus.InvalidStart, result.AxisVerdict.Status);
            Assert.Equal(SupportProposalStatus.InvalidStart, result.FootVerdict.Status);
            Assert.Contains(members.Take(result.Members), member => member.Static == scene["floor"] && member.HasDiscMaximum);
            Assert.Equal(SupportStatus.Refused, SupportFootprintEquivalence.AssertMatchesReference(() =>
                FootSupport.Find(null, scene.World, lease, query)).Status);
        }
    }
}
