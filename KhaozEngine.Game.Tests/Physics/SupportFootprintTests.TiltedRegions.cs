using System;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    // The old coefficient box spanned the slope gate. Each represented column on either side of it, and the gate
    // column itself, now certifies its own crest against its own exact oracle.
    [Theory]
    [InlineData(0f)]
    [InlineData(0.1f)]
    public void CurvedTiltedSideAcrossSlopeGateCertifiesEachRepresentedColumn(float z)
    {
        const float component = 0.70710677f;
        var request = Request(z: z, lower: -2, upper: 2);
        foreach (float y in new[] { MathF.BitDecrement(component), component, MathF.BitIncrement(component) })
        {
            var column = new Vector3(component, y, 0);
            var kernel = new SupportTiltedCurved(NewCurvedWork());
            GeometrySign result = kernel.Measure(new(1), ColumnLeaf(column), SupportNeighborhoodCurved.Side, request,
                out var member);
            var part = ColumnPart(column, Vector3.Zero, OracleSurface.CylinderSide, 0);
            var reference = SupportFootprintOracle.Curved(part.Tangent, request)!;
            _output.WriteLine($"y={y:R} result={result} capacity={(bool)Property(ObjectField(ObjectField(kernel, "_geometry"), "_roots"), "CapacityRefused")}");
            Assert.Equal(GeometrySign.Positive, result);
            CheckTiltedMember(member, reference, part, request);
            var selected = reference.Witnesses.Single(w => w.Role == SupportWitnessRole.Footprint);
            var exact = SupportFootprintTiltedOracle.Enclose(part, selected, request);
            Assert.Equal(y <= component, exact.Normal.Y.Lower >= R.From(request.CosMaxSlope));
        }
    }

    [Fact]
    public void CurvedTiltedCapAcrossSlopeGateKeepsItsGeometricWitness()
    {
        const float component = 0.70710677f;
        var request = Request(x: 0.35355338f, lower: -2, upper: 2);
        foreach (float y in new[] { MathF.BitDecrement(component), MathF.BitIncrement(component) })
        {
            var column = new Vector3(component, y, 0);
            var kernel = new SupportTiltedCurved(NewCurvedWork());
            Assert.Equal(GeometrySign.Positive, kernel.Measure(new(1), ColumnLeaf(column), SupportNeighborhoodCurved.TopCap,
                request, out var member));
            var part = ColumnPart(column, Vector3.Zero, OracleSurface.CylinderTop, 1);
            CheckTiltedMember(member, SupportFootprintOracle.Curved(part.Tangent, request)!, part, request);
        }
    }

    // An exactly horizontal represented column has a signed zero Y. Its cap orientation is certified exactly.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedTiltedSignedZeroYKeepsCertifiedComponentEquality(bool negative)
    {
        var kernel = new SupportTiltedCurvedGeometry(NewCurvedWork());
        Assert.True(kernel.Begin(ColumnLeaf(new(1, negative ? -0f : 0f, 0)), 0, 0, 0.2f));
        Assert.True(kernel.CapUniformlyUp(2));
        Assert.Equal(0, kernel.CapUpward(2).Lower);
        Assert.Equal(0, kernel.CapUpward(3).Upper);
    }

    // The upper cap role changes sign between these two tiny tilts. Each represented column decides its own role.
    [Fact]
    public void CurvedTiltedCapWhoseUpperRoleChangesWithTheColumnCertifiesEachColumn()
    {
        var request = Request(x: 0.6f, lower: -2, upper: 2);
        foreach (float y in new[] { -1e-8f, 1e-8f })
        {
            var column = new Vector3(1, y, 0);
            var part = ColumnPart(column, Vector3.Zero, OracleSurface.CylinderTop, 1);
            var reference = SupportFootprintOracle.Curved(part.Tangent, request);
            Assert.NotNull(reference);
            Assert.True(reference.Admitted);
            Assert.Equal(y > 0, reference.HasDiscMaximum);
            var kernel = new SupportTiltedCurved(NewCurvedWork());
            Assert.Equal(GeometrySign.Positive, kernel.Measure(new(1), ColumnLeaf(column), SupportNeighborhoodCurved.TopCap,
                request, out var member));
            Assert.False((bool)Property(ObjectField(kernel, "_admission"), "CapacityRefused"));
            CheckTiltedMember(member, reference, part, request);
        }
    }

    // The axis meets the finite end within a few nanometres. The slanted end rim of each tiny tilt decides the
    // axis role exactly, as its own oracle does, without exhausting root storage.
    [Fact]
    public void CurvedTiltedAxisAtFiniteEndCertifiesEachColumn()
    {
        var request = Request(lower: -2, upper: 2);
        var centre = new Vector3(-0.5f, -0.25f, 0);
        foreach (float y in new[] { -1e-8f, 1e-8f })
        {
            var column = new Vector3(1, y, 0);
            var kernel = new SupportTiltedCurvedGeometry(NewCurvedWork());
            Assert.True(kernel.Begin(ColumnLeaf(column, centre), 0, 0, 0.2f));
            GeometrySign axis = kernel.Axis(false, 0, out _);
            _output.WriteLine($"y={y:R} axis={axis} live={kernel.RootLiveHighWater}");
            Assert.InRange(kernel.RootLiveHighWater, 0, SupportCurvedRootRegions.MaximumLiveCells - 1);
            var reference = SupportFootprintOracle.Curved(ColumnPart(column, centre, OracleSurface.CylinderSide, 0).Tangent,
                request);
            bool expected = reference is not null && reference.Witnesses.Any(w => w.Role == SupportWitnessRole.Axis);
            Assert.Equal(expected ? GeometrySign.Positive : GeometrySign.Negative, axis);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedTiltedCapAcrossHorizontalBoundaryRetainsRawNormal(bool signed)
    {
        double x = signed ? -0.5 : -0.5 / Math.Sqrt(1.25);
        var request = Request(x: (float)x, z: 0.1f, lower: -2, upper: 2);
        foreach (float sy in signed ? new[] { -1e-8f, 1e-8f } : new[] { 0.5f })
        {
            var column = new Vector3(1, sy, 0);
            var kernel = new SupportTiltedCurvedGeometry(NewCurvedWork());
            Assert.True(kernel.Begin(ColumnLeaf(column), x, 0.1, 0.2));
            Assert.Equal(GeometrySign.Positive, kernel.Cap(SupportNeighborhoodCurved.BottomCap, false, out var witness, out var maximum));
            var part = ColumnPart(column, Vector3.Zero, OracleSurface.CylinderBottom, 1);
            P axis = part.Axis;
            B u = (B)R.One / B.Root(R.One + R.From(sy).Square()), k = axis.Y;
            P normal = new(-axis.X, -axis.Y, -axis.Z);
            P point = new(-axis.X * (B)R.From(0.5) - k * (B)R.From(0.25),
                -axis.Y * (B)R.From(0.5) + u * (B)R.From(0.25), R.Zero);
            ContainsCurvedBounds(witness.Position.X.Lower, witness.Position.X.Upper, point.X);
            ContainsCurvedBounds(witness.Position.Y.Lower, witness.Position.Y.Upper, point.Y);
            ContainsCurvedBounds(witness.Normal.X.Lower, witness.Normal.X.Upper, normal.X);
            ContainsCurvedBounds(witness.Normal.Y.Lower, witness.Normal.Y.Upper, normal.Y);
            ContainsCurvedBounds(maximum.Lower, maximum.Upper, point.Y);
            CheckTiltedPerPointEnclosure(true, CurvedPoint(witness.Position), CurvedPoint(witness.Normal), part, request);
        }
    }

    // Opposite tiny tilts each certify their own side crest.
    [Theory]
    [InlineData(0f)]
    [InlineData(0.1f)]
    public void CurvedTiltedSignedColumnsEncloseTheirOwnOrientation(float z)
    {
        var request = Request(z: z, lower: -2, upper: 2);
        var centre = new Vector3(0, -0.25f, 0);
        foreach (float y in new[] { -1e-8f, 1e-8f })
        {
            var column = new Vector3(1, y, 0);
            var kernel = new SupportTiltedCurvedGeometry(NewCurvedWork());
            Assert.True(kernel.Begin(ColumnLeaf(column, centre), 0, z, 0.2f));
            Assert.Equal(GeometrySign.Positive, kernel.Side(0, out var witness, out var maximum));
            var part = ColumnPart(column, centre, OracleSurface.CylinderSide, 0);
            var locator = SupportFootprintOracle.Curved(part.Tangent, request)!.GeometricMaximum!.Value;
            var exact = SupportFootprintTiltedOracle.Enclose(part, locator, request, restricted: false);
            ContainsCurvedBounds(witness.Position.X.Lower, witness.Position.X.Upper, exact.Position.X);
            ContainsCurvedBounds(witness.Position.Y.Lower, witness.Position.Y.Upper, exact.Position.Y);
            ContainsCurvedBounds(witness.Position.Z.Lower, witness.Position.Z.Upper, exact.Position.Z);
            ContainsCurvedBounds(witness.Normal.X.Lower, witness.Normal.X.Upper, exact.Normal.X);
            ContainsCurvedBounds(witness.Normal.Y.Lower, witness.Normal.Y.Upper, exact.Normal.Y);
            ContainsCurvedBounds(witness.Normal.Z.Lower, witness.Normal.Z.Upper, exact.Normal.Z);
            ContainsCurvedBounds(maximum.Lower, maximum.Upper, exact.Position.Y);
            // Off the crest, the binary64 per-point sampler cannot resolve a 1e-8 tilt within the tight enclosure.
            if (z == 0)
                CheckTiltedPerPointEnclosure(true, CurvedPoint(witness.Position), CurvedPoint(witness.Normal), part, request);
        }
    }

    // A synthetic cylinder at one represented source column. Tilted kernels read the column, not Axis.
    static SupportCurvedLeaf ColumnLeaf(Vector3 column, Vector3 centre = default, double half = 0.5) =>
        new(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(centre).Bounds, default, 0.25, half, default, default,
            default, column);

    // The exact oracle part of the same column, with its independent rational normalization.
    static SupportFootprintCurvedProofOracle.InstalledPart ColumnPart(Vector3 column, Vector3 centre,
        OracleSurface surface, int elementId, double radius = 0.25, double half = 0.5)
    {
        R sx = R.From(column.X), sy = R.From(column.Y), sz = R.From(column.Z);
        B length = B.Root(sx.Square() + sy.Square() + sz.Square());
        P axis = new((B)sx / length, (B)sy / length, (B)sz / length);
        P exactCentre = P.Exact(InstalledPoseOracle.V.From(centre));
        var tangent = new OracleTangent(new KhaozEngine.Physics.StaticHandle(1), elementId, surface, exactCentre.Middle,
            axis.Middle, radius, half)
        { ExactAxis = axis };
        return new(tangent, exactCentre, axis, R.From(radius), R.From(half));
    }

    [Theory]
    [InlineData(0, 0.1f, 0.4f)]
    [InlineData(-0.24f, 0.1f, 0.4f)]
    public void CurvedTiltedSideRegionsContainOracleWitness(float x, float z, float cosine)
    {
        var pose = new RigidPose(new(0, -0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6));
        using var scene = new SupportFootprintScene();
        var owner = scene.Install(new CylinderShape(0.3f, 2), new(pose.Position, pose.Orientation));
        var installed = SupportFootprintCurvedProofOracle.Installed(scene.World, owner)[0];
        var centre = installed.Centre.Middle;
        pose.Position = new((float)centre.X, (float)centre.Y, (float)centre.Z);
        var leaf = RepresentedLeaf(SupportCurvedShape.Cylinder, pose, 0.3f, 1);
        var kernel = new SupportTiltedCurvedGeometry(NewCurvedWork());
        Assert.True(kernel.Begin(leaf, x, z, 0.2f));
        GeometrySign status = kernel.Side(cosine, out var witness, out var maximum);
        _output.WriteLine($"status={status} root cells={kernel.RootCellCount} frontier={kernel.RootLiveHighWater} terminal={kernel.RootTerminalHighWater}");
        Assert.Equal(GeometrySign.Positive, status);
        var part = installed.Tangent;
        var request = Request(x: x, z: z, cosine: cosine, lower: -2, upper: 2);
        var reference = SupportFootprintOracle.Curved(part, request)!;
        var point = reference.Witnesses.Single(w => w.Role == SupportWitnessRole.Footprint);
        var exact = SupportFootprintTiltedOracle.Enclose(installed, point, request);
        ContainsCurvedBounds(witness.Position.X.Lower, witness.Position.X.Upper, exact.Position.X);
        ContainsCurvedBounds(witness.Position.Y.Lower, witness.Position.Y.Upper, exact.Position.Y);
        ContainsCurvedBounds(witness.Position.Z.Lower, witness.Position.Z.Upper, exact.Position.Z);
        ContainsCurvedBounds(witness.Normal.X.Lower, witness.Normal.X.Upper, exact.Normal.X);
        ContainsCurvedBounds(witness.Normal.Y.Lower, witness.Normal.Y.Upper, exact.Normal.Y);
        ContainsCurvedBounds(witness.Normal.Z.Lower, witness.Normal.Z.Upper, exact.Normal.Z);
        ContainsCurvedBounds(maximum.Lower, maximum.Upper, exact.Position.Y);
        CheckTiltedPerPointEnclosure(true, CurvedPoint(witness.Position), CurvedPoint(witness.Normal), installed, request);
        _output.WriteLine($"maximum=[{maximum.Lower:R},{maximum.Upper:R}]");
    }

    static P CurvedPoint(GeometryVector value) => new(CurvedBounds(value.X), CurvedBounds(value.Y), CurvedBounds(value.Z));

    static B CurvedBounds(GeometryInterval value) => new(R.From(value.Lower), R.From(value.Upper));

    // An irrational coefficient enclosure, not a transform box, still needs whole-region root certificates.
    [Fact]
    public void CurvedRootRegionsCoverAnEnclosedCoefficientWithoutScalarExhaustion()
    {
        var work = NewCurvedWork();
        var arithmetic = new SupportCurvedSourceArithmetic(work);
        arithmetic.Reset(Vector3.UnitY);
        Number square = arithmetic.Divide(arithmetic.One, arithmetic.Constant(3.99999996));
        Assert.True(arithmetic.Bounds(square).Lower < arithmetic.Bounds(square).Upper);
        var regions = new SupportCurvedRootRegions(work);
        Number[] polynomial = [arithmetic.Negate(square), arithmetic.Zero, arithmetic.One];
        Assert.True(regions.Solve(arithmetic, polynomial, -1, 1));
        Assert.Equal(2, regions.Count);
        GeometryInterval negative = regions.Get(0), positive = regions.Get(1);
        // Exact containment of +-1/sqrt(c): l^2 c <= 1 <= u^2 c on each positive magnitude.
        R c = R.From(3.99999996);
        Assert.True(negative.Upper < 0 && R.From(negative.Upper).Square() * c <= R.One && R.One <= R.From(negative.Lower).Square() * c);
        Assert.True(positive.Lower > 0 && R.From(positive.Lower).Square() * c <= R.One && R.One <= R.From(positive.Upper).Square() * c);
        Assert.InRange(regions.LiveHighWater, 1, SupportCurvedRootRegions.MaximumLiveCells);
        Assert.InRange(regions.TerminalCells, 2, SupportCurvedRootRegions.MaximumTerminalCells);
        _output.WriteLine($"roots=2 live={regions.LiveHighWater} terminal={regions.TerminalCells} cells={regions.EvaluatedCells}");
        bool complete = false;
        long bytes = AllocationMeasurement.Measure(nameof(CurvedRootRegionsCoverAnEnclosedCoefficientWithoutScalarExhaustion), () =>
            complete = regions.Solve(arithmetic, polynomial, -1, 1));
        Assert.True(complete);
        Assert.Equal(0, bytes);
    }

    // A genuine double root at an irrational point has no simple-root certificate and refuses within the static bound.
    [Fact]
    public void CurvedUncertifiedDoubleRootRefusesAtStaticCellBound()
    {
        var work = NewCurvedWork();
        var arithmetic = new SupportCurvedSourceArithmetic(work);
        arithmetic.Reset(Vector3.UnitY);
        // (t - sqrt(2)/4)^2 = t^2 - (sqrt(2)/2) t + 1/8.
        Number linear = arithmetic.Multiply(arithmetic.Constant(-0.5), arithmetic.Root(arithmetic.Constant(2)));
        var regions = new SupportCurvedRootRegions(work);
        Number[] polynomial = [arithmetic.Constant(0.125), linear, arithmetic.One];
        Assert.False(regions.Solve(arithmetic, polynomial, -1, 1));
        Assert.True(regions.CapacityRefused);
        Assert.Equal(0, regions.Count);
    }
}
