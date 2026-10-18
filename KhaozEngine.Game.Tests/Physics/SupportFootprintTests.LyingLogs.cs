using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Proof = KhaozEngine.Tests.Physics.SupportFootprintCurvedProofOracle;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    public static IEnumerable<object[]> LyingLogRows()
    {
        foreach (bool capsule in new[] { false, true })
            foreach (float radius in new[] { 0.1f, 0.15f, 0.3f })
                foreach (bool aboutX in new[] { false, true })
                    foreach (int sign in new[] { -1, 1 })
                        yield return [capsule, radius, aboutX, sign];
    }

    [Theory]
    [MemberData(nameof(LyingLogRows))]
    public void LyingLogFlankSupportsWithInstalledPoseOracle(bool capsule, float radius, bool aboutX, int sign)
    {
        for (int rebuild = 0; rebuild < 2; rebuild++)
        {
            using var scene = new SupportFootprintScene();
            scene.Install(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0)));
            Quaternion rotation = Quaternion.CreateFromAxisAngle(aboutX ? Vector3.UnitX : Vector3.UnitZ, MathF.PI / 2);
            StaticHandle owner = InstallLyingLog(scene, capsule, radius, rotation);
            var query = LyingLogQuery(radius, aboutX, sign * 0.25f);
            var request = FootSupport.DescribeFootprint(query);
            var source = Proof.Installed(scene.World, owner)[0];
            Assert.NotEqual(R.Zero, source.Axis.Y.Lower);
            Assert.InRange(Math.Abs(source.Tangent.Axis.Y), 0, 1e-6);
            var expected = SupportFootprintOracle.Select(Members(scene, request), request);
            Assert.Equal(owner, expected.Static);
            using var lease = scene.World.AcquireQueryReadLease();
            foreach (bool reference in new[] { false, true })
            {
                using IDisposable? evaluation = reference ? SupportReferenceEvaluation.Begin() : null;
                Check(FootSupport.Find(null, scene.World, lease, query));
                Check(FootSupport.Find(null, scene.World, lease, query));
                FootSupport.Find(null, scene.World, lease, query with { Axis = new(0.07f, -0.03f) });
                Check(FootSupport.Find(null, scene.World, lease, query));
                CheckLyingLogParts(scene, owner, request, lease: lease);
            }

            void Check(SupportSample sample)
            {
                Assert.Equal(SupportStatus.Walkable, sample.Status);
                Assert.Equal(owner, sample.Static);
                Assert.Equal(expected.ElementId, sample.FeatureId);
                Assert.True((double)sample.Height - sample.HeightError <= expected.HeightLower);
                Assert.True((double)sample.Height + sample.HeightError >= expected.HeightUpper);
                Assert.InRange(sample.HeightError, 0f, 0.0005f);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void LyingLogExactHorizontalAndSphereControls(int shape)
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = shape == 2 ? scene.Install(new SphereShape(0.15f), Pose.At(new(0, 0.15f, 0))) :
            InstallLyingLog(scene, shape == 1, 0.15f, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f));
        var query = LyingLogQuery(0.15f, true, 0.25f);
        var request = FootSupport.DescribeFootprint(query);
        var expected = SupportFootprintOracle.Select(Members(scene, request), request);
        Assert.InRange(expected.Height, 0.22071069, 0.22071072);
        using var lease = scene.World.AcquireQueryReadLease();
        var sample = FootSupport.Find(null, scene.World, lease, query);
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(owner, sample.Static);
        Assert.True((double)sample.Height - sample.HeightError <= expected.HeightLower);
        Assert.True((double)sample.Height + sample.HeightError >= expected.HeightUpper);
        CheckLyingLogParts(scene, owner, request, horizontal: true, lease: lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LyingLogBoundaryIdentityDoesNotCertifyAClippedOrUnprovedRoot(bool aboutX)
    {
        foreach (float angle in new[] { MathF.PI / 2, MathF.BitDecrement(MathF.PI / 2),
            MathF.BitIncrement(MathF.PI / 2), MathF.PI / 2 - 0.000001f, MathF.PI / 2 + 0.000001f })
        {
            using var scene = new SupportFootprintScene();
            Quaternion rotation = Quaternion.CreateFromAxisAngle(aboutX ? Vector3.UnitX : Vector3.UnitZ, angle);
            StaticHandle owner = InstallLyingLog(scene, false, 0.15f, rotation);
            var simulation = SimulationOf(scene.World);
            simulation.Statics.GetDescription(HandlesOf(scene.World)[owner.Value].Handle, out var description);
            var leaves = new List<SupportCurvedLeaf>();
            Assert.Equal(CapsuleFeatureStatus.Complete,
                SupportNeighborhoodCurved.Capture(simulation, description.Shape, description.Pose, leaves));
            foreach (int sign in new[] { -1, 1 })
            {
                var query = LyingLogQuery(0.15f, aboutX, sign * 0.25f);
                var request = FootSupport.DescribeFootprint(query);
                var geometry = new SupportTiltedCurvedGeometry();
                Assert.True(geometry.Begin(Assert.Single(leaves), query.Axis.X, query.Axis.Y, 0.2f));
                Assert.Equal(GeometrySign.Positive, geometry.Side(query.CosMaxSlope, out _, out var maximum));
                Assert.True(maximum.IsResolved && double.IsFinite(maximum.Lower) && double.IsFinite(maximum.Upper));
                var source = Proof.Installed(scene.World, owner)[0];
                var reference = SupportFootprintOracle.Curved(source.Tangent, request)!;
                var reading = SupportFootprintTiltedOracle.Enclose(source, reference.GeometricMaximum!.Value, request, false);
                ContainsCurvedBounds(maximum.Lower, maximum.Upper, reading.Position.Y);
                // Both lateral roots are outside the barrel here. Clipping one to a pole
                // must not confer the source equation or invent a feasible side point.
                Assert.True(geometry.Begin(leaves[0], aboutX ? sign : 0, aboutX ? 0 : sign, 0.2f));
                Assert.Equal(GeometrySign.Negative, geometry.Side(query.CosMaxSlope, out _, out _));
            }
        }
        // The old box spanned both horizontal Z signs. Each represented column decides its own side.
        foreach (float z in new[] { -1e-8f, 1e-8f })
        {
            var column = new Vector3(1, 1e-8f, z);
            var geometry = new SupportTiltedCurvedGeometry();
            Assert.True(geometry.Begin(ColumnLeaf(column), 0.5, 0.5, 0.25));
            GeometrySign side = geometry.Side(0, out _, out _);
            _output.WriteLine($"z={z:R} side={side}");
            Assert.NotEqual(GeometrySign.Unresolved, side);
        }
    }

    static SupportCurvedLeaf LyingLogBoundaryLeaf(float y) => ColumnLeaf(new(1, y, 0), half: 1);

    // The old box made this flank endpoint's slope unprovable. Each represented column now decides it, and the
    // attained crest still dominates.
    [Theory]
    [InlineData(1e-5f)]
    [InlineData(1e-4f)]
    public void LyingLogBoundaryBelowAnAttainedPointKeepsTheSide(float y)
    {
        var geometry = new SupportTiltedCurvedGeometry();
        double slope = Math.Sqrt(0.84) * (1 - 2.5e-9);
        Assert.True(geometry.Begin(LyingLogBoundaryLeaf(y), 0, -0.1, 0.2));
        // The positive endpoint near z=0.4 has an upward normal near the slope.
        GeometrySign boundary = geometry.SideBoundary(1, slope, out _);
        _output.WriteLine($"y={y:R} boundary={boundary}");
        Assert.NotEqual(GeometrySign.Unresolved, boundary);
        // The crest at z=0 is attained near 0.25, above that endpoint's height near 0.22913.
        Assert.Equal(GeometrySign.Positive, geometry.Side(slope, out _, out var maximum));
        Assert.InRange(maximum.Lower, 0.2499, 0.2501);
        Assert.InRange(maximum.Upper, maximum.Lower, 0.2501);
    }

    [Theory]
    [InlineData(1e-5f, 0.0, -0.1, true)]
    [InlineData(1e-4f, 0.0, -0.1, true)]
    [InlineData(1e-5f, 1 - 1e-5, -0.3, false)]
    [InlineData(1e-4f, 1 - 1e-5, -0.3, false)]
    [InlineData(1e-5f, 0.0, -0.3, true)]
    [InlineData(1e-4f, 0.0, -0.3, true)]
    public void LyingLogBoundaryTakesTheUntaggedCandidateRole(float y, double x, double z, bool steep)
    {
        // Rows: dominated by the attained crest, at the maximum near a finite end, and a slope-gated endpoint.
        double slope = steep ? Math.Sqrt(0.84) * (1 - 2.5e-9) : 0;
        var geometry = new SupportTiltedCurvedGeometry();
        Assert.True(geometry.Begin(LyingLogBoundaryLeaf(y), x, z, 0.2));
        GeometrySign boundary = geometry.SideBoundary(1, slope, out _);
        var tagged = geometry.Side(slope, out var taggedWitness, out var taggedMaximum);
        Assert.True(geometry.Begin(LyingLogBoundaryLeaf(y), x, z, 0.2));
        var untagged = geometry.Side(slope, out var untaggedWitness, out var untaggedMaximum, equations: false);
        _output.WriteLine($"y={y:R} boundary={boundary} tagged={tagged}");
        Assert.NotEqual(GeometrySign.Unresolved, boundary);
        Assert.Equal(untagged, tagged);
        Assert.Equal(untaggedMaximum, taggedMaximum);
        Assert.Equal(untaggedWitness, taggedWitness);
        if (z == -0.1) Assert.Equal(GeometrySign.Positive, tagged);
    }

    [Fact]
    public void LyingLogBoundaryPredicatesEachRejectTheEndpoint()
    {
        var geometry = new SupportTiltedCurvedGeometry();
        // Domain: Q+R*z=-disc gives z=-2, outside the circle for every source.
        Assert.True(geometry.Begin(LyingLogBoundaryLeaf(1e-3f), 0, -0.3, 0.2));
        Assert.Equal(GeometrySign.Negative, geometry.SideBoundary(-1, 0, out _));
        // Slope: the endpoint z=-0.4 has upward normal near 0.9165, below 0.95.
        Assert.Equal(GeometrySign.Positive, geometry.SideBoundary(1, 0, out var flank));
        Assert.Equal(GeometrySign.Negative, geometry.SideBoundary(1, 0.95, out _));
        Assert.Equal(GeometrySign.Negative, geometry.Side(0.95, out _, out _));
        // Finite axis: the endpoint lies near t=1.1, beyond the end at 1, where it would be near 0.2302.
        Assert.True(geometry.Begin(LyingLogBoundaryLeaf(1e-3f), 1.1, -0.3, 0.2));
        Assert.Equal(GeometrySign.Negative, geometry.SideBoundary(1, 0, out _));
        Assert.Equal(GeometrySign.Positive, geometry.Side(0, out _, out var maximum));
        Assert.True(maximum.Lower < flank.Position.Y.Lower);
        Assert.InRange(maximum.Lower, 0.2, 0.22);
    }

    public static IEnumerable<object[]> LyingLogSeamRows()
    {
        foreach (bool capsule in new[] { false, true })
            foreach (float radius in new[] { 0.15f, 0.3f })
                foreach (bool aboutX in new[] { false, true })
                    foreach (float across in new[] { 0f, -0.25f, 0.25f })
                        foreach (float along in new[] { -0.5f, MathF.BitDecrement(-0.5f), MathF.BitIncrement(-0.5f),
                            0.5f, MathF.BitDecrement(0.5f), MathF.BitIncrement(0.5f) })
                            yield return [capsule, radius, aboutX, across, along];
    }

    // Every seam row, including the exact end seams and their adjacent binary32 axes, certifies against the
    // represented oracle. The former refusals came only from the coefficient box spanning both readings.
    [Theory]
    [MemberData(nameof(LyingLogSeamRows))]
    public void LyingLogRolesAndEndSeamsKeepRawParts(bool capsule, float radius, bool aboutX, float across, float along)
    {
        for (int rebuild = 0; rebuild < 2; rebuild++)
        {
            using var scene = new SupportFootprintScene();
            scene.Install(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0)));
            StaticHandle owner = InstallLyingLog(scene, capsule, radius,
                Quaternion.CreateFromAxisAngle(aboutX ? Vector3.UnitX : Vector3.UnitZ, MathF.PI / 2));
            var query = LyingLogQuery(radius, aboutX, across);
            query = query with { Axis = aboutX ? new(across, along) : new(along, across) };
            using var lease = scene.World.AcquireQueryReadLease();
            foreach (bool reference in new[] { false, true })
            {
                using IDisposable? evaluation = reference ? SupportReferenceEvaluation.Begin() : null;
                CheckLyingLogParts(scene, owner, FootSupport.DescribeFootprint(query), lease: lease);
                FootSupport.Find(null, scene.World, lease, LyingLogQuery(radius, aboutX, across));
                CheckLyingLogParts(scene, owner, FootSupport.DescribeFootprint(query), lease: lease);
            }
        }
    }

    [Fact]
    public void LyingLogUnprovedSourceFamilyRefusesWithoutLowerFloorFallback()
    {
        using var scene = new SupportFootprintScene();
        scene.Install(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0)));
        StaticHandle owner = scene.Install(new CylinderShape(0.25f, 1),
            new(new(0.08349365f, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)));
        var source = Proof.Installed(scene.World, owner)[0];
        var crest = SupportFootprintTiltedOracle.Crest(source, false).Position;
        var query = new FootSupportQuery(new((float)(crest.X.Middle + 0.2f), 0), 0, 0.2f, 2, 2, 0.4f);
        var request = FootSupport.DescribeFootprint(query);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256);
        var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(73UL, 1024).ToArray();
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, result.Status);
        Assert.Equal(0, result.Members);
        Assert.Equal(0, result.Witnesses);
        Assert.Equal(before, members);
        Assert.All(joins, word => Assert.Equal(73UL, word));
        Assert.Equal(SupportStatus.Refused, FootSupport.Find(null, scene.World, lease, query).Status);
    }

    static StaticHandle InstallLyingLog(SupportFootprintScene scene, bool capsule, float radius, Quaternion rotation)
    {
        Vector3 centre = new(0, radius, 0);
        return scene.Install(capsule ? new CapsuleShape(radius, 1) : new CylinderShape(radius, 1),
            new(capsule ? centre : centre - Vector3.Transform(new Vector3(0, 0.5f, 0), rotation), rotation));
    }

    static FootSupportQuery LyingLogQuery(float radius, bool aboutX, float across) =>
        new(aboutX ? new(across, 0) : new(0, across), radius == 0.3f ? 0.3f : 0f,
            0.2f, 0.4f, 0.4f, MathF.Cos(MathF.PI / 4));

    void CheckLyingLogParts(SupportFootprintScene scene, StaticHandle owner,
        SupportFootprintRequest request, bool horizontal = false, IPhysicsQueryLease? lease = null)
    {
        var parts = Proof.Installed(scene.World, owner);
        var expected = parts.Select(p => SupportFootprintOracle.Curved(p.Tangent, request))
            .OfType<FootprintOracleMember>().OrderBy(m => m.ElementId).ToArray();
        using var ownedLease = lease is null ? scene.World.AcquireQueryReadLease() : null;
        var members = new SupportFootprintMember[256];
        var result = Capability(scene.World).QuerySupportFootprint(lease ?? ownedLease!, request, members, new ulong[1024]);
        if (result.Status != CapsuleFeatureStatus.Complete)
        {
            var simulation = SimulationOf(scene.World);
            simulation.Statics.GetDescription(HandlesOf(scene.World)[owner.Value].Handle, out var description);
            var leaves = new List<SupportCurvedLeaf>();
            SupportNeighborhoodCurved.Capture(simulation, description.Shape, description.Pose, leaves);
            foreach (var leaf in leaves)
                for (int part = 0; part < leaf.Parts; part++)
                    _output.WriteLine($"axis={request.Axis} part={part} result={new SupportTiltedCurved().Measure(owner, leaf, part, request, out _)}");
        }
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var actual = members.Take(result.Members).Where(m => m.Static == owner).OrderBy(m => m.ElementId).ToArray();
        Assert.Equal(expected.Select(m => m.ElementId), actual.Select(m => m.ElementId));
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.Equal(owner, actual[i].Static);
            Assert.InRange(actual[i].WitnessCount, 0, 2);
            var source = parts.Single(p => p.Tangent.ElementId == actual[i].ElementId);
            if (!horizontal) CheckTiltedMember(actual[i], expected[i], source, request);
            else
            {
                var exact = SupportFootprintClosedFormOracle.Measure(source, request);
                Assert.Equal(exact.Witnesses.Count, actual[i].WitnessCount);
                for (int j = 0; j < actual[i].WitnessCount; j++)
                {
                    var witness = j == 0 ? actual[i].First : actual[i].Second;
                    Assert.Equal(exact.Witnesses[j].Role, witness.Role);
                    ContainsCurvedVector(witness.Position, witness.PositionErrorMetres, exact.Witnesses[j].Position);
                    ContainsCurvedVector(witness.Normal, witness.NormalError, exact.Witnesses[j].Normal);
                }
            }
        }
    }
}
