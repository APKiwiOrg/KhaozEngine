// Real feature correspondence precedes private policy lookup. Wider enclosures below remain
// truthful supersets of actual output, but must not satisfy an unproved eligibility threshold.
using System;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using static KhaozEngine.Tests.Movement.CornerFeatureOracle;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public partial class LowLipTraversalTests
{
    private delegate bool FeatureEligibility(IPhysicsCapsuleFeatures capability, IPhysicsQueryLease lease,
        in CapsuleFeatureResult result, ReadOnlySpan<CapsuleIncidentFace> faces, float cosMaxSlope);

    [Theory]
    [InlineData("open", true)]
    [InlineData("convex", true)]
    [InlineData("concave", false)]
    [InlineData("competing", false)]
    [InlineData("underside", false)]
    [InlineData("wall", false)]
    [InlineData("dome", false)]
    [InlineData("unsupported-compound", false)]
    public void FeatureEligibilityMatchesTheActualFiniteNeighborhood(string fixture, bool eligible)
    {
        using var scene = new CornerFeatureControlScene(fixture);
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        // No scripted Completed factory supplies geometry. Complete, where required, precedes helper lookup.
        FeatureEligibility policy = BindFeatureEligibility();
        Assert.Equal(eligible, policy(scene.Features, scene.Lease, result,
            faces.AsSpan(0, result.Written), MathF.Cos(Tuning.MaxSlopeRadians)));
    }
    [Theory]
    [InlineData("position")]
    [InlineData("separation")]
    [InlineData("direction")]
    [InlineData("wall")]
    public void FeatureEligibilityRefusesWidenedEvidence(string wider)
    {
        using var scene = new CornerFeatureControlScene("convex");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        FeatureEligibility policy = BindFeatureEligibility();
        float slope = MathF.Cos(Tuning.MaxSlopeRadians);
        Assert.True(policy(scene.Features, scene.Lease, result, faces.AsSpan(0, result.Written), slope));
        CapsuleIncidentFace[] copy = faces.AsSpan(0, result.Written).ToArray();
        if (wider == "wall")
            for (int i = 0; i < copy.Length; i++)
                if (copy[i].Normal.Y == 0)
                    copy[i] = new(copy[i].FaceId, copy[i].Normal, 0.000001f, copy[i].Incidence);
        CapsuleFeatureResult broadened = CapsuleFeatureResult.Completed(scene.View, scene.Lease, scene.Target,
            result.LeafId, result.FeatureId, result.Kind, result.AxisPoint, result.GeometryPoint,
            result.SeparationNormal, wider == "separation" ? -0.0002 : result.SeparationLower,
            wider == "separation" ? 0.0002 : result.SeparationUpper,
            wider == "position" ? 0.001f : result.PositionErrorMetres,
            wider == "direction" ? 1f : result.NormalError, copy);
        // Only enclosure widths changed. No invented geometry or scripted provider establishes correspondence.
        Assert.False(policy(scene.Features, scene.Lease, broadened, copy, slope));
    }

    [Fact]
    public void FeatureEligibilityDoesNotReviveAnExpiredQuery()
    {
        using var scene = new CornerFeatureControlScene("open");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        FeatureEligibility policy = BindFeatureEligibility();
        Assert.True(policy(scene.Features, scene.Lease, result, faces.AsSpan(0, result.Written),
            MathF.Cos(Tuning.MaxSlopeRadians)));
        scene.Lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => policy(scene.Features, scene.Lease, result,
            faces.AsSpan(0, result.Written), MathF.Cos(Tuning.MaxSlopeRadians)));
    }

    [Fact]
    public void FeatureEligibilityRequiresTheOriginalReceiver()
    {
        using var scene = new CornerFeatureControlScene("open");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        FeatureEligibility policy = BindFeatureEligibility();
        Assert.Throws<InvalidOperationException>(() => policy(scene.World, scene.Lease, result,
            faces.AsSpan(0, result.Written), MathF.Cos(Tuning.MaxSlopeRadians)));
    }

    [Fact]
    public void FeatureEligibilityTreatsCoplanarRawFacesAsOnePatch()
    {
        Triangle[] triangles =
        [
            CornerFeatureControlScene.Top(),
            new(new(2, 0, -2), new(2, 0, 2), new(0, 0, 2)),
        ];
        using var scene = new CornerFeatureControlScene(triangles, new CapsuleShape(0.25f, 0.5f),
            Pose.At(new System.Numerics.Vector3(1, 0.5f, 0)),
            new CornerFeatureControlScene.RayControl(new(0.5f, 0, -1), System.Numerics.Vector3.UnitY));
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult result = scene.Query(faces);
        AssertComplete(scene.View, scene.World, scene.Features, scene.Lease, scene.Target, scene.Capsule,
            result, faces, original, CapsuleFeatureKind.FaceInterior, V(4, 1, 0, 4), V(1, 0, 0),
            [V(0, 1, 0), V(0, 1, 0)]);
        FeatureEligibility policy = BindFeatureEligibility();
        Assert.True(policy(scene.Features, scene.Lease, result, faces.AsSpan(0, result.Written),
            MathF.Cos(Tuning.MaxSlopeRadians)));
    }

    [Fact]
    public void FeatureEligibilityRejectsTwoNearFlatIncidentTops()
    {
        using var scene = new CornerFeatureControlScene("ridge");
        CapsuleFeatureResult result = scene.AssertCorrespondence(out CapsuleIncidentFace[] faces);
        Assert.Equal(2, result.Written);
        Assert.True(R.From(result.SeparationNormal.Y) - R.From(result.NormalError) >= R.From(0.9f));
        for (int i = 0; i < result.Written; i++)
            Assert.True(R.From(faces[i].Normal.Y) - R.From(faces[i].NormalError) >= R.From(0.9f));
        FeatureEligibility policy = BindFeatureEligibility();
        Assert.False(policy(scene.Features, scene.Lease, result, faces.AsSpan(0, result.Written),
            MathF.Cos(Tuning.MaxSlopeRadians)));
    }

    static FeatureEligibility BindFeatureEligibility()
    {
        MethodInfo? method = typeof(CharacterMovement).GetMethod("LowPropFeatureEligible",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (FeatureEligibility)method.CreateDelegate(typeof(FeatureEligibility));
    }

}
