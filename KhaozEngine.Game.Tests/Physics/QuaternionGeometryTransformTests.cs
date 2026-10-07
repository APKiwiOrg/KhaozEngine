using System;
using System.Numerics;
using BepuUtilities;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Pose arithmetic and input-domain checks only. Installed-shape ownership remains a backend obligation.
public class QuaternionGeometryTransformTests
{
    [Fact]
    public void IdentityPoseEnclosesItsKnownPoint()
    {
        var pose = Pose.At(new Vector3(54, 1.4825f, -97.75f));
        Vector3 local = new(0.125f, -2, 4);
        GeometryVector value = Probe(pose, local);
        Contains(value, 54.125, (double)pose.Position.Y - 2, -93.75);
        ContainsBackend(value, pose, local);
    }

    [Fact]
    public void HalfTurnPreservesItsSignedBasis()
    {
        var pose = new Pose(Vector3.Zero, new Quaternion(0, 0, 1, 0));
        Vector3 local = new(2, 3, 4);
        GeometryVector value = Probe(pose, local);
        Contains(value, -2, -3, 4);
        ContainsBackend(value, pose, local);
    }

    [Fact]
    public void ExactCyclicRotationHasIndependentClosedFormCoordinates()
    {
        var pose = new Pose(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f));
        Vector3 local = new(2, 3, 4);
        GeometryVector value = Probe(pose, local);
        Contains(value, 4, 2, 3);
        ContainsBackend(value, pose, local);
    }

    [Fact]
    public void GeneralRepresentedUnitRotationEnclosesTheBackendEvaluation()
    {
        var pose = new Pose(new Vector3(45, 1.4825f, -97.75f), Quaternion.CreateFromYawPitchRoll(0.2f, 0.3f, -0.4f));
        Vector3 local = new(-9, 0.0425f, 2);
        GeometryVector value = Probe(pose, local);
        ContainsBackend(value, pose, local);
    }

    [Fact]
    public void ZeroNonunitAndNonfiniteRotationsAreNotSilentlyNormalized()
    {
        Assert.False(Probe(new Pose(Vector3.Zero, default), Vector3.One).IsResolved);
        Assert.False(Probe(new Pose(Vector3.Zero, new Quaternion(0, 0, 0, 2)), Vector3.One).IsResolved);
        Assert.False(Probe(new Pose(Vector3.Zero, new Quaternion(float.NaN, 0, 0, 1)), Vector3.One).IsResolved);
    }

    [Fact]
    public void UnitBandUsesTheRepresentedSquaredNorm()
    {
        float inside = MathF.BitIncrement(1f);
        float outside = 1f + 4f * (inside - 1f);
        const double upper = 1d + 1d / (1 << 20);
        Assert.True((double)inside * inside < upper);
        Assert.True((double)outside * outside > upper);
        Assert.True(Probe(new Pose(Vector3.Zero, new Quaternion(0, 0, 0, inside)), Vector3.One).IsResolved);
        Assert.False(Probe(new Pose(Vector3.Zero, new Quaternion(0, 0, 0, outside)), Vector3.One).IsResolved);
    }

    [Fact]
    public void InputDomainBoundariesRefuseRatherThanClamp()
    {
        Assert.True(Probe(Pose.At(new Vector3(2048, -2048, 2048)), Vector3.Zero).IsResolved);
        Assert.False(Probe(Pose.At(new Vector3(MathF.BitIncrement(2048f), 0, 0)), Vector3.Zero).IsResolved);
        Assert.True(Probe(Pose.Identity, new Vector3(64, -64, 64)).IsResolved);
        Assert.False(Probe(Pose.Identity, new Vector3(MathF.BitIncrement(64f), 0, 0)).IsResolved);
        Assert.False(Probe(Pose.At(new Vector3(float.PositiveInfinity)), Vector3.Zero).IsResolved);
    }

    [Fact]
    public void OppositeQuaternionSignsEncloseTheSameKnownRotation()
    {
        Vector3 local = new(2, 3, 4);
        GeometryVector first = Probe(new Pose(Vector3.Zero, new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)), local);
        GeometryVector second = Probe(new Pose(Vector3.Zero, new Quaternion(-0.5f, -0.5f, -0.5f, -0.5f)), local);
        Contains(first, 4, 2, 3);
        Contains(second, 4, 2, 3);
    }

    static GeometryVector Probe(Pose pose, Vector3 local) => RepresentedGeometryTransforms.PosePoint(pose, local);

    static void ContainsBackend(GeometryVector value, Pose pose, Vector3 local)
    {
        Matrix3x3.CreateFromQuaternion(pose.Orientation, out Matrix3x3 matrix);
        Matrix3x3.Transform(local, matrix, out Vector3 rotated);
        Vector3 actual = rotated + pose.Position;
        Contains(value, actual.X, actual.Y, actual.Z);
    }

    static void Contains(GeometryVector value, double x, double y, double z)
    {
        Assert.True(value.IsResolved);
        Assert.True(value.X.Lower <= x && value.X.Upper >= x);
        Assert.True(value.Y.Lower <= y && value.Y.Upper >= y);
        Assert.True(value.Z.Lower <= z && value.Z.Upper >= z);
    }
}
