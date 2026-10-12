using System;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Number = KhaozEngine.Physics.Bepu.SupportCurvedSourceArithmetic.Number;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
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
        Assert.True(witness.Position.X.Lower <= point.Position.X && witness.Position.X.Upper >= point.Position.X);
        Assert.True(witness.Position.Y.Lower <= point.Position.Y && witness.Position.Y.Upper >= point.Position.Y);
        Assert.True(witness.Position.Z.Lower <= point.Position.Z && witness.Position.Z.Upper >= point.Position.Z);
        Assert.True(maximum.Lower <= reference.Maximum && maximum.Upper >= reference.Maximum);
        _output.WriteLine($"maximum=[{maximum.Lower:R},{maximum.Upper:R}]");
    }

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
