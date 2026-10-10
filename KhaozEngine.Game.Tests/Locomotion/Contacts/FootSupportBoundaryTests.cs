// The edges of foot support and its phase 1 limits. Expectations come from the installed geometry, never from
// a stepper run. A plane-derived height is evaluated through the installed float vertices in double.
using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class FootSupportBoundaryTests
{
    static readonly float CosMaxSlope = MathF.Cos(MathF.PI / 4f);

    static FootSupportQuery Query(float x, float z = 0, float feetY = 0) =>
        new(new Vector2(x, z), feetY, 0.2f, 0.4f, 0.4f, CosMaxSlope);

    static SupportSample Find(FootSupportScene scene, FootSupportQuery query) =>
        FootSupport.Find(null, null, scene.World, scene.Lease, query);

    static void AssertStatus(SupportStatus expected, SupportSample sample) =>
        Assert.True(sample.Status == expected, $"Expected {expected}, got {sample}");

    static void AssertWalkable(double expected, StaticHandle? support, SupportSample sample)
    {
        AssertStatus(SupportStatus.Walkable, sample);
        Assert.True(sample.HeightError <= 0.0005f, $"HeightError {sample.HeightError} in {sample}");
        Assert.True(Math.Abs(sample.Height - expected) <= sample.HeightError,
            $"Expected {expected:R}, got {sample.Height:R} +/- {sample.HeightError:R} in {sample}");
        Assert.Equal(support, sample.Static);
    }

    static FootSupportScene Floor(SceneVariant variant, float topY) =>
        new FootSupportScene(variant).Flat("floor", -4, 6, -5, 5, topY);

    // A 10 degree V of one mesh, its valley line along x 0 at height 0, each side 2 m along its slope. The
    // recorded top is the low X side.
    static FootSupportScene Valley()
    {
        float angle = Radians(10);
        float eaveX = 2 * MathF.Cos(angle), eaveY = 2 * MathF.Sin(angle);
        return new FootSupportScene(SceneVariant.Mesh)
            .Mesh("valley", [
                .. FootSupportScene.Quad(new(-eaveX, eaveY, -2), new(0, 0, -2), new(-eaveX, eaveY, 2), new(0, 0, 2)),
                .. FootSupportScene.Quad(new(0, 0, -2), new(eaveX, eaveY, -2), new(0, 0, 2), new(eaveX, eaveY, 2)),
            ])
            .Top("valley", new(0, 0, -2), new(0, 0, 2), new(-eaveX, eaveY, 2));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void CrackNarrowerThanTheDiscIsBridged(SceneVariant variant)
    {
        using FootSupportScene scene = new FootSupportScene(variant)
            .Flat("west", -2, -0.1f, -2, 2, 0).Flat("east", 0.1f, 2, -2, 2, 0);
        SupportSample sample = Find(scene, Query(0));
        StaticHandle? edge = sample.Static == scene["east"] ? scene["east"] : scene["west"];
        AssertWalkable(0, edge, sample);
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void CrackWiderThanTheDiscFindsNothingInReach(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, -1)
            .Flat("west", -2, -0.4f, -2, 2, 0).Flat("east", 0.4f, 2, -2, 2, 0);
        AssertStatus(SupportStatus.None, Find(scene, Query(0)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void RailBetweenTheAxisAndTheRimIsCaught(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, 0).Flat("rail", 0.14f, 0.16f, -2, 2, 0.1f);
        AssertWalkable(0.1f, scene["rail"], Find(scene, Query(0)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void OverhangAboveTheBandIsIgnored(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, 0)
            .Slab("overhang", new Vector3(0, 1.2f, 0), 0, 1, 1);
        AssertWalkable(0, scene["floor"], Find(scene, Query(0)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void FloorBelowTheBandIsNone(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, -0.5f);
        AssertStatus(SupportStatus.None, Find(scene, Query(0)));
    }

    // The band is inclusive. The floor at Y 0 is exactly ReachDown below the feet.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SupportAtTheBandBottomIsIncluded(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, 0);
        AssertWalkable(0, scene["floor"], Find(scene, Query(0, 0, 0.4f)));
    }

    // The crate top is exactly ReachUp above the feet, and its edge at x 0 is inside the disc.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void SupportAtTheBandTopIsIncluded(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, 0).Flat("crate", 0, 1, -1, 1, 0.4f);
        AssertWalkable(0.4f, scene["crate"], Find(scene, Query(-0.1f)));
    }

    // The crate top is 0.0005 above the band top. It hides the floor at the feet, so nothing may be trusted.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void StepJustAboveTheBandRefuses(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, 0).Flat("crate", 0, 1, -1, 1, 0.4005f);
        AssertStatus(SupportStatus.Refused, Find(scene, Query(-0.1f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void GeometryInsideTheLegStartRefuses(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, 0).Flat("crate", 0, 1, -1, 1, 0.5f);
        AssertStatus(SupportStatus.Refused, Find(scene, Query(-0.1f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void CoplanarTilesNeverRefuse(SceneVariant variant)
    {
        static FootSupportScene Tiles(SceneVariant variant) => new FootSupportScene(variant)
            .Flat("west", -2, 0, -2, 2, 0).Flat("east", 0, 2, -2, 2, 0);
        using FootSupportScene scene = Tiles(variant);
        SupportSample first = Find(scene, Query(0)), second = Find(scene, Query(0));
        // One sweep returns one hit. On an exact seam tie the backend's first hit owns the support.
        StaticHandle? owner = first.Static == scene["east"] ? scene["east"] : scene["west"];
        AssertWalkable(0, owner, first);
        Assert.Equal(first, second);
        using FootSupportScene rebuilt = Tiles(variant);
        Assert.Equal(first, Find(rebuilt, Query(0)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void ExcludedStaticNeverContributes(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, 0).Flat("box", -1, 1, -1, 1, 0.1f);
        using IPhysicsWorldQueryView view = scene.World.CreateQueryViewExcludingStatics([scene["box"]]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        AssertWalkable(0, scene["floor"], FootSupport.Find(null, null, view, lease, Query(0)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void DynamicBodyNeverContributes(SceneVariant variant)
    {
        using FootSupportScene scene = Floor(variant, 0);
        scene.World.AddDynamic(new BoxShape(new Vector3(1, 0.05f, 1)), Pose.At(new Vector3(0, 0.05f, 0)),
            DynamicBodyDescription.WithMass(1));
        AssertWalkable(0, scene["floor"], Find(scene, Query(0)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void OriginFrameIsLocal(SceneVariant variant)
    {
        using FootSupportScene zero = Lip(variant);
        using FootSupportScene shifted = Lip(variant, new Vector3(256, 0, -256));
        Assert.Equal(new Vector3(256, 0, -256), shifted.World.Origin);
        SupportSample expected = Find(zero, Query(-0.1f));
        AssertStatus(SupportStatus.Walkable, expected);
        Assert.Equal(expected, Find(shifted, Query(-0.1f)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void BackFaceOfOneSidedMeshRefuses(SceneVariant variant)
    {
        // Reversed from the up-facing quad winding, so the triangle faces down. The probe meets its back face
        // from above, the feature query reports NoFeature, and nothing below that face is probed.
        using FootSupportScene scene = Floor(variant, 0)
            .Mesh("ceiling", [new(-2, 0.1f, -2), new(-2, 0.1f, 4), new(4, 0.1f, -2)]);
        AssertStatus(SupportStatus.Refused, Find(scene, Query(0)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void BoxTopCornerVertexRefuses(SceneVariant variant)
    {
        // The disc meets the top corner 0.1414 from the axis with its lowest point near 0.041, above the floor.
        using FootSupportScene scene = Floor(variant, 0).Flat("box", 0, 1, 0, 1, 0.1f);
        AssertStatus(SupportStatus.Refused, Find(scene, Query(-0.1f, -0.1f)));
    }

    [Fact]
    public void ValleyOffTheLineSupportsTheAxisSide()
    {
        using FootSupportScene scene = Valley();
        double expected = scene.TopHeightAt("valley", -0.1f, 0);
        AssertWalkable(expected, scene["valley"], Find(scene, Query(-0.1f, 0, (float)expected)));
    }

    [Fact]
    public void ConcaveValleyLineRefuses()
    {
        using FootSupportScene scene = Valley();
        AssertStatus(SupportStatus.Refused, Find(scene, Query(0)));
    }

    // A sphere centred under the axis, its top 0.0005 below the band bottom at -0.4. Both probes meet the top
    // inside the sweep margin, the sphere cannot be certified, and nothing else is in reach.
    [Fact]
    public void RefusedSurfaceBelowTheBandIsIgnored()
    {
        using var scene = new FootSupportScene(SceneVariant.Box);
        scene.World.AddStatic(new SphereShape(0.25f), Pose.At(new Vector3(0, -0.4005f - 0.25f, 0)));
        AssertStatus(SupportStatus.None, Find(scene, Query(0)));
    }

    [Fact]
    public void CurvedPrimitiveRefuses()
    {
        using var scene = new FootSupportScene(SceneVariant.Box);
        scene.World.AddStatic(new SphereShape(1), Pose.At(new Vector3(0, -0.8f, 0)));
        AssertStatus(SupportStatus.Refused, Find(scene, Query(0, 0, 0.2f)));
    }
}
