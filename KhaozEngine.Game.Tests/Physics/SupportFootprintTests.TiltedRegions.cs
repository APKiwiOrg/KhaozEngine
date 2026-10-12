using System;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;
using B = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Bounds;
using P = KhaozEngine.Tests.Physics.SupportFootprintExactOracle.Point;
using R = KhaozEngine.Tests.Physics.InstalledPoseOracle.R;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.1f)]
    public void CurvedTiltedSideAcrossSlopeGateCertifiesCrestOrBoundedRefusal(float z)
    {
        double component = 0.70710677f;
        var source = new FeaturePoint(FeatureNumber.Exact(component),
            FeatureNumber.Enclosed(GeometryInterval.Enclose(component - 1e-7, component + 1e-7)), FeatureNumber.Exact(0));
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
            default, 0.25, 0.5, default, default, default, source);
        var request = Request(z: z, lower: -2, upper: 2);
        var kernel = new SupportTiltedCurved();
        GeometrySign result = kernel.Measure(new(1), leaf, SupportNeighborhoodCurved.Side, request, out var member);
        Assert.Equal(z == 0 ? GeometrySign.Positive : GeometrySign.Unresolved, result);
        foreach (double y in new[] { component - 1e-7, component + 1e-7 })
        {
            R sx = R.From(component), sy = R.From(y);
            B length = B.Root(sx.Square() + sy.Square());
            P axis = new((B)sx / length, (B)sy / length, R.Zero);
            P centre = new(R.Zero, R.Zero, R.Zero);
            var tangent = new OracleTangent(new KhaozEngine.Physics.StaticHandle(1), 0, OracleSurface.CylinderSide,
                centre.Middle, axis.Middle, 0.25, 0.5)
            { ExactAxis = axis };
            var part = new SupportFootprintCurvedProofOracle.InstalledPart(tangent, centre, axis, R.From(0.25), R.From(0.5));
            var reference = SupportFootprintOracle.Curved(tangent, request)!;
            if (z == 0) CheckTiltedMember(member, reference, part, request);
            else
            {
                var selected = reference.Witnesses.Single(w => w.Role == SupportWitnessRole.Footprint);
                var exact = SupportFootprintTiltedOracle.Enclose(part, selected, request);
                Assert.Equal(y < component, exact.Normal.Y.Lower >= R.From(request.CosMaxSlope));
            }
        }
        if (z != 0)
            Assert.True((bool)Property(ObjectField(ObjectField(kernel, "_geometry"), "_roots"), "CapacityRefused"));
    }

    [Fact]
    public void CurvedTiltedCapAcrossSlopeGateKeepsItsGeometricWitness()
    {
        double component = 0.70710677f;
        var source = new FeaturePoint(FeatureNumber.Exact(component),
            FeatureNumber.Enclosed(GeometryInterval.Enclose(component - 1e-7, component + 1e-7)), FeatureNumber.Exact(0));
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
            default, 0.25, 0.5, default, default, default, source);
        var request = Request(x: 0.35355338f, lower: -2, upper: 2);
        var kernel = new SupportTiltedCurved();
        Assert.Equal(GeometrySign.Positive, kernel.Measure(new(1), leaf, SupportNeighborhoodCurved.TopCap, request, out var member));
        foreach (double y in new[] { component - 1e-7, component + 1e-7 })
        {
            R sx = R.From(component), sy = R.From(y);
            B length = B.Root(sx.Square() + sy.Square());
            P axis = new((B)sx / length, (B)sy / length, R.Zero);
            P centre = new(R.Zero, R.Zero, R.Zero);
            var tangent = new OracleTangent(new KhaozEngine.Physics.StaticHandle(1), 1, OracleSurface.CylinderTop,
                centre.Middle, axis.Middle, 0.25, 0.5)
            { ExactAxis = axis };
            var part = new SupportFootprintCurvedProofOracle.InstalledPart(tangent, centre, axis, R.From(0.25), R.From(0.5));
            CheckTiltedMember(member, SupportFootprintOracle.Curved(tangent, request)!, part, request);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedTiltedWeakYSignKeepsCertifiedComponentEquality(bool negative)
    {
        var source = new FeaturePoint(FeatureNumber.Exact(1), FeatureNumber.Enclosed(negative ?
            GeometryInterval.Enclose(-1e-8, 0) : GeometryInterval.Enclose(0, 1e-8)), FeatureNumber.Exact(0));
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
            default, 0.25, 0.5, default, default, default, source);
        var kernel = new SupportTiltedCurvedGeometry();
        Assert.True(kernel.Begin(leaf, 0, 0, 0.2f));
        Assert.True(kernel.CapUniformlyUp(2));
        Assert.Equal(0, kernel.CapUpward(2).Lower);
        Assert.Equal(0, kernel.CapUpward(3).Upper);
    }

    [Fact]
    public void CurvedTiltedCapWhoseUpperRoleChangesAcrossSourceRequiresBoundedCoverage()
    {
        var source = new FeaturePoint(FeatureNumber.Exact(1),
            FeatureNumber.Enclosed(GeometryInterval.Enclose(-1e-8, 1e-8)), FeatureNumber.Exact(0));
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
            default, 0.25, 0.5, default, default, default, source);
        var request = Request(x: 0.6f, lower: -2, upper: 2);
        foreach (double y in new[] { -1e-8, 1e-8 })
        {
            var length = B.Root(R.One + R.From(y).Square());
            P axis = new((B)R.One / length, (B)R.From(y) / length, R.Zero);
            var part = new OracleTangent(new KhaozEngine.Physics.StaticHandle(1), 1, OracleSurface.CylinderTop,
                new(0, 0, 0), axis.Middle, 0.25, 0.5)
            { ExactAxis = axis };
            var reference = SupportFootprintOracle.Curved(part, request);
            Assert.NotNull(reference);
            Assert.True(reference.Admitted);
            Assert.Equal(y > 0, reference.HasDiscMaximum);
        }
        var kernel = new SupportTiltedCurved();
        Assert.Equal(GeometrySign.Unresolved, kernel.Measure(new(1), leaf, SupportNeighborhoodCurved.TopCap, request, out _));
        Assert.True((bool)Property(ObjectField(kernel, "_admission"), "CapacityRefused"));
    }

    [Fact]
    public void CurvedTiltedAxisCrossingFiniteEndRequiresBoundedCoverage()
    {
        var source = new FeaturePoint(FeatureNumber.Exact(1),
            FeatureNumber.Enclosed(GeometryInterval.Enclose(-1e-8, 1e-8)), FeatureNumber.Exact(0));
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder,
            FeaturePoint.Exact(new Vector3(-0.5f, -0.25f, 0)).Bounds, default, 0.25, 0.5,
            default, default, default, source);
        var kernel = new SupportTiltedCurvedGeometry();
        Assert.True(kernel.Begin(leaf, 0, 0, 0.2f));
        Assert.Equal(GeometrySign.Unresolved, kernel.Axis(false, 0, out _));
        Assert.Equal(SupportCurvedRootRegions.MaximumLiveCells, kernel.RootLiveHighWater);
        Assert.True(kernel.RootCellCount > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedTiltedCapAcrossHorizontalBoundaryRetainsRawNormal(bool signed)
    {
        FeatureNumber y = signed ? FeatureNumber.Enclosed(GeometryInterval.Enclose(-1e-8, 1e-8)) : FeatureNumber.Exact(0.5);
        var source = new FeaturePoint(FeatureNumber.Exact(1), y, FeatureNumber.Exact(0));
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(Vector3.Zero).Bounds,
            default, 0.25, 0.5, default, default, default, source);
        double x = signed ? -0.5 : -0.5 / Math.Sqrt(1.25);
        var request = Request(x: (float)x, z: 0.1f, lower: -2, upper: 2);
        var kernel = new SupportTiltedCurvedGeometry();
        Assert.True(kernel.Begin(leaf, x, 0.1, 0.2));
        Assert.Equal(GeometrySign.Positive, kernel.Cap(SupportNeighborhoodCurved.BottomCap, false, out var witness, out var maximum));
        foreach (double sy in signed ? new[] { -1e-8, 0, 1e-8 } : new[] { 0.5 })
        {
            B length = B.Root(R.One + R.From(sy).Square());
            P axis = new((B)R.One / length, (B)R.From(sy) / length, R.Zero);
            P centre = new(R.Zero, R.Zero, R.Zero);
            var tangent = new OracleTangent(new KhaozEngine.Physics.StaticHandle(1), 1, OracleSurface.CylinderBottom,
                centre.Middle, axis.Middle, 0.25, 0.5)
            { ExactAxis = axis };
            var part = new SupportFootprintCurvedProofOracle.InstalledPart(tangent, centre, axis, R.From(0.25), R.From(0.5));
            B u = (B)R.One / length, k = axis.Y;
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

    [Theory]
    [InlineData(0f)]
    [InlineData(0.1f)]
    public void CurvedTiltedSignedSourceFamilyEnclosesBothOrientations(float z)
    {
        var source = new FeaturePoint(FeatureNumber.Exact(1),
            FeatureNumber.Enclosed(GeometryInterval.Enclose(-1e-8, 1e-8)), FeatureNumber.Exact(0));
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder,
            FeaturePoint.Exact(new Vector3(0, -0.25f, 0)).Bounds, default, 0.25, 0.5, default, default, default, source);
        var kernel = new SupportTiltedCurvedGeometry();
        Assert.True(kernel.Begin(leaf, 0, z, 0.2f));
        Assert.Equal(GeometrySign.Positive, kernel.Side(0, out var witness, out var maximum));
        var request = Request(z: z, lower: -2, upper: 2);
        foreach (double y in new[] { -1e-8, 0, 1e-8 })
        {
            R sy = R.From(y);
            B length = B.Root(R.One + sy.Square());
            P axis = new((B)R.One / length, (B)sy / length, R.Zero);
            P centre = new(R.Zero, R.From(-0.25), R.Zero);
            var tangent = new OracleTangent(new KhaozEngine.Physics.StaticHandle(1), 0, OracleSurface.CylinderSide,
                centre.Middle, axis.Middle, 0.25, 0.5)
            { ExactAxis = axis };
            var part = new SupportFootprintCurvedProofOracle.InstalledPart(tangent, centre, axis, R.From(0.25), R.From(0.5));
            var locator = SupportFootprintOracle.Curved(tangent, request)!.GeometricMaximum!.Value;
            var exact = SupportFootprintTiltedOracle.Enclose(part, locator, request, restricted: false);
            ContainsCurvedBounds(witness.Position.X.Lower, witness.Position.X.Upper, exact.Position.X);
            ContainsCurvedBounds(witness.Position.Y.Lower, witness.Position.Y.Upper, exact.Position.Y);
            ContainsCurvedBounds(witness.Position.Z.Lower, witness.Position.Z.Upper, exact.Position.Z);
            ContainsCurvedBounds(witness.Normal.X.Lower, witness.Normal.X.Upper, exact.Normal.X);
            ContainsCurvedBounds(witness.Normal.Y.Lower, witness.Normal.Y.Upper, exact.Normal.Y);
            ContainsCurvedBounds(witness.Normal.Z.Lower, witness.Normal.Z.Upper, exact.Normal.Z);
            ContainsCurvedBounds(maximum.Lower, maximum.Upper, exact.Position.Y);
            CheckTiltedPerPointEnclosure(true, CurvedPoint(witness.Position), CurvedPoint(witness.Normal), part, request);
        }
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
        Assert.True(InstalledPoseOperator.TryCreate(pose, out var map));
        var leaf = new SupportCurvedLeaf(0, SupportCurvedShape.Cylinder, FeaturePoint.Exact(pose.Position).Bounds,
            GeometryVectorOperations.Normalize(map.Y.Bounds), 0.3f, 1, default, default, default, map.Y);
        var kernel = new SupportTiltedCurvedGeometry();
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

    [Fact]
    public void CurvedRootRegionsCoverEverySourceWithoutScalarExhaustion()
    {
        var arithmetic = new SupportCurvedSourceArithmetic();
        arithmetic.Reset(new(FeatureNumber.Enclosed(GeometryInterval.Enclose(0.25, 0.25000001)),
            FeatureNumber.Exact(1), FeatureNumber.Exact(0)));
        var regions = new SupportCurvedRootRegions();
        Number[] polynomial = [arithmetic.Negate(arithmetic.X), arithmetic.Zero, arithmetic.One];
        Assert.True(regions.Solve(arithmetic, polynomial, -1, 1));
        Assert.Equal(2, regions.Count);
        GeometryInterval negative = regions.Get(0), positive = regions.Get(1);
        Assert.True(negative.Lower <= -Math.Sqrt(0.25000001) && negative.Upper >= -0.5);
        Assert.True(positive.Lower <= 0.5 && positive.Upper >= Math.Sqrt(0.25000001));
        Assert.InRange(regions.LiveHighWater, 1, SupportCurvedRootRegions.MaximumLiveCells);
        Assert.InRange(regions.TerminalCells, 2, SupportCurvedRootRegions.MaximumTerminalCells);
        _output.WriteLine($"roots=2 live={regions.LiveHighWater} terminal={regions.TerminalCells} cells={regions.EvaluatedCells}");
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool complete = regions.Solve(arithmetic, polynomial, -1, 1);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(complete);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void CurvedUncertifiedRootFamilyRefusesAtStaticCellBound()
    {
        var arithmetic = new SupportCurvedSourceArithmetic();
        arithmetic.Reset(new(FeatureNumber.Enclosed(GeometryInterval.Enclose(-0.0001, 0.0001)),
            FeatureNumber.Exact(1), FeatureNumber.Exact(0)));
        var regions = new SupportCurvedRootRegions();
        Number[] polynomial = [arithmetic.Negate(arithmetic.X), arithmetic.Zero, arithmetic.One];
        Assert.False(regions.Solve(arithmetic, polynomial, -1, 1));
        Assert.True(regions.CapacityRefused);
        Assert.Equal(0, regions.Count);
    }
}
