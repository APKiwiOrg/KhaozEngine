using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using Bounds = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using Point = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;
using Proof = KhaozEngine.Tests.Physics.SupportFootprintCurvedProofOracle;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void CurvedOraclePublishesLargeRationalBoundsOutward()
    {
        BigInteger huge = BigInteger.One << 4096;
        foreach (R value in new[] { new R(huge + 1, huge - 1), new R(-huge - 1, huge - 1), new R(1, huge) })
        {
            var bounds = Proof.Publish(SupportFootprintExactOracle.Bounds.Exact(value));
            Assert.True(double.IsFinite(bounds.Lower) && double.IsFinite(bounds.Upper));
            Assert.True(R.From(bounds.Lower) <= value && R.From(bounds.Upper) >= value);
            Assert.Equal(bounds.Lower, Math.BitDecrement(bounds.Upper));
        }
    }

    [Fact]
    public void CurvedOracleReportsWitnessAndPerPointSupremum()
    {
        Proof.Reading exact = Proof.Sphere(new R(-1, 4), new R(1, 4), new R(7, 20), new R(1, 5), new R(1, 2));
        Assert.Equal(SupportStatus.Walkable, exact.Status);
        Assert.False(exact.HasAxis);
        Assert.Equal(new R(3, 20), exact.Witness.X.Lower);
        Assert.True(exact.Witness.Y.Lower <= new R(-1, 20) && exact.Witness.Y.Upper >= new R(-1, 20));
        Assert.True(exact.Normal.Y.Lower <= new R(4, 5) && exact.Normal.Y.Upper >= new R(4, 5));
        var oracle = Proof.Publish(exact.Contribution);
        Assert.True(oracle.Lower <= -0.2 && oracle.Upper >= -0.2);
        Assert.Equal(exact.Contribution, exact.PerPoint);

        double geometricContribution = SupportFootprintExactOracle.AsDouble(
            Proof.AnisotropicContribution(new R(1, 5), R.Zero));
        double alternatePointContribution = SupportFootprintExactOracle.AsDouble(
            Proof.AnisotropicContribution(new R(9, 50), new R(3, 100)));
        var supremum = Proof.Publish(Proof.AnisotropicSupremum());
        Assert.Equal(-0.96, geometricContribution, 10);
        Assert.Equal(-0.8776, alternatePointContribution, 10);
        Assert.True(supremum.Lower >= alternatePointContribution);
        Assert.True(supremum.Upper - supremum.Lower < 1e-14);
        _output.WriteLine($"Exact decimal sphere contribution=[{oracle.Lower:R}, {oracle.Upper:R}] perPoint=[{oracle.Lower:R}, {oracle.Upper:R}]");
        _output.WriteLine($"Anisotropic geometric={geometricContribution:R} alternate={alternatePointContribution:R} supremum=[{supremum.Lower:R}, {supremum.Upper:R}]");

        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new SphereShape(0.25f), Pose.At(new(0, -0.25f, 0)));
        Proof.InstalledPart part = Assert.Single(Proof.Installed(scene.World, owner));
        SupportFootprintRequest request = Request(x: 0.35f);
        Proof.Reading installed = Proof.Sphere(new R(-1, 4), part.Radius, R.From(request.Axis.X),
            R.From(request.Radius), R.From(request.CosMaxSlope).Square());
        FootprintOracleMember member = SupportFootprintOracle.Curved(part.Tangent, request)!;
        Assert.Single(member.Witnesses);
        Assert.Equal(installed.Status, SupportFootprintOracle.Select([member], request).Status);
        CompareCurvedReadings(part.Tangent, member, request, installed);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, false)]
    [InlineData(4, false, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(4, true, false)]
    [InlineData(2, true, true)]
    [InlineData(3, true, true)]
    [InlineData(4, true, true)]
    [InlineData(5, true, false)]
    [InlineData(5, true, true)]
    public void CurvedOracleInstalledPoseCrestsHaveIndependentExactReadings(int kind, bool tilted, bool compound)
    {
        using var scene = new SupportFootprintScene();
        PhysicsShape shape = kind switch
        {
            0 => new SphereShape(0.25f),
            1 => new CapsuleShape(0.25f, 0),
            2 => new CapsuleShape(0.25f, 0.5f),
            _ => new CylinderShape(0.25f, 0.5f)
        };
        Quaternion rotation = tilted ? Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)) : Quaternion.Identity;
        Pose pose = new(new(0, -0.25f, 0), rotation);
        if (compound)
        {
            shape = new CompoundShape([new(shape, new Pose(new(0.125f, 0.125f, -0.125f),
                Quaternion.Normalize(new Quaternion(0.2f, 0.1f, -0.1f, 0.95f))))]);
        }
        // Inverting local Y makes the independent bottom cap the upward-facing raw part.
        if (kind == 4) pose = pose with { Orientation = Quaternion.Multiply(rotation, new Quaternion(1, 0, 0, 0)) };
        StaticHandle owner = scene.Install(shape, pose);
        Proof.InstalledPart part = Proof.Installed(scene.World, owner).Single(p => kind switch
        {
            3 => p.Tangent.Surface == OracleSurface.CylinderTop,
            4 => p.Tangent.Surface == OracleSurface.CylinderBottom,
            5 => p.Tangent.Surface == OracleSurface.CylinderSide,
            _ => p.Tangent.Surface == OracleSurface.Round
        });
        OracleVector end = part.Tangent.Centre + part.Tangent.Axis *
            (part.Tangent.Axis.Y >= 0 ? part.Tangent.HalfLength : -part.Tangent.HalfLength);
        SupportFootprintRequest request = Request(x: (float)end.X, z: (float)end.Z, radius: 0.5f,
            cosine: 0.01f, lower: -2, upper: 2);
        Proof.Reading exact = Proof.Crest(part, request);
        FootprintOracleMember member = SupportFootprintOracle.Curved(part.Tangent, request)!;
        Assert.NotNull(member);
        Assert.Contains(member.Witnesses, w => w.Role == SupportWitnessRole.Footprint);
        CompareCurvedReadings(part.Tangent, member, request, exact);
        Assert.Equal(exact.Status, SupportFootprintOracle.Select([member], request).Status);
        if (kind is 3 or 4)
        {
            var other = Proof.Installed(scene.World, owner).Single(p => p.Tangent.Surface ==
                (kind == 3 ? OracleSurface.CylinderBottom : OracleSurface.CylinderTop));
            FootprintOracleMember? lower = SupportFootprintOracle.Curved(other.Tangent, request);
            Assert.NotNull(lower);
            Assert.Empty(lower.Witnesses);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CurvedOracleHorizontalBarrelEndAndAxisMissStrata(bool cylinder, bool beyondEnd)
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(cylinder ? new CylinderShape(0.25f, 1) : new CapsuleShape(0.25f, 1),
            new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        Proof.InstalledPart part = Proof.Installed(scene.World, owner).First();
        float axisZ = (float)part.Tangent.Centre.Z + (beyondEnd ? 0.75f : 0);
        SupportFootprintRequest request = Request(x: 0.35f, z: axisZ, radius: beyondEnd ? 0.5f : 0.2f);
        FootprintOracleMember member = SupportFootprintOracle.Curved(part.Tangent, request)!;
        Assert.NotNull(member);
        Assert.Single(member.Witnesses);
        Assert.True(member.WitnessInsideClosedDisc);
        Assert.Equal(SupportWitnessRole.Footprint, member.Witnesses[0].Role);
        Assert.InRange(part.Tangent.Closest(member.Witnesses[0].Position).Distance, 0, 1e-12);
        Proof.Reading exact;
        if (!beyondEnd)
        {
            exact = Proof.Sphere(R.Zero, part.Radius, R.From(request.Axis.X),
                R.From(request.Radius), R.From(request.CosMaxSlope).Square());
            exact = exact with { Witness = exact.Witness with { Z = part.Centre.Z } };
        }
        else exact = Proof.Crest(part, request);
        CompareCurvedReadings(part.Tangent, member, request, exact);
    }

    [Fact]
    public void CurvedOracleHorizontalCylinderFiniteEndAndDiscBoundaryConstrainTheMaximum()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CylinderShape(0.25f, 1),
            new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        Proof.InstalledPart part = Proof.Installed(scene.World, owner).First();
        Assert.Equal(OracleSurface.CylinderSide, part.Tangent.Surface);
        Assert.Equal(new Point(R.Zero, R.Zero, R.One), part.Axis);
        Assert.Equal(new R(1, 2), part.Half);
        SupportFootprintRequest request = Request(x: (float)part.Tangent.Centre.X + 0.35f,
            z: (float)part.Tangent.Centre.Z + 0.75f, radius: 0.3f);
        R axisX = R.From(request.Axis.X), axisZ = R.From(request.Axis.Y);
        R centreX = part.Centre.X.Lower, centreY = part.Centre.Y.Lower, centreZ = part.Centre.Z.Lower;
        R endZ = centreZ + part.Half, delta = axisZ - endZ, offset = axisX - centreX;
        R discSquare = R.From(request.Radius).Square();
        Assert.Equal(new R(1, 4), delta);
        Bounds available = Bounds.Root(discSquare - delta.Square());
        Assert.True(available.Upper < offset);
        Bounds lateral = (Bounds)offset - available;
        Bounds heightSquare = (Bounds)part.Radius.Square() - lateral * lateral;
        Bounds height = new(Bounds.Root(heightSquare.Lower).Lower, Bounds.Root(heightSquare.Upper).Upper);
        var point = new Point((Bounds)centreX + lateral, (Bounds)centreY + height, endZ);
        var normal = new Point(lateral / (Bounds)part.Radius, height / (Bounds)part.Radius, R.Zero);
        Bounds tangent = point.Y + normal.X * (point.X - (Bounds)axisX) / normal.Y;
        Assert.True(tangent.Upper < point.Y.Lower);
        Assert.True(height.Upper < part.Radius);
        Assert.True(normal.Y.Upper < R.From(request.CosMaxSlope));

        // Height and tangent decrease with lateral distance. The finite end leaves sqrt(r*r-delta*delta)
        // of disc width, so the nearest feasible lateral point attains both maxima. The tangent wins min.
        Bounds perPoint = (Bounds)centreY + ((Bounds)part.Radius.Square() - (Bounds)offset * lateral) / height;
        var exact = new Proof.Reading(point, normal, tangent, perPoint, SupportStatus.Steep, false);
        FootprintOracleMember member = SupportFootprintOracle.Curved(part.Tangent, request)!;
        Assert.NotNull(member);
        FootprintOracleWitness witness = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, witness.Role);
        Assert.Equal(SupportFootprintExactOracle.AsDouble(endZ), witness.Position.Z);
        Assert.True(member.WitnessInsideClosedDisc);
        double dx = witness.Position.X - request.Axis.X, dz = witness.Position.Z - request.Axis.Y;
        double distanceSquare = dx * dx + dz * dz;
        Assert.True(distanceSquare <= (double)request.Radius * request.Radius);
        Assert.InRange(Math.Abs(distanceSquare - (double)request.Radius * request.Radius), 0, 1e-12);
        CompareCurvedReadings(part.Tangent, member, request, exact);
        Assert.Equal(exact.Status, SupportFootprintOracle.Select([member], request).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedOracleHorizontalCapsuleEndAndBarrelSeamAreAttained(bool seam)
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CapsuleShape(0.25f, 1),
            new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        Proof.InstalledPart part = Assert.Single(Proof.Installed(scene.World, owner));
        SupportFootprintRequest request = Request(z: seam ? 0.5f : 0.85f);
        Proof.Reading exact = Proof.Sphere(R.Zero, part.Radius, R.From(request.Axis.Y) - part.Half,
            R.From(request.Radius), R.From(request.CosMaxSlope).Square());
        exact = exact with
        {
            Witness = new(exact.Witness.Z, exact.Witness.Y, exact.Witness.X + (SupportFootprintExactOracle.Bounds)part.Half),
            Normal = new(exact.Normal.Z, exact.Normal.Y, exact.Normal.X)
        };
        FootprintOracleMember member = SupportFootprintOracle.Curved(part.Tangent, request)!;
        Assert.NotNull(member);
        Assert.Equal(seam ? 2 : 1, member.Witnesses.Count);
        CompareCurvedReadings(part.Tangent, member, request, exact);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CurvedOracleHiddenPartsDoNotUseTheWedgesWitness(int kind)
    {
        using var scene = new SupportFootprintScene();
        PhysicsShape shape = kind switch
        {
            0 => new SphereShape(0.25f),
            1 => new CapsuleShape(0.25f, 0),
            2 or 3 => new CapsuleShape(0.25f, 0.5f),
            _ => new CylinderShape(0.25f, 0.5f)
        };
        bool lying = kind is 3 or 5;
        Pose pose = new(new(0, kind is 2 or 4 ? -0.5f : -0.25f, 0),
            lying ? new Quaternion(0.5f, 0.5f, 0.5f, 0.5f) : Quaternion.Identity);
        StaticHandle owner = scene.Install(shape, pose);
        var parts = Proof.Installed(scene.World, owner);
        SupportFootprintRequest request = Request();
        FootprintOracleMember[] before = parts.Select(p => SupportFootprintOracle.Curved(p.Tangent, request))
            .OfType<FootprintOracleMember>().ToArray();
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3);
        StaticHandle wedge = scene.Install(new BoxShape(new(0.1f, 0.1f, 0.3f)),
            new(new Vector3(0.2f, 0.25f, 0) + Vector3.Transform(new Vector3(0, -0.1f, 0), tilt), tilt));
        Assert.True(scene.World.SweepCapsule(request.FootProposal.Probe, request.FootProposal.Start,
            -Vector3.UnitY, request.FootProposal.Distance, out SweepHit hit));
        Assert.Equal(wedge, hit.Body);
        FootprintOracleMember[] after = parts.Select(p => SupportFootprintOracle.Curved(p.Tangent, request))
            .OfType<FootprintOracleMember>().ToArray();
        Assert.Equal(before.Select(p => p.Witnesses.ToArray()), after.Select(p => p.Witnesses.ToArray()));
        Assert.Contains(scene.Faces, f => f.Normal.Y > 0 && f.Normal.Y < request.CosMaxSlope &&
            SupportFootprintOracle.Polygon(f, request) is not null);
        FootprintOracleSupport support = SupportFootprintOracle.Select(after, request);
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.Equal(0, support.Height, 10);
        Assert.Equal(owner, support.Static);
        foreach (FootprintOracleMember member in after.Where(m => m.Witnesses.Any(w => w.Normal.Y > 0)))
        {
            OracleTangent part = parts.Single(p => p.Tangent.ElementId == member.ElementId).Tangent;
            var reading = SupportFootprintOracle.PerPointSupremum(part, request);
            Assert.True(reading.Lower <= 0 && reading.Upper >= 0);
            Assert.True(reading.Upper - reading.Lower <= 1e-7);
            _output.WriteLine($"Hidden kind={kind} part={member.ElementId} geometric=[0, 0] perPoint=[{reading.Lower:R}, {reading.Upper:R}]");
        }
        var all = scene.Faces.Select(f => SupportFootprintOracle.Polygon(f, request))
            .OfType<FootprintOracleMember>().Concat(after).ToArray();
        Assert.Equal(owner, SupportFootprintOracle.Select(all, request).Static);
    }

    [Fact]
    public void CurvedOracleTiltedSideHasAnExactHighestWalkableBoundaryCertificate()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CapsuleShape(0.25f, 1),
            new(Vector3.Zero, new Quaternion(0, 0, 0.5f, MathF.Sqrt(3) / 2)));
        Proof.InstalledPart capsule = Assert.Single(Proof.Installed(scene.World, owner));
        Proof.InstalledPart part = capsule with { Tangent = capsule.Tangent with { Surface = OracleSurface.CylinderSide } };
        SupportFootprintRequest request = Request(z: 0.29f, cosine: 0.8f);
        Proof.Boundary exact = Proof.WalkableBoundary(part, request);
        Assert.True(exact.Derivative.Upper < R.Zero);
        Assert.True(exact.AxialCoordinate.Lower > -part.Half && exact.AxialCoordinate.Upper < part.Half);
        Assert.Equal(R.From(request.CosMaxSlope), exact.Normal.Y.Lower);
        FootprintOracleMember member = SupportFootprintOracle.Curved(part.Tangent, request)!;
        FootprintOracleWitness witness = Assert.Single(member.Witnesses);
        Assert.InRange((witness.Position - exact.Witness.Middle).Length, 0, 1e-7);
        Assert.InRange((witness.Normal - exact.Normal.Middle).Length, 0, 1e-7);
        Assert.True(member.Maximum > SupportFootprintExactOracle.AsDouble(exact.Witness.Y.Upper));
        Assert.Equal(SupportStatus.Walkable, SupportFootprintOracle.Select([member], request).Status);
        var geometric = Proof.Publish(exact.Contribution);
        var alternate = SupportFootprintOracle.PerPointSupremum(part.Tangent, request);
        Assert.True(alternate.Upper >= geometric.Lower);
        Assert.True(alternate.Lower <= alternate.Upper && alternate.Upper - alternate.Lower <= 1e-7);
        _output.WriteLine($"Slope equality geometric=[{geometric.Lower:R}, {geometric.Upper:R}] perPoint=[{alternate.Lower:R}, {alternate.Upper:R}]");
    }

    [Fact]
    public void CurvedOracleTiltedCapAndSideShareAnAttainedRimWithIndependentNormals()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CylinderShape(0.25f, 0.5f),
            new(Vector3.Zero, Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f))));
        var parts = Proof.Installed(scene.World, owner);
        Proof.InstalledPart side = parts.Single(p => p.Tangent.Surface == OracleSurface.CylinderSide);
        Proof.InstalledPart cap = parts.Single(p => p.Tangent.Surface == OracleSurface.CylinderTop);
        OracleVector end = cap.Tangent.Centre + cap.Tangent.Axis * cap.Tangent.HalfLength;
        SupportFootprintRequest request = Request(x: (float)end.X, z: (float)end.Z, radius: 0.5f,
            cosine: 0.01f, lower: -2, upper: 2);
        Proof.Reading sideReading = Proof.Crest(side, request), capReading = Proof.Crest(cap, request);
        Assert.Equal(sideReading.Witness, capReading.Witness);
        Assert.NotEqual(sideReading.Normal, capReading.Normal);
        Assert.NotEqual(side.Tangent.ElementId, cap.Tangent.ElementId);
        CompareCurvedReadings(side.Tangent, SupportFootprintOracle.Curved(side.Tangent, request)!, request, sideReading);
        CompareCurvedReadings(cap.Tangent, SupportFootprintOracle.Curved(cap.Tangent, request)!, request, capReading);
    }

    [Fact]
    public void CurvedOracleVerticalCapsRemainContextAndFlatCapDiscTangencySupports()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle horizontal = scene.Install(new CylinderShape(0.25f, 0.5f),
            new(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)));
        SupportFootprintRequest request = Request(radius: 0.5f);
        foreach (var cap in Proof.Installed(scene.World, horizontal).Where(p => p.Tangent.Surface != OracleSurface.CylinderSide))
        {
            FootprintOracleMember member = SupportFootprintOracle.Curved(cap.Tangent, request)!;
            Assert.NotNull(member);
            Assert.Contains(member.Witnesses, w => w.Role == SupportWitnessRole.Footprint);
            Assert.All(member.Witnesses, w => Assert.Equal(0, w.Normal.Y));
            FootprintOracleWitness highest = member.Witnesses.Single(w => w.Role == SupportWitnessRole.Footprint);
            Assert.Equal(0.25, highest.Position.Y);
            Assert.Equal(SupportStatus.None, SupportFootprintOracle.Select([member], request).Status);
            Assert.Equal((double.NegativeInfinity, double.NegativeInfinity), SupportFootprintOracle.PerPointSupremum(cap.Tangent, request));
            _output.WriteLine($"Vertical cap {cap.Tangent.ElementId} attainedHeight=0.25 geometric=none perPoint=empty");
        }
        StaticHandle upright = scene.Install(new CylinderShape(0.25f, 0.5f), Pose.At(new(0, -0.5f, 0)));
        Proof.InstalledPart flat = Proof.Installed(scene.World, upright).Single(p => p.Tangent.Surface == OracleSurface.CylinderTop);
        request = Request(x: 0.5f, radius: 0.25f, cosine: 1);
        FootprintOracleMember tangent = SupportFootprintOracle.Curved(flat.Tangent, request)!;
        FootprintOracleWitness witness = Assert.Single(tangent.Witnesses);
        Assert.Equal(new OracleVector(0.25, 0, 0), witness.Position);
        Assert.Equal(new OracleVector(0, 1, 0), witness.Normal);
        Assert.Equal(SupportStatus.Walkable, SupportFootprintOracle.Select([tangent], request).Status);
        var reading = SupportFootprintOracle.PerPointSupremum(flat.Tangent, request);
        Assert.True(reading.Lower <= 0 && reading.Upper >= 0);
        _output.WriteLine($"Flat cap tangency geometric=[0, 0] perPoint=[{reading.Lower:R}, {reading.Upper:R}]");
    }

    [Fact]
    public void CurvedOracleTiltedCapDiscIntersectionIsTheOnlyMaximumStratum()
    {
        using var scene = new SupportFootprintScene();
        StaticHandle owner = scene.Install(new CylinderShape(0.25f, 0.5f), new(new(0.45f, -0.4f, 0.25f),
            Quaternion.Normalize(new Quaternion(0, 0, MathF.Sqrt(0.1f), MathF.Sqrt(0.9f)))));
        Proof.InstalledPart cap = Proof.Installed(scene.World, owner).Single(p => p.Tangent.Surface == OracleSurface.CylinderTop);
        SupportFootprintRequest request = Request();
        FootprintOracleMember member = SupportFootprintOracle.Curved(cap.Tangent, request)!;
        FootprintOracleWitness witness = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, witness.Role);
        OracleVector centre = cap.Tangent.Centre + cap.Tangent.Axis * cap.Tangent.HalfLength;
        Assert.InRange(Math.Abs((witness.Position - centre).Length - cap.Tangent.Radius), 0, 1e-7);
        Assert.InRange(Math.Abs(Math.Sqrt(witness.Position.X * witness.Position.X + witness.Position.Z * witness.Position.Z) - request.Radius), 0, 1e-7);
        OracleVector uphill = (new OracleVector(0, 1, 0) - cap.Tangent.Axis * cap.Tangent.Axis.Y).Unit;
        OracleVector crest = centre + uphill * cap.Tangent.Radius;
        Assert.True(crest.X * crest.X + crest.Z * crest.Z > (double)request.Radius * request.Radius);
        var discUphill = new OracleVector(request.Radius,
            Proof.CapPoint(cap, R.From(request.Radius), R.Zero).Y.Middle, 0);
        Assert.True((discUphill - centre).Length > cap.Tangent.Radius);
        var feasible = Proof.CapPoint(cap, new R(1, 10), new R(1, 10));
        var capCentre = Proof.CapPoint(cap, R.From(centre.X), R.From(centre.Z));
        var dx = feasible.X - capCentre.X;
        var dy = feasible.Y - capCentre.Y;
        var dz = feasible.Z - capCentre.Z;
        Assert.True((dx * dx + dy * dy + dz * dz).Upper < cap.Radius.Square());
        var plane = Proof.CapPoint(cap, R.Zero, R.Zero).Y;
        Assert.True(feasible.Y.Lower > plane.Upper);
        var certified = Proof.Publish(plane);
        var alternate = SupportFootprintOracle.PerPointSupremum(cap.Tangent, request);
        Assert.True(alternate.Lower <= certified.Upper && alternate.Upper >= certified.Lower);
        Assert.InRange(SupportFootprintOracle.Select([member], request).Height,
            certified.Lower - 1e-12, certified.Upper + 1e-12);
        _output.WriteLine($"Tilted cap intersection geometric=[{certified.Lower:R}, {certified.Upper:R}] perPoint=[{certified.Lower:R}, {certified.Upper:R}] numerical=[{alternate.Lower:R}, {alternate.Upper:R}]");
    }

    void CompareCurvedReadings(OracleTangent part, FootprintOracleMember member,
        SupportFootprintRequest request, Proof.Reading exact)
    {
        FootprintOracleWitness witness = member.Witnesses.Single(w => w.Role == SupportWitnessRole.Footprint);
        Assert.InRange((witness.Position - exact.Witness.Middle).Length, 0, 1e-7);
        Assert.InRange((witness.Normal - exact.Normal.Middle).Length, 0, 1e-7);
        Assert.True(member.WitnessInsideClosedDisc);
        Assert.InRange(part.Closest(witness.Position).Distance, 0, 1e-7);
        (double lower, double upper) = Proof.Publish(exact.Contribution);
        double plane = witness.Position.Y + (witness.Normal.X * (witness.Position.X - request.Axis.X) +
            witness.Normal.Z * (witness.Position.Z - request.Axis.Y)) / witness.Normal.Y;
        Assert.InRange(Math.Min(plane, witness.Position.Y), lower - 1e-12, upper + 1e-12);
        var alternate = SupportFootprintOracle.PerPointSupremum(part, request);
        var reference = Proof.Publish(exact.PerPoint);
        Assert.True(alternate.Lower <= reference.Upper && alternate.Upper >= reference.Lower,
            $"Alternate [{alternate.Lower:R}, {alternate.Upper:R}] misses [{reference.Lower:R}, {reference.Upper:R}].");
        Assert.True(alternate.Upper - alternate.Lower <= 1e-7);
        _output.WriteLine($"{part.Surface} axis={part.Axis} part={part.ElementId} geometric=[{lower:R}, {upper:R}] perPoint=[{reference.Lower:R}, {reference.Upper:R}] numerical=[{alternate.Lower:R}, {alternate.Upper:R}]");
    }

    [Fact]
    public void CurvedOracleUprightSideRetainsItsAttainedZeroNormalBoundary()
    {
        var side = new OracleTangent(new StaticHandle(0), 0, OracleSurface.CylinderSide,
            new(0, 0, 0), new(0, 1, 0), 0.25, 0.5);
        SupportFootprintRequest request = Request(x: 0.5f, radius: 0.25f);
        FootprintOracleMember member = SupportFootprintOracle.Curved(side, request)!;
        Assert.NotNull(member);
        Assert.True(member.HasDiscMaximum);
        FootprintOracleWitness witness = Assert.Single(member.Witnesses);
        Assert.Equal(SupportWitnessRole.Footprint, witness.Role);
        Assert.Equal(new OracleVector(0.25, 0.5, 0), witness.Position);
        Assert.Equal(new OracleVector(1, 0, 0), witness.Normal);
        Assert.Equal(SupportStatus.None, SupportFootprintOracle.Select([member], request).Status);
        Assert.Equal((double.NegativeInfinity, double.NegativeInfinity),
            SupportFootprintOracle.PerPointSupremum(side, request));
        _output.WriteLine("Upright side tangency geometric=none perPoint=empty");
        FootprintOracleMember axis = SupportFootprintOracle.Curved(side, Request(x: 0.25f))!;
        Assert.Equal(SupportWitnessRole.Axis, axis.Witnesses[0].Role);
        Assert.Equal(witness.Position, axis.Witnesses[0].Position);
    }
}
