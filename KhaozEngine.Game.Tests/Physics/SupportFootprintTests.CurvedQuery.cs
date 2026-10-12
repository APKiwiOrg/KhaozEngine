using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using Proof = KhaozEngine.Tests.Physics.SupportFootprintCurvedProofOracle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData("sphere-apex")]
    [InlineData("sphere-flank")]
    [InlineData("sphere-axis-tangent")]
    [InlineData("sphere-disc-tangent")]
    [InlineData("capsule-zero")]
    [InlineData("capsule-upright")]
    [InlineData("capsule-horizontal")]
    [InlineData("capsule-horizontal-yaw")]
    [InlineData("capsule-end-tangent")]
    [InlineData("capsule-inverted")]
    [InlineData("capsule-end")]
    [InlineData("capsule-seam")]
    [InlineData("capsule-core-line")]
    [InlineData("capsule-axis-tangent")]
    [InlineData("capsule-compound")]
    [InlineData("cylinder-upright")]
    [InlineData("cylinder-horizontal")]
    [InlineData("cylinder-horizontal-yaw")]
    [InlineData("cylinder-negative-horizontal")]
    [InlineData("cylinder-ring")]
    [InlineData("cylinder-finite-end")]
    [InlineData("cylinder-finite-end-yaw")]
    [InlineData("cylinder-axis-tangent")]
    [InlineData("cylinder-disc-tangent")]
    [InlineData("cylinder-cap-tangent")]
    [InlineData("cylinder-inverted")]
    [InlineData("cylinder-compound")]
    [InlineData("cylinder-inverted-compound")]
    [InlineData("hidden-sphere")]
    [InlineData("hidden-capsule-zero")]
    [InlineData("hidden-capsule-upright")]
    [InlineData("hidden-capsule-horizontal")]
    [InlineData("hidden-cylinder-upright")]
    [InlineData("hidden-cylinder-horizontal")]
    public void CurvedFootprintMatchesAllProvedPoseStrata(string stratum)
    {
        using var scene = new SupportFootprintScene();
        (PhysicsShape shape, Pose pose, SupportFootprintRequest request) = CurvedQueryRow(stratum);
        StaticHandle owner = scene.Install(shape, pose);
        Proof.InstalledPart[] parts = Proof.Installed(scene.World, owner).ToArray();
        FootprintOracleMember[] expected = parts.Select(p => SupportFootprintOracle.Curved(p.Tangent, request))
            .OfType<FootprintOracleMember>().OrderBy(p => p.ElementId).ToArray();
        Assert.NotEmpty(expected);
        if (stratum.StartsWith("hidden-", StringComparison.Ordinal))
        {
            Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3);
            StaticHandle wedge = scene.Install(new BoxShape(new(0.1f, 0.1f, 0.3f)),
                new(new Vector3(0.2f, 0.25f, 0) + Vector3.Transform(new Vector3(0, -0.1f, 0), tilt), tilt));
            Assert.True(scene.World.SweepCapsule(request.FootProposal.Probe, request.FootProposal.Start,
                -Vector3.UnitY, request.FootProposal.Distance, out SweepHit hit));
            Assert.Equal(wedge, hit.Body);
        }

        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        SupportFootprintMember[] actual = members.Take(result.Members).Where(m => m.Static == owner).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            SupportFootprintMember member = actual[i];
            FootprintOracleMember reference = expected[i];
            var exact = SupportFootprintClosedFormOracle.Measure(parts.Single(p => p.Tangent.ElementId == member.ElementId), request);
            Assert.Equal(reference.Static, member.Static);
            Assert.Equal(reference.ElementId, member.ElementId);
            Assert.Equal(SupportElementKind.Tangent, member.Kind);
            Assert.Equal(reference.HasDiscMaximum, member.HasDiscMaximum);
            Assert.Equal(exact.Witnesses.Count, member.WitnessCount);
            Assert.InRange(member.WitnessCount, 0, 2);
            if (exact.Maximum is { } maximum)
                ContainsCurvedBounds(member.DiscHeightLower, member.DiscHeightUpper, maximum);
            for (int role = 0; role < member.WitnessCount; role++)
            {
                SupportFootprintWitness witness = role == 0 ? member.First : member.Second;
                var attained = exact.Witnesses[role];
                Assert.Equal(attained.Role, witness.Role);
                ContainsCurvedVector(witness.Position, witness.PositionErrorMetres, attained.Position);
                ContainsCurvedVector(witness.Normal, witness.NormalError, attained.Normal);
                if (attained.Contribution is { } contribution)
                    ContainsCurvedContribution(witness, request, contribution);
            }
            int index = Array.FindIndex(members, 0, result.Members,
                m => m.Static == member.Static && m.ElementId == member.ElementId);
            for (int j = 0; j < result.Members; j++)
                Assert.Equal(0UL, joins[index * result.JoinWordsPerRow + j / 64] & (1UL << (j % 64)));
        }
        Assert.Equal(members.Take(result.Members).Sum(m => m.WitnessCount), result.Witnesses);
        if (stratum == "cylinder-upright")
            Assert.InRange(0.5, actual.Single(m => m.ElementId == 1).DiscHeightLower,
                actual.Single(m => m.ElementId == 1).DiscHeightUpper);
        if (stratum == "cylinder-horizontal")
        {
            SupportFootprintMember side = actual.Single(m => m.ElementId == 0);
            double top = (double)0.3f + 0.3f;
            Assert.InRange(top, side.DiscHeightLower, side.DiscHeightUpper);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        SupportFootprintResult warm = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(CapsuleFeatureStatus.Complete, warm.Status);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    public void CurvedRawCapacityUsesTwoSlots(int count)
    {
        using var scene = new SupportFootprintScene();
        for (int i = 0; i < count; i++)
            scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
        using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
        SupportFootprintMember[] members = MemberSentinels(256), before = (SupportFootprintMember[])members.Clone();
        ulong[] joins = Enumerable.Repeat(0xDEADBEEFUL, 1024).ToArray(), beforeJoins = (ulong[])joins.Clone();
        var request = Request();
        SupportFootprintResult result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        if (count == 256)
        {
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            Assert.Equal(256, result.Members);
            Assert.Equal(512, result.Witnesses);
            Assert.All(members, m =>
            {
                Assert.Equal(0, m.ElementId);
                Assert.Equal(2, m.WitnessCount);
                Assert.Equal(SupportWitnessRole.Axis, m.First.Role);
                Assert.Equal(SupportWitnessRole.Footprint, m.Second.Role);
            });
            Assert.Equal(256, members.Select(m => m.Static).Distinct().Count());
            Assert.All(joins, word => Assert.Equal(0UL, word));
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var warm = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
            Assert.Equal(CapsuleFeatureStatus.Complete, warm.Status);
            Assert.Equal(512, warm.Witnesses);
            Assert.Equal(0, bytes);
        }
        else
        {
            Assert.Equal(CapsuleFeatureStatus.CapacityExceeded, result.Status);
            Assert.Equal(257, result.RequiredElements);
            Assert.Equal(0, result.Members);
            Assert.Equal(0, result.Witnesses);
            Assert.Equal(before, members);
            Assert.Equal(beforeJoins, joins);
        }
    }

    static void ContainsCurvedBounds(double lower, double upper, B reference)
    {
        Assert.True(R.From(lower) <= reference.Lower && R.From(upper) >= reference.Upper);
    }

    static void ContainsCurvedVector(Vector3 value, float error, P reference)
    {
        Assert.True(float.IsFinite(error) && error >= 0);
        R e = R.From(error);
        Assert.True(R.From(value.X) - e <= reference.X.Lower && R.From(value.X) + e >= reference.X.Upper);
        Assert.True(R.From(value.Y) - e <= reference.Y.Lower && R.From(value.Y) + e >= reference.Y.Upper);
        Assert.True(R.From(value.Z) - e <= reference.Z.Lower && R.From(value.Z) + e >= reference.Z.Upper);
    }

    static void ContainsCurvedContribution(SupportFootprintWitness witness, in SupportFootprintRequest request, B reference)
    {
        B Component(float value, float error) => new(R.From(value) - R.From(error), R.From(value) + R.From(error));
        B px = Component(witness.Position.X, witness.PositionErrorMetres), py = Component(witness.Position.Y, witness.PositionErrorMetres);
        B pz = Component(witness.Position.Z, witness.PositionErrorMetres), nx = Component(witness.Normal.X, witness.NormalError);
        B ny = Component(witness.Normal.Y, witness.NormalError), nz = Component(witness.Normal.Z, witness.NormalError);
        if (ny.Lower <= R.Zero) return;
        B plane = py + (nx * (px - (B)R.From(request.Axis.X)) + nz * (pz - (B)R.From(request.Axis.Y))) / ny;
        var contribution = new B(py.Lower < plane.Lower ? py.Lower : plane.Lower,
            py.Upper < plane.Upper ? py.Upper : plane.Upper);
        Assert.True(contribution.Lower <= reference.Lower && contribution.Upper >= reference.Upper);
    }

    static (PhysicsShape Shape, Pose Pose, SupportFootprintRequest Request) CurvedQueryRow(string row)
    {
        bool hidden = row.StartsWith("hidden-", StringComparison.Ordinal);
        string name = hidden ? row[7..] : row;
        bool cylinder = name.StartsWith("cylinder-", StringComparison.Ordinal);
        bool capsule = name.StartsWith("capsule-", StringComparison.Ordinal);
        bool horizontal = name.Contains("horizontal", StringComparison.Ordinal) ||
            name.Contains("end", StringComparison.Ordinal) || name.EndsWith("seam", StringComparison.Ordinal) ||
            ((cylinder || capsule) && name.Contains("tangent", StringComparison.Ordinal) && name != "cylinder-cap-tangent");
        PhysicsShape shape = cylinder ? new CylinderShape(0.25f, 1) : capsule
            ? new CapsuleShape(0.25f, name.EndsWith("zero", StringComparison.Ordinal) ? 0 : 1) : new SphereShape(0.25f);
        Pose pose = new(new(0, -0.25f, 0), horizontal ? new Quaternion(0.5f, 0.5f, 0.5f, 0.5f) : Quaternion.Identity);
        SupportFootprintRequest request = hidden ? Request() : Request(lower: -2, upper: 2);
        if (hidden && name.EndsWith("upright", StringComparison.Ordinal))
            pose = pose with { Position = new(0, cylinder ? -1 : -0.75f, 0) };
        if (name is "sphere-flank") request = Request(x: 0.35f);
        if (name.EndsWith("axis-tangent", StringComparison.Ordinal)) request = Request(x: 0.25f);
        if (name.EndsWith("disc-tangent", StringComparison.Ordinal)) request = Request(x: 0.5f, radius: 0.25f);
        if (name is "capsule-end") request = Request(z: 0.85f);
        if (name is "capsule-seam") request = Request(z: 0.5f);
        if (name is "capsule-core-line")
        {
            shape = new CapsuleShape(0.25f, 1.625f);
            pose = new(new(0, -0.25f, 0), new(0.5f, 0.2692306339740753f, 0.5f, 0.6538458466529846f));
            request = Request(x: -0.3125f, z: 0.75f, lower: -2, upper: 2);
        }
        if (name is "cylinder-finite-end") request = Request(x: 0.35f, z: 1.25f, radius: 0.3f);
        if (name is "cylinder-cap-tangent")
            request = Request(x: 0.5f, radius: 0.25f, cosine: 1);
        if (!hidden && name is "cylinder-upright")
        {
            shape = new CylinderShape(0.4f, 0.5f);
            // Cylinder installation lifts the centre by half its length.
            pose = Pose.Identity;
        }
        if (!hidden && name is "cylinder-horizontal")
        {
            shape = new CylinderShape(0.3f, 2);
            pose = pose with { Position = new(0, 0.3f, -1) };
        }
        if (name.Contains("tilted", StringComparison.Ordinal) || name.Contains("compound", StringComparison.Ordinal) ||
            name.Contains("inverted", StringComparison.Ordinal))
        {
            pose = pose with { Orientation = Quaternion.Identity };
            if (name.Contains("inverted", StringComparison.Ordinal))
                pose = pose with { Orientation = Quaternion.Multiply(pose.Orientation, new Quaternion(1, 0, 0, 0)) };
            if (name.Contains("compound", StringComparison.Ordinal))
                shape = new CompoundShape([
                    new(new SphereShape(0.125f), Pose.At(new(0, -0.125f, 0))),
                    new(shape, new(new(0.125f, 0.125f, -0.125f),
                        Quaternion.Identity)),
                    new(new SphereShape(0.125f), Pose.At(new(0.1f, 0, 0)))
                ]);
            request = Request(radius: 0.5f, cosine: 0.01f, lower: -2, upper: 2);
        }
        if (name.EndsWith("yaw", StringComparison.Ordinal))
        {
            pose = new(cylinder ? new(0.3f, -0.25f, -0.4f) : new(0, -0.25f, 0), new(0.5f, 0.1f, 0.5f, 0.7f));
            request = name.Contains("finite-end", StringComparison.Ordinal) ? Request(x: -0.45f, z: 0.6f, radius: 0.3f) :
                Request(x: 0.08f, z: 0.06f, lower: -2, upper: 2);
        }
        if (name is "capsule-end-tangent") request = Request(z: 1, radius: 0.25f);
        if (name is "cylinder-negative-horizontal")
        {
            pose = new(new(0, -0.25f, 0), new(-0.5f, -0.5f, 0.5f, 0.5f));
            request = Request(lower: -2, upper: 2);
        }
        if (name is "cylinder-ring")
        {
            pose = new(new(0, -0.25f, 0), new(0.5f, 0.5f, 0.5f, 0.5f));
            request = Request(x: 0.45005f, z: 0.5f);
        }
        return (shape, pose, request);
    }
}
