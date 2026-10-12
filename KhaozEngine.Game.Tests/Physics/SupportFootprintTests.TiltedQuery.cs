using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
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
        var source = SupportFootprintCurvedProofOracle.Installed(scene.World, owner)[0];
        var part = source.Tangent;
        double u = Math.Sqrt(part.Axis.X * part.Axis.X + part.Axis.Z * part.Axis.Z);
        var cap = part.Centre + part.Axis * part.HalfLength;
        var crest = cap + new OracleVector(-part.Axis.Y * part.Axis.X / u, u,
            -part.Axis.Y * part.Axis.Z / u) * part.Radius;
        var request = Request(x: (float)(crest.X + 0.2f), lower: -2, upper: 2, cosine: 0.4f);
        var expected = SupportFootprintOracle.Curved(part, request);
        Assert.NotNull(expected);
        Assert.True(expected.Admitted);
        Assert.True(expected.HasDiscMaximum);
        var reading = SupportFootprintTiltedOracle.Enclose(source, expected.GeometricMaximum!.Value, request, restricted: false);
        Assert.True(reading.Normal.Y.Lower > R.Zero);
        var volume = SupportFootprintOracle.Volume(request);
        Assert.True(reading.Position.Y.Lower >= R.From(volume.Bottom));
        Assert.True(reading.Position.Y.Upper <= R.From(volume.Top));
        var interior = SupportFootprintTiltedOracle.Crest(source, false).Position;
        R inward = new(1, 1024);
        Assert.True(source.Half > inward);
        interior = new(interior.X - source.Axis.X * (B)inward,
            interior.Y - source.Axis.Y * (B)inward, interior.Z - source.Axis.Z * (B)inward);
        B ix = interior.X - (B)R.From(request.Axis.X), iz = interior.Z - (B)R.From(request.Axis.Y);
        Assert.True((ix * ix + iz * iz).Upper <= R.From(request.Radius).Square());
        Assert.True(interior.Y.Lower >= R.From(volume.Bottom) && interior.Y.Upper <= R.From(volume.Top));
        B dx = reading.Position.X - (B)R.From(request.Axis.X), dz = reading.Position.Z - (B)R.From(request.Axis.Y);
        R expanded = R.From(request.Radius) + R.From(request.ContactBand);
        Assert.True((dx * dx + dz * dz).Upper <= expanded.Square());
        var perPoint = SupportFootprintOracle.PerPointSupremum(part, request);
        Assert.True(double.IsFinite(perPoint.Lower) && perPoint.Lower <= perPoint.Upper);
        Assert.True(R.From(perPoint.Upper) <= R.From(part.Centre.Y) + source.Half + source.Radius);
        using var lease = scene.World.AcquireQueryReadLease();
        var members = MemberSentinels(256); var before = (SupportFootprintMember[])members.Clone();
        var joins = Enumerable.Repeat(73UL, 1024).ToArray();
        using var region = SupportAllocationRegion.Start();
        var result = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        _output.WriteLine(TiltedStatistics(scene.World));
        Assert.Equal(CapsuleFeatureStatus.Unsupported, result.Status);
        Assert.Equal(before, members);
        Assert.All(joins, word => Assert.Equal(73UL, word));
        var (_, roots, admission) = TiltedScratch(scene.World);
        Assert.True((bool)Property(roots, "CapacityRefused") || (bool)Property(admission, "CapacityRefused"));
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var warm = Capability(scene.World).QuerySupportFootprint(lease, request, members, joins);
        long allocation = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        region.Dispose();
        _output.WriteLine($"refusal retained-array-payload={ArrayPayload(TiltedScratch(scene.World).Tilted)}");
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
        using var region = SupportAllocationRegion.Start();
        var result = capability.QuerySupportFootprint(lease, request, members, joins);
        var actual = members.Take(result.Members).Where(m => m.Static == owner).ToArray();
        string coldStatistics = TiltedStatistics(scene.World);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var warm = capability.QuerySupportFootprint(lease, request, members, joins);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        region.Dispose();
        _output.WriteLine($"{stratum} status={result.Status} {coldStatistics}");
        Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
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
            CheckTiltedMember(actual[i], expected[i], source, request);
        }
        _output.WriteLine($"{stratum} {coldStatistics}");
        Assert.Equal(CapsuleFeatureStatus.Complete, warm.Status);
        Assert.Equal(0, bytes);
    }

    static void CheckTiltedMember(SupportFootprintMember member, FootprintOracleMember reference,
        SupportFootprintCurvedProofOracle.InstalledPart source, in SupportFootprintRequest request)
    {
        Assert.Equal(reference.ElementId, member.ElementId);
        Assert.Equal(reference.Witnesses.Count, member.WitnessCount);
        Assert.Equal(reference.HasDiscMaximum, member.HasDiscMaximum);
        if (reference.GeometricMaximum is { } maximum)
        {
            var enclosed = SupportFootprintTiltedOracle.Enclose(source, maximum, request, restricted: false);
            ContainsCurvedBounds(member.DiscHeightLower, member.DiscHeightUpper, enclosed.Position.Y);
        }
        for (int role = 0; role < member.WitnessCount; role++)
        {
            var witness = role == 0 ? member.First : member.Second;
            var expected = reference.Witnesses[role];
            Assert.Equal(expected.Role, witness.Role);
            var enclosed = SupportFootprintTiltedOracle.Enclose(source, expected, request);
            ContainsCurvedVector(witness.Position, witness.PositionErrorMetres, enclosed.Position);
            ContainsCurvedVector(witness.Normal, witness.NormalError, enclosed.Normal);
            Assert.True(R.From(witness.UpwardLower) <= enclosed.Normal.Y.Lower,
                $"upward={witness.UpwardLower:R} oracle=[{enclosed.Normal.Y.Lower}, {enclosed.Normal.Y.Upper}]");
            if (expected.Role == member.First.Role)
                Assert.Equal(witness.UpwardLower, member.UpwardLower);
        }
        var selected = member.First.Role == SupportWitnessRole.Footprint ? member.First : member.Second;
        B Component(float value, float error) => new(R.From(value) - R.From(error), R.From(value) + R.From(error));
        CheckTiltedPerPointEnclosure(member.HasDiscMaximum,
            new P(Component(selected.Position.X, selected.PositionErrorMetres),
                Component(selected.Position.Y, selected.PositionErrorMetres),
                Component(selected.Position.Z, selected.PositionErrorMetres)),
            new P(Component(selected.Normal.X, selected.NormalError),
                Component(selected.Normal.Y, selected.NormalError),
                Component(selected.Normal.Z, selected.NormalError)),
            source, request);
    }

    static void CheckTiltedPerPointEnclosure(bool hasDiscMaximum, P position, P normal,
        SupportFootprintCurvedProofOracle.InstalledPart source,
        in SupportFootprintRequest request)
    {
        var part = source.Tangent;
        var reading = SupportFootprintOracle.PerPointSupremum(part, request);
        if (double.IsNegativeInfinity(reading.Upper))
        {
            Assert.Equal(double.NegativeInfinity, reading.Lower);
            return;
        }
        Assert.True(hasDiscMaximum);
        // A per-point supremum differs from SG-19's selected tangent. The attained tangent
        // bounds it below, and the independent unit-axis solid bounds every point above.
        R upper = R.From(part.Centre.Y) + R.From(part.HalfLength) + R.From(part.Radius);
        B ny = normal.Y;
        if (ny.Lower <= R.Zero)
        {
            var reference = SupportFootprintOracle.Curved(part, request)!;
            var locator = reference.Witnesses.Single(w => w.Role == SupportWitnessRole.Footprint);
            ny = SupportFootprintTiltedOracle.Enclose(source, locator, request).Normal.Y;
            Assert.True(ny.Lower > R.Zero);
        }
        B px = position.X, py = position.Y, pz = position.Z, nx = normal.X, nz = normal.Z;
        B plane = py + (nx * (px - (B)R.From(request.Axis.X)) + nz * (pz - (B)R.From(request.Axis.Y))) / ny;
        R lower = py.Lower < plane.Lower ? py.Lower : plane.Lower;
        ContainsCurvedBounds(SupportFootprintCurvedProofOracle.Publish(new(lower, upper)).Lower,
            SupportFootprintCurvedProofOracle.Publish(new(lower, upper)).Upper,
            new(R.From(reading.Lower), R.From(reading.Upper)));
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
