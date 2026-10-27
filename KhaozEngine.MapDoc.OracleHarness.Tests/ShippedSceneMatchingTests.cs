using System;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedSceneMatchingTests
{
    static readonly MapResourceStamp Stamp = new("synthetic", "synthetic", "synthetic", "synthetic", 1);
    static readonly MapRigidFrame Rigid = new(Color.White, Material.None, .5f, 0, default, false, 0, MotionKey.None);

    [Theory]
    [InlineData(0L)]
    [InlineData(36028797018963968L)]
    public void RigidMatching_EquivalentWorldPoseSurvivesNeighbouringAnchors(long origin)
    {
        var source = RigidDraw(Triangle(), new(origin + 3, 2, -4), new(0, .25f, 0), new(7, 8, 9));
        var actual = RigidDraw(Triangle(), new(origin + 2, 2, -5), new(1.0000002f, .25f, 1), new(8, 8, 10));
        Compare(source, actual);
    }

    [Theory]
    [InlineData("pose")]
    [InlineData("rotation")]
    [InlineData("mesh")]
    [InlineData("owner")]
    [InlineData("channel")]
    [InlineData("duplicate")]
    [InlineData("focus")]
    [InlineData("dissolve")]
    public void RigidMatching_RejectsRealDifferences(string mutation)
    {
        var source = RigidDraw(Triangle(), new(3, 2, -4), new(0, .25f, 0), new(7, 8, 9));
        var actual = RigidDraw(Triangle(), new(2, 2, -5), new(1.0000002f, .25f, 1), new(8, 8, 10));
        var draw = actual.Draws.Single();
        if (mutation == "pose") { var matrix = draw.LocalTransform; matrix.M41 += .01f; draw = draw with { LocalTransform = matrix }; }
        if (mutation == "rotation") draw = draw with { LocalTransform = Matrix4x4.CreateRotationY(.71f) * Matrix4x4.CreateTranslation(1, .25f, 1) };
        if (mutation == "mesh")
        {
            var resource = actual.Resources.Single(); var mesh = ((MapRigidResource)resource.Payload).Mesh;
            mesh.Vertices[0].Position.X += .01f;
            actual = new([resource with { Payload = new MapRigidResource(mesh, default) }], actual.Draws);
        }
        if (mutation == "owner") draw = draw with { Resource = draw.Resource with { Key = draw.Resource.Key with { OwnerId = "other" } } };
        if (mutation == "channel") draw = draw with { Channels = MapDrawChannels.Shadow };
        if (mutation == "focus") draw = draw with { LocalFocus = new(8.01f, 8, 10) };
        if (mutation == "dissolve") draw = draw with { Rigid = Rigid with { Dissolve = .25f } };
        Compare(source, new(actual.Resources, mutation == "duplicate" ? [draw, draw] : [draw]), rejects: true);
    }

    [Fact]
    public void LightMatching_RotatedOwnerBoxUsesIndependentCommonSpaceCorners()
    {
        Compare(LightDraw(source: true), LightDraw(source: false));
    }

    [Theory]
    [InlineData("box")]
    [InlineData("mode")]
    [InlineData("cache")]
    [InlineData("clearance")]
    [InlineData("owner")]
    [InlineData("colour")]
    [InlineData("intensity")]
    [InlineData("radius")]
    public void LightMatching_RejectsGeometryAndExactDescriptorMutations(string mutation)
    {
        var actual = LightDraw(source: false); var draw = actual.Draws.Single(); var light = draw.Light!.Value;
        light = mutation switch
        {
            "box" => light with { Shadow = light.Shadow with { ExclusionMin = light.Shadow.ExclusionMin + new Vector3(.01f, 0, 0) } },
            "mode" => light with { Shadow = light.Shadow with { Mode = LightShadowMode.Dynamic } },
            "cache" => light with { Shadow = light.Shadow with { Key = 18 } },
            "clearance" => light with { Shadow = light.Shadow.WithNearRadius(.0000001f) },
            "owner" => light with { FixtureOwnerId = "other" },
            "colour" => light with { Colour = new(.9999999f, 1, 1) },
            "intensity" => light with { Intensity = 2.0000002f },
            "radius" => light with { Radius = 3.0000002f },
            _ => throw new ArgumentException(mutation)
        };
        Compare(LightDraw(source: true), new(actual.Resources, [draw with { Light = light }]), rejects: true);
    }

    [Fact]
    public void LightMatching_DeclaredOwnerLocalBoxUsesGeometryTolerance()
    {
        Assert.True(ShippedSceneComparison.PayloadEqual(DeclaredLight(0, .125f), DeclaredLight(.0000002f, .125f)));
        Assert.False(ShippedSceneComparison.PayloadEqual(DeclaredLight(0, .125f), DeclaredLight(.01f, .125f)));
        Assert.False(ShippedSceneComparison.PayloadEqual(DeclaredLight(0, .125f), DeclaredLight(0, .12500001f)));
    }

    [Fact]
    public void ProxyMatching_VertexIndexAndTriangleOrderDoNotChangeCoverage()
    {
        Compare(Proxy(source: true), Proxy(source: false));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("hole")]
    [InlineData("overlap")]
    [InlineData("winding")]
    [InlineData("colour")]
    [InlineData("normal")]
    public void ProxyMatching_RejectsChangedCoverageAndAttributes(string mutation)
    {
        var actual = Proxy(source: false); var resource = actual.Resources.Single(); var mesh = ((MapRigidResource)resource.Payload).Mesh;
        if (mutation == "position") mesh.Vertices[3].Position.X += .01f;
        if (mutation == "colour") mesh.Vertices[3].Color = new(.5f, 1, 1, 1);
        if (mutation == "normal") mesh.Vertices[3].Normal = Vector3.UnitX;
        uint[] indices = mutation switch
        {
            "hole" => [1, 3, 0],
            "overlap" => [1, 3, 0, 1, 3, 0],
            "winding" => [1, 0, 3, 3, 2, 0],
            _ => mesh.Indices32
        };
        Compare(Proxy(source: true), new([resource with { Payload = new MapRigidResource(new(mesh.Vertices, indices), default) }], actual.Draws), rejects: true);
    }

    static GltfMesh Triangle() => new([new(new(0, 0, 0), Vector3.UnitZ, Vector4.One),
        new(new(1, 0, 0), Vector3.UnitZ, Vector4.One), new(new(0, 1, 0), Vector3.UnitZ, Vector4.One)], new uint[] { 0, 1, 2 });

    static MapDrawSet RigidDraw(GltfMesh mesh, MapSubmissionAnchor anchor, Vector3 translation, Vector3 focus) =>
        Set(new(MapRenderKind.Roof, "canopy", "part/0", 0), new MapRigidResource(mesh, default), anchor,
            Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(translation), focus, Rigid);

    static MapDrawSet Proxy(bool source)
    {
        // The source square has two oriented triangles. Actual storage duplicates the seam vertex and
        // permutes vertices, triangle order and cyclic indices, in a different anchor frame.
        Vector3[] points = source ? [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)] :
            [new(2, 1, 0), new(1, 0, 0), new(1, 1, 0), new(2, 0, 0), new(2, 1, 0)];
        var mesh = new GltfMesh(points.Select(p => new ModelVertex(p, Vector3.UnitZ, Vector4.One)).ToArray(),
            source ? new uint[] { 0, 1, 2, 1, 3, 2 } : new uint[] { 3, 4, 2, 1, 3, 2 });
        return Set(new(MapRenderKind.Roof, "cluster/caps/0/0/0", "hlod", 0), new MapRigidResource(mesh, default),
            source ? new(3, 0, -4) : new(2, 0, -4), Matrix4x4.Identity, source ? new(7, 8, 9) : new(8, 8, 9), Rigid);
    }

    static MapDrawSet LightDraw(bool source)
    {
        var declaration = new MapLightShadowExclusion(MapLightShadowExclusionKind.FixtureBox)
        { Min = new(-.25f, -.5f, -.75f), Max = new(.5f, .75f, 1), Margin = .125f };
        var anchor = source ? new MapSubmissionAnchor(2, 0, -3) : new(1, 0, -3);
        LightShadow shadow;
        if (source)
        {
            var owner = new LegacyScenePlacement(1, "column", Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(2.5f, .125f, -2.5f),
                false, [], [], new(0, 0, 0, 1, 1, 1));
            shadow = ShippedSceneLightShadows.Resolve(new(1, "bulb", Vector3.Zero, Vector3.One, 2, 3, true) { ShadowExclusion = declaration }, owner, anchor, 17);
        }
        else
        {
            // Native prefab composition is independent of the single source renderer transform above.
            var pose = MapEffectiveTransform.Compose(new MapEffectiveTransform(2.5, .125, -2.5, .35, 1), new MapEffectiveTransform(0, 0, 0, .35, 1));
            var matrix = MapRenderTransform.ToLocal(pose, anchor);
            var corners = Enumerable.Range(0, 8).Select(i =>
            {
                return Vector3.Transform(new Vector3((i & 1) == 0 ? -.25f : .5f, (i & 2) == 0 ? -.5f : .75f,
                    (i & 4) == 0 ? -.75f : 1), matrix);
            }).ToArray();
            shadow = LightShadow.Static(17).WithExclusionBox(corners.Aggregate(Vector3.Min) - new Vector3(.125f), corners.Aggregate(Vector3.Max) + new Vector3(.125f));
        }
        return Set(new(MapRenderKind.Light, "bulb", "light", 0), DeclaredLight(0, .125f), anchor,
            Matrix4x4.CreateTranslation(source ? new(.5f, .125f, .5f) : new(1.5f, .125f, .5f)), source ? new(7, 8, 9) : new(8, 8, 9),
            light: new(Color.White, 3, 2, shadow, "column"));
    }

    static MapLightResource DeclaredLight(float offset, float margin) => new(new(1, "bulb", "column", new(new(0, 1), new(0, 1), new(0, 1)),
        Vector3.One, 2, 3, true)
    {
        ShadowExclusion = new(MapLightShadowExclusionKind.FixtureBox)
        { Min = new(-.25f + offset, -.5f, -.75f), Max = new(.5f, .75f, 1), Margin = margin }
    });

    static MapDrawSet Set(MapRenderKey key, MapResourcePayload payload, MapSubmissionAnchor anchor, Matrix4x4 matrix, Vector3 focus,
        MapRigidFrame? rigid = null, MapLightFrame? light = null)
    {
        var resource = new MapPreparedResource(new(key, Stamp), new(0, 0, 0, 1, 1, 1), payload, "synthetic");
        return new([resource], [new(resource.Reference, MapDrawChannels.Colour | MapDrawChannels.Shadow, anchor, matrix, focus, rigid, light, null, null)]);
    }

    static void Compare(MapDrawSet source, MapDrawSet actual, bool rejects = false)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var expected = source.Draws.Select(d => new SceneExpectedSubmission(d, source.Resources.Single(r => r.Reference == d.Resource).Payload as MapRigidResource)).ToArray();
        void Run() => PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.CompareDrawSets(expected, actual, report));
        if (rejects) Assert.Throws<PrivateOracleFailure>(Run);
        else { Run(); Assert.Empty(fixture.FailureCategories()); }
    }
}
