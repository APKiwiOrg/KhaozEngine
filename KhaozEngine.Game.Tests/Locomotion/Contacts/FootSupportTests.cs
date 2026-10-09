// Every expectation below is derived from the installed geometry, never from a stepper run. Each physics case
// runs on boxes and on the equivalent two-triangle meshes with the same expected values.
using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class FootSupportTests
{
    static readonly float CosMaxSlope = MathF.Cos(MathF.PI / 4f);

    static FootSupportQuery Query(float x, float z = 0, float feetY = 0, float footRadius = 0.2f) =>
        new(new Vector2(x, z), feetY, footRadius, 0.4f, 0.4f, CosMaxSlope);

    static SupportSample Find(FootSupportScene scene, FootSupportQuery query) =>
        FootSupport.Find(null, null, scene.World, scene.Lease, query);

    static void AssertHeight(double expected, SupportSample sample)
    {
        Assert.True(sample.HeightError <= 0.0005f, $"HeightError {sample.HeightError} in {sample}");
        Assert.True(Math.Abs(sample.Height - expected) <= sample.HeightError,
            $"Expected {expected:R}, got {sample.Height:R} +/- {sample.HeightError:R} in {sample}");
    }

    static void AssertWalkable(double expected, StaticHandle? support, SupportSample sample)
    {
        Assert.True(sample.Status == SupportStatus.Walkable, $"Expected Walkable, got {sample}");
        AssertHeight(expected, sample);
        Assert.Equal(support, sample.Static);
    }

    [Fact]
    public void TerrainAloneIsExact()
    {
        SupportSample sample = FootSupport.Find((_, _) => 0.3f, null, null, null, Query(0.7f, -1.3f));
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(0.3f, sample.Height);
        Assert.Equal(0f, sample.HeightError);
        Assert.Null(sample.Static);
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void PropFloorAboveTerrainWins(SceneVariant variant)
    {
        using FootSupportScene scene = PropFloor(variant);
        SupportSample sample = FootSupport.Find((_, _) => 0f, null, scene.World, scene.Lease, Query(0.25f, 0.25f));
        AssertWalkable(0.1f, scene["floor"], sample);
    }

    [Theory]
    [InlineData(SceneVariant.Box, 5f)]
    [InlineData(SceneVariant.Box, 20f)]
    [InlineData(SceneVariant.Box, 40f)]
    [InlineData(SceneVariant.Mesh, 5f)]
    [InlineData(SceneVariant.Mesh, 20f)]
    [InlineData(SceneVariant.Mesh, 40f)]
    public void SlopesSupportThePlaneAtTheAxis(SceneVariant variant, float degrees)
    {
        using FootSupportScene scene = Slope(variant, degrees);
        // The body stands on the slope, so its feet are at the plane under the axis, 0.5 tan(theta). The oracle
        // is that plane through the float vertices as installed.
        float feetY = (float)(0.5 * Math.Tan(degrees * Math.PI / 180));
        AssertWalkable(scene.TopHeightAt("slope", 0.5f, 0), scene["slope"], Find(scene, Query(0.5f, 0, feetY)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SlopeBeyondTheLimitIsSteep(SceneVariant variant)
    {
        using FootSupportScene scene = Slope(variant, 60f);
        SupportSample sample = Find(scene, Query(0.5f, 0, (float)(0.5 * Math.Tan(Math.PI / 3))));
        Assert.True(sample.Status == SupportStatus.Steep, $"Expected Steep, got {sample}");
    }

    // The footprint rests on the steep face 0.3 above walkable terrain, so the higher steep face is the support.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SteepFaceAboveWalkableTerrainIsSteep(SceneVariant variant)
    {
        using FootSupportScene scene = Slope(variant, 60f);
        float x = (float)(0.3 / Math.Tan(Math.PI / 3));
        double plane = scene.TopHeightAt("slope", x, 0);
        SupportSample sample = FootSupport.Find((_, _) => 0f, null, scene.World, scene.Lease,
            Query(x, 0, (float)plane));
        Assert.True(sample.Status == SupportStatus.Steep, $"Expected Steep, got {sample}");
        AssertHeight(plane, sample);
        Assert.Equal(scene["slope"], sample.Static);
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void LipInsideTheDiscLiftsToItsTop(SceneVariant variant)
    {
        using FootSupportScene scene = Lip(variant);
        AssertWalkable(LipTop, scene["lip"], Find(scene, Query(-0.1f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void LipOutsideTheDiscLeavesTheGround(SceneVariant variant)
    {
        using FootSupportScene scene = Lip(variant);
        AssertWalkable(0, scene["floor"], Find(scene, Query(-0.25f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void CrateWithinStepHeightIsStoodOn(SceneVariant variant)
    {
        using FootSupportScene scene = Crate(variant);
        AssertWalkable(CrateTop, scene["crate"], Find(scene, Query(-0.1f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box, 0.15f)]
    [InlineData(SceneVariant.Box, 0.2f)]
    [InlineData(SceneVariant.Mesh, 0.15f)]
    [InlineData(SceneVariant.Mesh, 0.2f)]
    public void ShallowTreadsChainToTheReachedTread(SceneVariant variant, float footRadius)
    {
        using FootSupportScene scene = Treads(variant);
        // Tread 2's nosing is at x 0.35. The axis stands on tread 1, 0.1 before it.
        AssertWalkable(0.5f, scene["tread2"], Find(scene, Query(0.25f, 0, 0.25f, footRadius)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void DescendingRampAheadKeepsTheFlat(SceneVariant variant)
    {
        using FootSupportScene scene = Ramp(variant, -0.5f);
        AssertWalkable(0, scene["floor"], Find(scene, Query(-0.1f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void AscendingRampAheadKeepsTheFlat(SceneVariant variant)
    {
        using FootSupportScene scene = Ramp(variant, 0.5f);
        AssertWalkable(0, scene["floor"], Find(scene, Query(-0.1f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void ConvexRidgeSupportsTheAxisSide(SceneVariant variant)
    {
        using FootSupportScene scene = Ridge(variant, 10f);
        AssertWalkable(scene.TopHeightAt("roof", -0.1f, 0), scene["roof"], Find(scene, Query(-0.1f, 0, 0.48f)));
    }

    // At 10 degrees the leg probe meets the axis-side face before the ridge edge, so separate statics agree
    // with the single-static crease.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void RidgeOfTwoSeparateStaticsSupportsTheAxisFace(SceneVariant variant)
    {
        using FootSupportScene scene = TwoStaticRidge(variant, 10f);
        AssertWalkable(scene.TopHeightAt("left", -0.1f, 0), scene["left"], Find(scene, Query(-0.1f, 0, 0.48f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SwimmingBankStepIsSupportedAtTheAxis(SceneVariant variant)
    {
        using FootSupportScene scene = Bank(variant);
        // The swimming repro's capsule radius is 0.25, so its footprint radius is 0.125.
        SupportSample sample = Find(scene, Query(0.881f, 0, 0, 0.125f));
        AssertWalkable(BankTop, scene["bank"], sample);
        Assert.True(Math.Abs(sample.Witness.X - 1) <= 0.001, $"Witness {sample.Witness}");
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void IdenticalQueriesAreBitIdentical(SceneVariant variant)
    {
        using FootSupportScene scene = Lip(variant);
        SupportSample first = Find(scene, Query(-0.1f)), second = Find(scene, Query(-0.1f));
        Assert.Equal(first, second);
        Assert.Equal(BitConverter.SingleToInt32Bits(first.Height), BitConverter.SingleToInt32Bits(second.Height));
        Assert.Equal(BitConverter.SingleToInt32Bits(first.HeightError),
            BitConverter.SingleToInt32Bits(second.HeightError));
    }

    [Fact]
    public void WorldWithoutFeatureCapabilityThrows()
    {
        using FootSupportScene scene = Lip(SceneVariant.Box);
        var featureless = new FeaturelessWorld(scene.World);
        Assert.Throws<NotSupportedException>(() =>
            FootSupport.Find(null, null, featureless, scene.Lease, Query(-0.1f)));
    }

    [Fact]
    public void ExpiredOrForeignLeaseThrows()
    {
        using FootSupportScene scene = Lip(SceneVariant.Box);
        IPhysicsQueryLease expired = scene.World.AcquireQueryReadLease();
        expired.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
            FootSupport.Find(null, null, scene.World, expired, Query(-0.1f)));

        using var other = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsQueryLease foreign = other.AcquireQueryReadLease();
        Assert.Throws<InvalidOperationException>(() =>
            FootSupport.Find(null, null, scene.World, foreign, Query(-0.1f)));
    }

    [Theory]
    [InlineData("axis")]
    [InlineData("footRadius")]
    [InlineData("reachDown")]
    public void InvalidQueryValuesThrow(string field)
    {
        FootSupportQuery query = field switch
        {
            "axis" => Query(float.NaN),
            "footRadius" => Query(0, 0, 0, 0),
            _ => Query(0) with { ReachDown = -1 },
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => FootSupport.Find((_, _) => 0f, null, null, null, query));
    }
}
