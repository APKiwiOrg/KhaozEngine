// The edges of foot support, and the phase 1 limits the support neighborhood now certifies. Expectations come
// from the installed geometry, never from a stepper run. A plane-derived height is evaluated through the installed
// float vertices in double.
using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
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

    // A 10 degree V, its valley line along x 0 at height 0, each side 2 m along its slope. The mesh variant is one
    // mesh "valley" whose recorded top is the low X side. The box variant is two slabs, "west" falling to the line
    // and "east" rising from it.
    static FootSupportScene Valley(SceneVariant variant = SceneVariant.Mesh)
    {
        float angle = Radians(10);
        float eaveX = 2 * MathF.Cos(angle), eaveY = 2 * MathF.Sin(angle);
        if (variant == SceneVariant.Box)
            return new FootSupportScene(variant)
                .Slab("west", new Vector3(-eaveX / 2, eaveY / 2, 0), -angle, 1, 2)
                .Slab("east", new Vector3(eaveX / 2, eaveY / 2, 0), angle, 1, 2);
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
        // Both tiles are in the neighborhood at the seam. The selection's tie order picks the owner.
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
    public void BackFaceOfOneSidedMeshSeesTheFloor(SceneVariant variant)
    {
        // Reversed from the up-facing quad winding, so the triangle faces down. The probes pass its back face, as
        // the simulation's one-sided contacts do, and certify the floor beneath. This closes the back-face case of
        // the hidden lower surface limit only. A sloped surface that stops the leg probe can still hide a lower
        // walkable surface inside the disc (#1347).
        using FootSupportScene scene = Floor(variant, 0)
            .Mesh("ceiling", [new(-2, 0.1f, -2), new(-2, 0.1f, 4), new(4, 0.1f, -2)]);
        AssertWalkable(0, scene["floor"], Find(scene, Query(0)));
    }

    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void BoxTopCornerSupportsTheTop(SceneVariant variant)
    {
        // The disc meets the top corner 0.1414 from the axis with its lowest point near 0.041, above the floor. The
        // corner's sides are vertical and contribute nothing, so the top's plane at the axis is the support.
        using FootSupportScene scene = Floor(variant, 0).Flat("box", 0, 1, 0, 1, 0.1f);
        SupportSample sample = Find(scene, Query(-0.1f, -0.1f));
        AssertWalkable(0.1f, scene["box"], sample);
        if (variant == SceneVariant.Box) Assert.Equal(SupportNeighborhoodOracle.BoxTop, sample.FeatureId);
    }

    [Fact]
    public void ValleyOffTheLineSupportsTheAxisSide()
    {
        using FootSupportScene scene = Valley();
        double expected = scene.TopHeightAt("valley", -0.1f, 0);
        AssertWalkable(expected, scene["valley"], Find(scene, Query(-0.1f, 0, (float)expected)));
    }

    // Neither face of the concave crease caps the other, so each contributes its own plane at the axis and the
    // higher one is the crease height through the installed vertices.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void ConcaveValleyLineSupportsTheCrease(SceneVariant variant)
    {
        using FootSupportScene scene = Valley(variant);
        SupportSample sample = Find(scene, Query(0));
        if (variant == SceneVariant.Mesh)
        {
            AssertWalkable(0, scene["valley"], sample);
            return;
        }
        double west = scene.TopHeightAt("west", 0, 0), east = scene.TopHeightAt("east", 0, 0);
        AssertWalkable(Math.Max(west, east), sample.Static, sample);
        Assert.Contains(sample.Static, new StaticHandle?[] { scene["west"], scene["east"] });
    }

    // An over-capacity fan whose apex is 0.0005 below the band bottom at -0.4. Both probes meet the apex inside the
    // sweep margin and its neighborhood refuses, but a refusal below the band cannot hide support in it.
    [Fact]
    public void RefusedSurfaceBelowTheBandIsIgnored()
    {
        using FootSupportScene scene = OverCapacityFan(new Vector3(0, -0.4005f, 0));
        AssertStatus(SupportStatus.None, Find(scene, Query(0)));
    }

    // A sphere of radius 0.25 centred under the axis. Both probes meet its top, a tangent element with an upward
    // normal, so the support is the top at centre.Y + 0.25. The mesh variant's pole vertex is that same point.
    [Theory]
    [InlineData(SceneVariant.Box)]
    [InlineData(SceneVariant.Mesh)]
    public void CurvedPrimitiveSupportsItsTop(SceneVariant variant)
    {
        var centre = new Vector3(0, -0.25f, 0);
        using FootSupportScene scene = Sphere(variant, centre, 0.25f);
        AssertWalkable((double)centre.Y + 0.25f, scene["sphere"], Find(scene, Query(0)));
    }

    // An upright capsule prop of radius 0.15 and length 0.3 centred at Y -0.3. Both probes meet its top, a tangent
    // element of the upper cap with an upward normal, at centre.Y + length / 2 + radius.
    [Fact]
    public void CapsulePrimitiveSupportsItsTop()
    {
        const float radius = 0.15f, length = 0.3f;
        var centre = new Vector3(0, -0.3f, 0);
        using FootSupportScene scene = new FootSupportScene(SceneVariant.Box)
            .Add("capsule", new CapsuleShape(radius, length), Pose.At(centre));
        AssertWalkable((double)centre.Y + length / 2.0 + radius, scene["capsule"], Find(scene, Query(0)));
    }
}
