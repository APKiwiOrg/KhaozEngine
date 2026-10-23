using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeRoofHlodGpuTests(NativeMapGoldenScene scene) : IClassFixture<NativeMapGoldenScene>
{
    [Fact]
    public void GoldenFixture_DeclaresTwoRoofsInOneAcceptedCohort()
    {
        using var setup = NativeMapGoldenSetup.Create("roof-hlod"); var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(setup.State, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var roofs = residency.AcceptedPlans.SelectMany(p => p.Resources).Where(r => r.Reference.Key.Kind == MapRenderKind.Roof).ToArray();
        Assert.Single(roofs, r => r.Reference.Key.PartId == "hlod");
        Assert.Equal(new[] { "roof-a", "roof-b" }, roofs.Where(r => r.Reference.Key.PartId != "hlod").Select(r => r.Reference.Key.OwnerId).Distinct().Order());
    }

    [GpuTheory]
    [InlineData("visible", MapRoofMode.AlwaysVisible, 40)]
    [InlineData("hidden", MapRoofMode.AlwaysHidden, 40)]
    [InlineData("mixed", MapRoofMode.Auto, 40)]
    [InlineData("midpoint", MapRoofMode.AlwaysVisible, 30)]
    public void RoofHlod_VisibilityAndComplementGolden(string state, MapRoofMode mode, float distance)
    {
        var image = scene.Capture("roof-hlod", false, false, mode, new(32 + distance, 0, 32));
        var roofs = scene.LastEvidence.Submitted.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray();
        if (state == "mixed")
        {
            Assert.Equal(4, roofs.Length); Assert.DoesNotContain(roofs, r => r.Resource.Key.PartId == "hlod");
            Assert.Equal(2, roofs.Count(r => r.Channels.HasFlag(MapDrawChannels.Colour)));
            Assert.All(roofs, r => Assert.Equal(0, r.Rigid!.Value.Dissolve));
        }
        else
        {
            var proxy = Assert.Single(roofs, r => r.Resource.Key.PartId == "hlod");
            Assert.Equal(state != "hidden", proxy.Channels.HasFlag(MapDrawChannels.Colour));
            Assert.True(proxy.Channels.HasFlag(MapDrawChannels.Shadow));
            if (state == "midpoint")
            {
                Assert.Equal(.5f, proxy.Rigid!.Value.Dissolve, 4); Assert.Equal(1, proxy.Rigid.Value.DissolveComplement);
                Assert.False(proxy.Rigid.Value.InvertShadowDissolve); Assert.Equal(5, roofs.Length);
            }
        }
        GoldenCompare.AssertOrUpdate("native-map-roof-hlod-" + state, image.Rgba, 640, 480);
    }

    [GpuFact]
    public void RoofHlod_ShadowOmissionGolden()
    {
        var focus = new Vector3(72, 0, 32);
        var hidden = scene.Capture("roof-hlod", false, false, MapRoofMode.AlwaysHidden, focus);
        Assert.Equal(1, scene.LastEvidence.ShadowOnlyInstances);
        var omitted = scene.Capture("roof-hlod", false, false, MapRoofMode.AlwaysHidden, focus, omitRoofShadow: true);
        NativeMapGoldenAssertions.FeatureVisible(hidden, omitted);
        GoldenCompare.AssertOrUpdate("native-map-roof-hlod-shadow-omission", omitted.Rgba, 640, 480);
    }

    [GpuFact]
    public void RoofHlod_AgedSharedSceneMatchesFresh()
    {
        scene.Capture("roof-hlod", false, false, MapRoofMode.AlwaysVisible, new(62, 0, 32));
        scene.Capture("roof-hlod", false, false, MapRoofMode.Auto, new(72, 0, 32));
        var aged = scene.Capture("roof-hlod");
        var fresh = NativeMapGoldenScene.Fresh("roof-hlod");
        Assert.Equal(fresh.Rgba, aged.Rgba);
    }
}
