using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;

namespace KhaozEngine.Tests.Physics;

public class InstalledPoseDomainTests
{
    // Endpoint, adjacent inward, adjacent outward for each closed norm endpoint. Six cases.
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, -1, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, -1, false)]
    public void ClosedNormBandUsesExactRepresentedSumAndAdjacentValues(int endpoint, int step, bool admitted)
    {
        Quaternion boundary = endpoint == 0
            ? new Quaternion(0, 0, 1, 1f / 1024)
            : new Quaternion(1023f / 1024, 43f / 1024, 14f / 1024, 1f / 1024);
        R expected = endpoint == 0 ? R.One + Tau : R.One - Tau;
        // Lower numerator: 1023^2+43^2+14^2+1^2 = 1048575. Upper: 1048576+1.
        Assert.Equal(expected, Norm(boundary));
        Quaternion q = boundary;
        if (step != 0) q.W = step < 0 ? MathF.BitDecrement(q.W) : MathF.BitIncrement(q.W);
        R exact = Norm(q);
        if (step < 0) Assert.True(exact < expected);
        if (step > 0) Assert.True(exact > expected);
        Assert.Equal(admitted, InBand(q));
        InstalledPoseSourceChecks.Coefficients(q);

        // Existing owner only, at zero local input. No proposed private production seam is needed.
        GeometryVector value = RepresentedGeometryTransforms.PosePoint(new Pose(Vector3.Zero, q), Vector3.Zero);
        Assert.Equal(admitted, value.IsResolved);
        if (admitted)
        {
            Assert.True(value.X.Lower <= 0 && value.X.Upper >= 0);
            Assert.True(value.Y.Lower <= 0 && value.Y.Upper >= 0);
            Assert.True(value.Z.Lower <= 0 && value.Z.Upper >= 0);
        }
        else
        {
            using var scene = new InstalledPoseScene(new BoxShape(Vector3.One), new Pose(Vector3.Zero, q));
            scene.AssertRefused(new Vector3(0, 1.25f, 0), CapsuleFeatureStatus.Unsupported);
        }
    }

    [Fact]
    public void SixtyFourMetreControlSeparatesForwardAndTransposeInverseGeometry()
    {
        Quaternion q = new(0, 0, 1, 1f / 1024);
        Assert.Equal(R.One + Tau, Norm(q));
        InstalledPoseSourceChecks.Coefficients(q);
        M real = Real(q), represented = Represented(q), ideal = NormalizedIdeal(q);
        Assert.Equal(real, represented); // Coefficient conversion is exact for this control.
        InstalledPoseSourceChecks.PointOperations(q, Vector3.Zero, new Vector3(64, 64, 0));
        V local = V.From(new Vector3(64, 64, 0));
        V forward = represented.Apply(local);
        V roundedOutput = RoundedPoint(represented, V.Zero, local);
        V transposeInverse = represented.TransposeInverseImage(local);
        Assert.Equal(V.From(new Vector3(-64.125f, -63.875f, 0)), forward);
        Assert.Equal(forward, roundedOutput); // Point rounding happens to be exact here too.
        Assert.NotEqual(forward, ideal.Apply(local));
        Assert.NotEqual(forward, transposeInverse);
        R delta = new(1, 1 << 18), determinant = R.One + delta;
        Assert.Equal(determinant, represented.Determinant);
        Assert.Equal(forward / determinant, transposeInverse);
        R expectedSquared = new R(8192, 1) * delta.Square() / determinant;
        Assert.Equal(expectedSquared, (forward - transposeInverse).LengthSquared);
        I distance = Root(expectedSquared);
        Assert.True(distance.Lower > PositionCeiling);
        Assert.True(distance.Lower > new R(345, 1000000) && distance.Upper < new R(346, 1000000));
        // No Complete assertion: this finite systematic discrepancy exceeds the unchanged ceiling.
    }
}
