using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapGoldenGpuTests(NativeMapGoldenScene scene) : IClassFixture<NativeMapGoldenScene>
{
    [GpuTheory]
    [InlineData("paint")]
    [InlineData("props")]
    [InlineData("roofs")]
    [InlineData("water")]
    [InlineData("foliage")]
    [InlineData("origin")]
    public void NativeMapFamilies_CertifyCompleteAndDetectOmission(string variant)
    {
        var positive = scene.Capture(variant); var evidence = scene.LastEvidence;
        NativeMapGoldenAssertions.Complete(evidence);
        var omitted = scene.Capture(variant, omitFeature: true); var control = scene.LastEvidence;
        NativeMapGoldenAssertions.FeatureVisible(positive, omitted);
        Assert.ThrowsAny<XunitException>(() => NativeMapGoldenAssertions.FeatureVisible(omitted, omitted));
        if (variant == "props")
        {
            Assert.Equal(new[] { 0, 1, 2 }, evidence.Submitted.Where(d => d.Resource.Key.Kind == MapRenderKind.Placement)
                .Select(d => d.Resource.Key.Lod).Distinct().Order());
            Assert.Equal(6, evidence.ColourInstances - control.ColourInstances);
            Assert.DoesNotContain(control.Submitted, d => d.Resource.Key.Kind == MapRenderKind.Placement);
        }
        else if (variant == "roofs")
        {
            Assert.Equal(17, evidence.Lights); Assert.Equal(17, control.Lights);
            var roofs = evidence.Submitted.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray(); Assert.Equal(2, roofs.Length);
            Assert.All(roofs, r => { Assert.False((r.Channels & MapDrawChannels.Colour) != 0); Assert.True((r.Channels & MapDrawChannels.Shadow) != 0); });
            Assert.Equal(2, evidence.ShadowOnlyInstances); Assert.Equal(0, control.ShadowOnlyInstances); Assert.True(evidence.ShadowDrawCalls > 0);
            var receiver = NativeMapGoldenAssertions.GroundRoi(evidence, 4.5f, 3, 6, 4.5f);
            Assert.True(NativeMapGoldenAssertions.Darkened(positive, omitted, receiver) >= 64);
            Assert.Equal(0, NativeMapGoldenAssertions.Darkened(omitted, omitted, receiver));
            var colourRoi = NativeMapGoldenAssertions.GroundRoi(evidence, 1, 1, 2, 2);
            Assert.Equal(0, NativeMapGoldenAssertions.Changed(positive, omitted, colourRoi, 1));
            var visible = scene.Capture(variant, false, false, MapRoofMode.AlwaysVisible, null);
            NativeMapGoldenAssertions.FeatureVisible(visible, positive, colourRoi);
        }
        else if (variant == "water")
        {
            Assert.Contains(evidence.Submitted, d => d.Water.HasValue); Assert.DoesNotContain(control.Submitted, d => d.Water.HasValue);
            var dryNotch = NativeMapGoldenAssertions.GroundRoi(evidence, 3, 3, 5, 5);
            Assert.Equal(0, NativeMapGoldenAssertions.Changed(positive, omitted, dryNotch, 1));
            var wet = NativeMapGoldenAssertions.GroundRoi(evidence, .1f, 1.5f, .9f, 6.5f);
            NativeMapGoldenAssertions.FeatureVisible(positive, omitted, wet);
        }
        else if (variant == "foliage")
        {
            var bindings = evidence.Inventory.Resources.Where(r => r.Payload is MapFoliageResource)
                .SelectMany(r => ((MapFoliageResource)r.Payload).Models).OrderBy(m => m.ModelId).ToArray();
            Assert.Equal(new[] { "A", "B", "C", "D", "E" }, bindings.Select(m => m.ModelId));
            Assert.Equal(new[] { 2, 1, 2, 2, 2 }, bindings.Select(m => m.Parts.Count));
            Assert.Equal(NativeMapGoldenScene.Profiles, bindings.Select(m => m.Motion));
            Assert.Equal(new[] { true, false, true, true, true }, bindings.Select(m => m.CastShadows));
            Assert.NotEmpty(evidence.FoliageUniforms); Assert.Empty(control.FoliageUniforms);
        }
        GoldenCompare.AssertOrUpdate("native-map-" + variant, positive.Rgba, 640, 480);
    }

    [GpuTheory]
    [InlineData("paint")]
    [InlineData("props")]
    [InlineData("roofs")]
    [InlineData("water")]
    [InlineData("foliage")]
    [InlineData("origin")]
    public void NativeMapFamilies_AgedSceneMatchesFresh(string variant)
    {
        scene.Capture("props"); scene.Capture("water", omitFeature: true); scene.Capture("origin", farOrigin: true);
        var aged = scene.Capture(variant); var evidence = scene.LastEvidence;
        var fresh = NativeMapGoldenScene.Fresh(variant);
        Assert.Equal(aged.Backend, fresh.Backend); Assert.Equal(aged.Rgba, fresh.Rgba);
        NativeMapGoldenAssertions.Complete(evidence);
    }

    [GpuFact]
    public void NativeMapOrigin_SameAbsoluteRealizationAcrossRenderOrigins()
    {
        var a = scene.Capture("origin"); var ea = scene.LastEvidence;
        var b = scene.Capture("origin", farOrigin: true); var eb = scene.LastEvidence;
        NativeMapGoldenAssertions.OriginPair(a, ea, b, eb);
        var omitted = scene.Capture("origin", omitFeature: true, farOrigin: true);
        NativeMapGoldenAssertions.FeatureVisible(b, omitted);
    }

    [GpuFact]
    public void SeparateRoofs_RemainShadowOnlyAfterAcceptedNonRoofHlodAndMidpoint()
    {
        using var setup = NativeMapGoldenSetup.Create("roofs");
        var scheduler = new ResidencyScheduler();
        using var host = new NativeUploadTestScene();
        using var residency = new MapRenderResidency(setup.State, new MapScene3DSink(host.Scene), null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency); Assert.Equal(0, view.SettleForCapture(1024).Pending);
        var plan = Assert.Single(residency.AcceptedPlans); var cluster = Assert.Single(plan.Inventory.Placements.Clusters);
        Assert.Equal(new[] { "solid" }, cluster.RepresentedIds);
        Assert.DoesNotContain(cluster.RepresentedIds, id => id == "roof-a");
        var area = cluster.Request.Area;
        var focus = new Vector3((area.MinX + area.MaxX) * .5f + (float)cluster.Request.Anchor.X + 30, 0,
            (area.MinZ + area.MaxZ) * .5f + (float)cluster.Request.Anchor.Z);
        var image = scene.Capture("roofs", false, false, MapRoofMode.AlwaysHidden, focus);
        var evidence = scene.LastEvidence;
        var proxy = Assert.Single(evidence.Submitted, d => d.Resource.Key.PartId == "hlod");
        Assert.Equal(.5f, proxy.Rigid!.Value.Dissolve, 4); Assert.Equal(0, proxy.Rigid.Value.DissolveComplement); Assert.True(proxy.Rigid.Value.InvertShadowDissolve);
        Assert.All(evidence.Submitted.Where(d => d.Resource.Key.OwnerId == "solid"), d => Assert.Equal(.5f, d.Rigid!.Value.Dissolve, 4));
        Assert.Equal(2, evidence.ShadowOnlyInstances); Assert.True(evidence.ShadowDrawCalls > 0);
        var omitted = scene.Capture("roofs", true, false, MapRoofMode.AlwaysHidden, focus);
        NativeMapGoldenAssertions.FeatureVisible(image, omitted);
    }

    [Theory]
    [InlineData("paint")]
    [InlineData("props")]
    [InlineData("roofs")]
    [InlineData("water")]
    [InlineData("foliage")]
    [InlineData("origin")]
    [InlineData("water-closed")]
    public void NativeMapFamilyInputs_PrepareOnTheCpuWithoutADevice(string variant)
    {
        using var setup = NativeMapGoldenSetup.Create(variant);
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(setup.State, new ResidencyDevice(), null, scheduler);
        var acquired = setup.State.Source.Acquire(setup.State.Windows[0]);
        Assert.True(acquired.Witness.CompleteForScope, string.Join("\n", acquired.Findings));
        ResidencyFixture.Complete(residency, scheduler);
        var recorder = new MapDrawRecorder(); residency.Record(setup.Frame, recorder); var inventory = recorder.Snapshot();
        Assert.NotEmpty(inventory.Draws);
        if (variant == "water") Assert.All(residency.AcceptedPlans.SelectMany(p => p.Inventory.Water.Cells).SelectMany(c => c.Regions),
            r => Assert.DoesNotContain(r.Kind, new[] { KhaozEngine.MapDoc.Water.MapWaterRegionKind.Closed, KhaozEngine.MapDoc.Water.MapWaterRegionKind.Unresolved }));
        if (variant == "water-closed") Assert.DoesNotContain(inventory.Draws, d => d.Water.HasValue);
        if (variant == "paint") Assert.Contains(residency.AcceptedPlans.SelectMany(p => p.Inventory.Terrain.Partitions), p => p.PaintId == "partial");
    }
}
