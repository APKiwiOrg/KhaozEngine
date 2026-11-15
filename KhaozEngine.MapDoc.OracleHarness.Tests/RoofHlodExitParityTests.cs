using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class RoofHlodExitParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceExitBand_HasLiteralCoverageAndNativeDrawSetParity(bool overlap)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1542
        using var fixture = ShippedSceneGuardFixtures.Create(overlap ? "roof-hlod-exit-overlap" : "roof-hlod", comparison =>
        {
            var probes = new JsonArray();
            foreach (float distance in new[] { 90f, 95f, 99f, 100f })
                foreach (string mode in new[] { "AlwaysVisible", "AlwaysHidden" })
                    probes.Add(new JsonObject
                    {
                        ["id"] = mode + distance,
                        ["windowId"] = "A",
                        ["observerX"] = 0,
                        ["observerY"] = 2,
                        ["observerZ"] = 0,
                        ["roofMode"] = mode,
                        ["timeSeconds"] = 1,
                        ["windSeed"] = 17,
                        ["focusX"] = 32 + distance,
                        ["focusY"] = 2,
                        ["focusZ"] = -32
                    });
            comparison["policy"]!["probes"] = probes;
        });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var source = LegacySceneRecorder.Record(inputs.World, inputs.Catalogs, inputs);
        var layer = Assert.Single(inputs.SourcePolicy.Placements.Layers);
        Assert.NotEmpty(source.Frames);
        foreach (var frame in source.Frames)
        {
            var probe = inputs.Manifest.Policy.Probes.Single(p => p.Id == frame.ProbeId);
            float distance = (float)probe.FocusX!.Value - 32;
            if (distance == 100) { Assert.Empty(frame.Draws); continue; }
            float handoffStart = layer.HlodDistance - layer.HlodCrossfadeWidth * .5f;
            float t = Math.Clamp((distance - handoffStart) / layer.HlodCrossfadeWidth, 0, 1);
            if (overlap && t <= PropHlod.HlodMergedSkipAt)
            {
                Assert.DoesNotContain(frame.Draws, d => d.Key!.Value.PartId == "hlod");
                continue;
            }
            var proxy = Assert.Single(frame.Draws, d => d.Key!.Value.PartId == "hlod");
            if (overlap)
            {
                float exitStart = MathF.Max(0, layer.DrawRadius - layer.HlodCrossfadeWidth);
                float exit = Math.Clamp((distance - exitStart) / (layer.DrawRadius - exitStart), 0, 1);
                Assert.Equal(t * (1 - exit), proxy.Dissolve, 5); Assert.Equal(1, proxy.Complement);
            }
            else
            {
                Assert.Equal((distance - 90) / 10, proxy.Dissolve, 5); Assert.Equal(0, proxy.Complement);
            }
            Assert.False(proxy.InvertShadow);
        }
        PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.Compare(inputs, report));
        Assert.Empty(fixture.FailureCategories());
    }
    [Fact]
    public void IndependentIndividualBackend_ComposesPlacementExitAndLodCoverage()
    {
        var scene = new LegacyRecordingScene();
        var parts = GltfLoader.LoadPartsWithMaterials(ShippedSceneGuardFixtures.TriangleGlb(), "invented source");
        var near = scene.LoadParts(parts, 0); var far = scene.LoadParts(parts, 1);
        var prop = new PropPlacement("cap", 0, 0, 0, 1, 0, 0);
        var owner = new LegacyScenePlacement(1, "cap", Matrix4x4.Identity, true, new[] { parts, parts }, [], new(0, 0, 0, 1, 1, 1));
        scene.BeginFrame([owner], new Dictionary<long, ScenePlacement> { [1] = new(1, "cap", []) });
        var layer = PropLayer.PlacementLayer(new[] { prop }, new Dictionary<string, IReadOnlyList<MeshHandle>> { ["cap"] = near }, 100, 10,
            new Dictionary<string, IReadOnlyList<MeshHandle>> { ["cap"] = far }, 95).WithLodCrossfade(10);
        scene.DrawLayer([prop], layer, new(95, 0, 0), 0, true);
        Assert.Equal(new[] { .75f, .25f }, scene.Draws.Select(d => d.Dissolve));
        Assert.Equal(new[] { 0f, 1f }, scene.Draws.Select(d => d.Complement));
        Assert.All(scene.Draws, d => Assert.True(d.ShadowOnly));
    }

}
