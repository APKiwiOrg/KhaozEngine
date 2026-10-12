using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using Xunit;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void CurvedTiltedUncertifiedSourceFamilyRefusesAtomicallyAtCellBound()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CylinderShape(0.25f, 1),
            new(new(0.08349365f, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)));
        var part = SupportFootprintCurvedProofOracle.Installed(scene.World, owner)[0].Tangent;
        double u = Math.Sqrt(part.Axis.X * part.Axis.X + part.Axis.Z * part.Axis.Z);
        var cap = part.Centre + part.Axis * part.HalfLength;
        var crest = cap + new OracleVector(-part.Axis.Y * part.Axis.X / u, u,
            -part.Axis.Y * part.Axis.Z / u) * part.Radius;
        var request = Request(x: (float)(crest.X + 0.2f), lower: -2, upper: 2, cosine: 0.4f);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256); var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(73UL, 1024).ToArray();
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        _output.WriteLine(TiltedStatistics(scene.World));
        Assert.Equal(CapsuleFeatureStatus.Unsupported, result.Status);
        Assert.Equal(before, members);
        Assert.All(joins, word => Assert.Equal(73UL, word));
        var (_, roots, admission) = TiltedScratch(scene.World);
        Assert.True((bool)Property(roots, "CapacityRefused") || (bool)Property(admission, "CapacityRefused"));
        _output.WriteLine($"refusal retained-array-payload={ArrayPayload(TiltedScratch(scene.World).Tilted)}");
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var warm = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        long allocation = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        Assert.Equal(CapsuleFeatureStatus.Unsupported, warm.Status);
        Assert.Equal(0, allocation);
        Assert.Equal(before, members);
        Assert.All(joins, word => Assert.Equal(73UL, word));
    }

    [Theory]
    [InlineData("capsule")]
    [InlineData("finite-end")]
    [InlineData("cap-boundary")]
    [InlineData("hidden")]
    [InlineData("compound")]
    [InlineData("inverted")]
    public void CurvedTiltedQueryEnclosesEveryIndependentPart(string stratum)
    {
        using var scene = new SupportFootprintScene();
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6);
        PhysicsShape shape = stratum == "capsule" ? new CapsuleShape(0.3f, 2) : new CylinderShape(0.3f, 2);
        Pose pose = new(new(0, -0.25f, 0), tilt);
        if (stratum == "inverted") pose = pose with { Orientation = Quaternion.Multiply(tilt, new Quaternion(1, 0, 0, 0)) };
        if (stratum == "compound")
        {
            shape = new CompoundShape([new(shape, pose)]);
            pose = new(new(0.1f, 0, -0.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f));
        }
        StaticHandle owner = scene.Install(shape, pose);
        var parts = SupportFootprintCurvedProofOracle.Installed(scene.World, owner).ToArray();
        var request = Request(z: 0.1f, lower: -2, upper: 2, cosine: 0.4f);
        if (stratum is "finite-end" or "cap-boundary")
        {
            var part = parts[0].Tangent;
            var cap = part.Centre + part.Axis * part.HalfLength;
            double u = Math.Sqrt(part.Axis.X * part.Axis.X + part.Axis.Z * part.Axis.Z);
            var crest = cap + new OracleVector(-part.Axis.Y * part.Axis.X / u, u,
                -part.Axis.Y * part.Axis.Z / u) * part.Radius;
            request = Request(x: (float)crest.X + (stratum == "cap-boundary" ? 0.19f : 0),
                z: stratum == "cap-boundary" ? 0.15f : 0.1f, lower: -2, upper: 2, cosine: 0.4f);
        }
        if (stratum == "hidden")
        {
            var wedge = scene.Install(new BoxShape(new(0.4f, 0.1f, 0.4f)),
                new(new(0, 2.2f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3)));
            Assert.True(scene.World.SweepCapsule(request.FootProposal.Probe, request.FootProposal.Start,
                -Vector3.UnitY, request.FootProposal.Distance, out SweepHit hit));
            Assert.Equal(wedge, hit.Body);
        }
        var expected = parts.Select(p => SupportFootprintOracle.Curved(p.Tangent, request))
            .OfType<FootprintOracleMember>().OrderBy(m => m.ElementId).ToArray();
        Assert.NotEmpty(expected);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256]; var joins = new ulong[1024];
        var capability = Capability(scene.World);
        var result = capability.QuerySupportFootprint(lease, request, members, joins);
        _output.WriteLine($"{stratum} status={result.Status} {TiltedStatistics(scene.World)}");
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
        var actual = members.Take(result.Members).Where(m => m.Static == owner).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            var source = parts.Single(p => p.Tangent.ElementId == actual[i].ElementId);
            if (stratum == "finite-end" && actual[i].HasDiscMaximum)
            {
                var exact = SupportFootprintTiltedOracle.Crest(source, source.Tangent.Surface != OracleSurface.CylinderSide);
                B dx = exact.Position.X - (B)R.From(request.Axis.X), dz = exact.Position.Z - (B)R.From(request.Axis.Y);
                Assert.True((dx * dx + dz * dz).Upper <= R.From(request.Radius).Square());
                Assert.True(exact.Normal.Y.Lower >= R.From(request.CosMaxSlope));
            }
            CheckTiltedMember(actual[i], expected[i], stratum == "finite-end" ?
                source : null);
            CheckTiltedPerPointEnclosure(actual[i], source.Tangent, request);
        }
        _output.WriteLine($"{stratum} {TiltedStatistics(scene.World)}");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var warm = capability.QuerySupportFootprint(lease, request, members, joins);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(CapsuleFeatureStatus.Complete, warm.Status);
        Assert.Equal(0, bytes);
    }

    static void CheckTiltedMember(SupportFootprintMember member, FootprintOracleMember reference,
        SupportFootprintCurvedProofOracle.InstalledPart? crest = null)
    {
        Assert.Equal(reference.ElementId, member.ElementId);
        Assert.Equal(reference.Witnesses.Count, member.WitnessCount);
        Assert.Equal(reference.HasDiscMaximum, member.HasDiscMaximum);
        if (reference.HasDiscMaximum) Assert.InRange(reference.Maximum, member.DiscHeightLower, member.DiscHeightUpper);
        for (int role = 0; role < member.WitnessCount; role++)
        {
            var witness = role == 0 ? member.First : member.Second;
            var expected = reference.Witnesses[role];
            Assert.Equal(expected.Role, witness.Role);
            if (crest is { } part && witness.Role == SupportWitnessRole.Footprint)
            {
                var exact = SupportFootprintTiltedOracle.Crest(part, part.Tangent.Surface != OracleSurface.CylinderSide);
                ContainsCurvedVector(witness.Position, witness.PositionErrorMetres, exact.Position);
                ContainsCurvedVector(witness.Normal, witness.NormalError, exact.Normal);
                ContainsCurvedBounds(member.DiscHeightLower, member.DiscHeightUpper, exact.Position.Y);
                continue;
            }
            Contains(witness.Position, witness.PositionErrorMetres, expected.Position);
            Contains(witness.Normal, witness.NormalError, expected.Normal);
        }
        static void Contains(Vector3 value, float error, OracleVector expected)
        {
            Assert.InRange(expected.X, (double)value.X - error, (double)value.X + error);
            Assert.InRange(expected.Y, (double)value.Y - error, (double)value.Y + error);
            Assert.InRange(expected.Z, (double)value.Z - error, (double)value.Z + error);
        }
    }

    static void CheckTiltedPerPointEnclosure(SupportFootprintMember member, OracleTangent part,
        in SupportFootprintRequest request)
    {
        var reading = SupportFootprintOracle.PerPointSupremum(part, request);
        if (double.IsNegativeInfinity(reading.Upper)) return;
        Assert.True(member.HasDiscMaximum);
        // A per-point supremum differs from SG-19's selected tangent. The attained tangent
        // bounds it below, and the independent unit-axis solid bounds every point above.
        R upper = R.From(part.Centre.Y) + R.From(part.HalfLength) + R.From(part.Radius);
        Assert.True(R.From(reading.Upper) <= upper);
        var witness = member.First.Role == SupportWitnessRole.Footprint ? member.First : member.Second;
        B Component(float value, float error) => new(R.From(value) - R.From(error), R.From(value) + R.From(error));
        B ny = Component(witness.Normal.Y, witness.NormalError);
        if (ny.Lower <= R.Zero) return;
        B px = Component(witness.Position.X, witness.PositionErrorMetres), py = Component(witness.Position.Y, witness.PositionErrorMetres);
        B pz = Component(witness.Position.Z, witness.PositionErrorMetres), nx = Component(witness.Normal.X, witness.NormalError);
        B nz = Component(witness.Normal.Z, witness.NormalError);
        B plane = py + (nx * (px - (B)R.From(request.Axis.X)) + nz * (pz - (B)R.From(request.Axis.Y))) / ny;
        R lower = py.Lower < plane.Lower ? py.Lower : plane.Lower;
        Assert.True(lower <= R.From(reading.Lower));
    }

    static string TiltedStatistics(object world)
    {
        var (tilted, _, admission) = TiltedScratch(world);
        object geometry = ObjectField(tilted, "_geometry");
        return $"root cells={Property(geometry, "RootCellCount")} frontier={Property(geometry, "RootLiveHighWater")} terminal={Property(geometry, "RootTerminalHighWater")} " +
            $"admission cells={Property(admission, "TotalCells")} frontier={Property(admission, "FrontierHighWater")}";
    }

    static object Property(object target, string name) => target.GetType()
        .GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static object ObjectField(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static object CurvedScratch(object world) => ObjectField(ObjectField(world, "_footprintScratch"), "_curvedKernel");

    static (object Tilted, object Roots, object Admission) TiltedScratch(object world)
    {
        object curved = CurvedScratch(world), tilted = ObjectField(curved, "_tilted");
        object geometry = ObjectField(tilted, "_geometry"), roots = ObjectField(geometry, "_roots"), admission = ObjectField(tilted, "_admission");
        return (tilted, roots, admission);
    }
}
