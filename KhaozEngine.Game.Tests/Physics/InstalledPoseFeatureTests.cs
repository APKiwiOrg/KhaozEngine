using System;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Physics.InstalledPoseOracle;

namespace KhaozEngine.Tests.Physics;

public class InstalledPoseFeatureTests
{
    internal static Quaternion NearW => new(0, 0, 0, 1f + 1f / (1 << 22));
    internal static Quaternion Yaw => new(0, 0.6f, 0, 0.8f);
    internal static Quaternion Tilt => new(0, 0, 0.6f, 0.8f);
    internal static Quaternion ThreeDimensional => new(0.5f, 0.5f, 0.5f, 0.5f + 1f / (1 << 22));

    // Seven installed box rows. Complete is required only after both independent forward proofs.
    [Theory]
    [InlineData("near-w")]
    [InlineData("yaw")]
    [InlineData("tilt")]
    [InlineData("three-dimensional")]
    [InlineData("translated-yaw")]
    [InlineData("large-frame-tilt")]
    [InlineData("exact-identity-control")]
    public void InstalledBoxRobustFaceInteriorPublishesUnchangedErrorBounds(string row)
    {
        Quaternion q;
        Vector3 translation = Vector3.Zero, proposal, inward;
        switch (row)
        {
            case "near-w":
                q = NearW; proposal = new(0, 1.25f, 0); inward = -Vector3.UnitY;
                Assert.NotEqual(R.One, Norm(q));
                Assert.Equal(Represented(Quaternion.Identity), Represented(q));
                break;
            case "yaw":
                q = Yaw; proposal = new(0, 1.25f, 0); inward = -Vector3.UnitY;
                Assert.NotEqual(R.One, Norm(q));
                break;
            case "tilt":
                q = Tilt; proposal = new(-1.2f, 0.35f, 0); inward = new(0.96f, -0.28f, 0);
                break;
            case "three-dimensional":
                q = ThreeDimensional; proposal = new(0, 0, 1.25f); inward = -Vector3.UnitZ;
                break;
            case "translated-yaw":
                q = Yaw; translation = new(54, 1.5f, -98); proposal = new(54, 2.75f, -98);
                inward = -Vector3.UnitY;
                break;
            case "large-frame-tilt":
                q = Tilt; translation = new(1024, 100, -1024);
                proposal = translation + new Vector3(-1.2f, 0.35f, 0);
                inward = new(0.96f, -0.28f, 0);
                break;
            case "exact-identity-control":
                q = Quaternion.Identity; proposal = new(0, 1.25f, 0); inward = -Vector3.UnitY;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(row));
        }
        Assert.True(InBand(q));
        InstalledPoseSourceChecks.Coefficients(q);
        InstalledPoseSourceChecks.PointOperations(q, translation, new Vector3(0, 1.25f, 0));
        V t = V.From(translation);
        Face real = BoxTop(Real(q), t, proposal);
        Face represented = BoxTop(Represented(q), t, proposal);
        using var scene = new InstalledPoseScene(new BoxShape(Vector3.One), new Pose(translation, q));
        scene.CheckTargetRay(proposal, inward);
        scene.AssertComplete(proposal, real, represented);
    }

    [Fact]
    public void RecenteredInstalledHullKeepsItsRepresentedCentroidWrapperAndTilt()
    {
        Vector3[] source = HullVertices();
        Pose root = new(Vector3.Zero, Tilt);
        using var scene = new InstalledPoseScene(new ConvexHullShape((Vector3[])source.Clone()), root);
        // Setup restored every actual raw vertex through the actual centroid with exact rationals.
        // Expected geometry starts from those installed values, never a feature-query result.
        Pose local = Assert.IsType<Pose>(scene.InstalledHullLocalPose);
        Pose representedLeaf = Assert.IsType<Pose>(scene.InstalledHullPose);
        Assert.Equal(Tilt, representedLeaf.Orientation);
        Assert.True(InBand(representedLeaf.Orientation));
        R bottom = -R.From(local.Position.Y), top = new R(2, 1) - R.From(local.Position.Y);
        Vector3 proposal = new(-2.1600000858306885f, 0.6299999952316284f, 0);
        Face real = BoxTop(Real(representedLeaf.Orientation), V.From(representedLeaf.Position), proposal, bottom, top);
        Face represented = BoxTop(Represented(representedLeaf.Orientation), V.From(representedLeaf.Position), proposal, bottom, top);
        scene.CheckTargetRay(proposal, new Vector3(0.96f, -0.28f, 0));
        scene.AssertComplete(proposal, real, represented);
    }

    [Fact]
    public void InstalledCompoundUsesTheRepresentedComposedLeafPoseInsteadOfIdealMatrixComposition()
    {
        Pose root = new(new Vector3(3, 0.5f, -2), Yaw);
        Pose child = new(new Vector3(2, 0.5f, -1), Tilt);
        Assert.True(InBand(root.Orientation) && InBand(child.Orientation));
        Pose leaf = InstalledPoseSourceChecks.Composition(child, root);
        Assert.Equal(new Quaternion(0.36000001430511475f, 0.48000001907348633f,
            0.48000001907348633f, 0.64000004529953f), leaf.Orientation);
        Assert.Equal(new Vector3(2.5999999046325684f, 1, -4.199999809265137f), leaf.Position);
        Assert.True(InBand(leaf.Orientation));
        Assert.NotEqual(Real(root.Orientation).Apply(Real(child.Orientation).Apply(V.UnitY)),
            Real(leaf.Orientation).Apply(V.UnitY));
        Vector3 proposal = new(2.2639999389648438f, 1.350000023841858f, -3.047999620437622f);
        Face real = BoxTop(Real(leaf.Orientation), V.From(leaf.Position), proposal);
        Face represented = BoxTop(Represented(leaf.Orientation), V.From(leaf.Position), proposal);
        var shape = new CompoundShape([new CompoundChild(new BoxShape(Vector3.One), child)]);
        using var scene = new InstalledPoseScene(shape, root);
        scene.CheckTargetRay(proposal, new Vector3(0.2688f, -0.28f, -0.9216f));
        scene.AssertComplete(proposal, real, represented);
    }

    [Fact]
    public void InstalledCompoundRefusesAnOutOfBandComposedPoseWithoutTouchingCallerStorage()
    {
        Pose root = new(Vector3.Zero, NearW), child = new(new Vector3(2, 0, 0), NearW);
        Assert.True(InBand(root.Orientation) && InBand(child.Orientation));
        Pose leaf = InstalledPoseSourceChecks.Composition(child, root);
        Assert.Equal(new Quaternion(0, 0, 0, 1f + 1f / (1 << 21)), leaf.Orientation);
        Assert.True(Norm(leaf.Orientation) > R.One + Tau);
        var shape = new CompoundShape([new CompoundChild(new BoxShape(Vector3.One), child)]);
        using var scene = new InstalledPoseScene(shape, root);
        scene.CheckTargetRay(new Vector3(2, 1.25f, 0), -Vector3.UnitY);
        scene.AssertRefused(new Vector3(2, 1.25f, 0), CapsuleFeatureStatus.Unsupported);
    }

    // Three one-sided mesh rows. No grazing assertion is smuggled into the robust front cases.
    [Theory]
    [InlineData("tilted-front", true)]
    [InlineData("tilted-back", false)]
    [InlineData("three-dimensional-front", true)]
    public void InstalledMeshRequiresForwardAndPinnedLocalSideAgreement(string row, bool front)
    {
        Quaternion q = row == "three-dimensional-front" ? ThreeDimensional : Tilt;
        Assert.True(InBand(q));
        InstalledPoseSourceChecks.Coefficients(q);
        Vector3 proposal = row switch
        {
            "tilted-front" => new(-1.2f, 0.35f, 0),
            "tilted-back" => new(-0.72f, 0.21f, 0),
            "three-dimensional-front" => new(0, 0, 1.25f),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        Face real = Triangle(Real(q), V.Zero, proposal, front);
        Face represented = Triangle(Represented(q), V.Zero, proposal, front);
        AssertTriangleLocalSide(Represented(q), V.Zero, proposal, front);
        var mesh = new TriangleMeshShape(
            [TriangleA.AsSingle(), TriangleB.AsSingle(), TriangleC.AsSingle()], [0, 1, 2]);
        using var scene = new InstalledPoseScene(mesh, new Pose(Vector3.Zero, q));
        Vector3 frontControl = row == "three-dimensional-front" ? new(0, 0, 1.25f) : new(-1.2f, 0.35f, 0);
        Vector3 inward = row == "three-dimensional-front" ? -Vector3.UnitZ : new(0.96f, -0.28f, 0);
        scene.CheckTargetRay(frontControl, inward);
        if (front) scene.AssertComplete(proposal, real, represented);
        else scene.AssertRefused(proposal, CapsuleFeatureStatus.NoFeature);
    }

    static Vector3[] HullVertices() =>
    [
        new(-1, 0, -1), new(1, 0, -1), new(-1, 2, -1), new(1, 2, -1),
        new(-1, 0, 1), new(1, 0, 1), new(-1, 2, 1), new(1, 2, 1),
    ];
}
