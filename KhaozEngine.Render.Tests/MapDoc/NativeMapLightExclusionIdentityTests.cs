using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapLightExclusionIdentityTests
{
    [Theory]
    [InlineData("None", "NearSphere")]
    [InlineData("None", "FixtureBox")]
    [InlineData("NearSphere", "radius")]
    [InlineData("FixtureBox", "margin")]
    [InlineData("FixtureBox", "min")]
    [InlineData("FixtureBox", "max")]
    public void ChangingExclusion_InvalidatesResourceAndCaptureIdentity(string original, string changed)
    {
        string first = NativeMapLightExclusionTests.Declaration(original)!;
        var json = JsonNode.Parse(NativeMapLightExclusionTests.Declaration(changed) ?? first)!;
        if (changed == "radius") json["radius"] = .75f;
        if (changed == "margin") json["margin"] = .25f;
        if (changed == "min") json["min"]!["x"] = -2;
        if (changed == "max") json["max"]!["z"] = 2;
        string second = json.ToJsonString();
        string ResourceDigest(string declaration)
        {
            var scene = NativeMapPlacementTests.Scene(NativeRenderAssetFixtures.Placements(shadowExclusionJson: declaration));
            var recorder = new MapDrawRecorder(); NativeMapLights.Record(scene, NativeMapPlacementTests.Frame, recorder, NativeMapPlacementTests.Stamp);
            return recorder.Snapshot().Resources.Single(r => r.Reference.Key.OwnerId == "emitter-00").PayloadDigest;
        }
        Assert.NotEqual(ResourceDigest(first), ResourceDigest(second));
        string CaptureDigest(string declaration)
        {
            var (document, closure) = CaptureDependencyFixtures.Create();
            var intent = document.BuildingIntent!;
            document.BuildingIntent = new(1, [], [], [intent.Lights[0] with
            { Owner = new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "sun-caster") }], []);
            var root = JsonNode.Parse(MapDocumentFile.SaveText(document))!;
            root["buildingIntent"]!["lights"]![0]!["shadowExclusion"] = JsonNode.Parse(declaration);
            using var disk = MapDocumentSource.FromDocument(MapDocumentFile.LoadText(root.ToJsonString()));
            var source = MapSceneSource.Capture(disk, closure).Source!;
            var request = CaptureDependencyFixtures.Request() with { Source = source, Closure = closure };
            var result = MapCaptureDependencies.Compute(request);
            Assert.Equal(MapSceneStatus.CompleteForScope, result.Status);
            Assert.NotEmpty(result.LightingDigest);
            return result.LightingDigest;
        }
        Assert.NotEqual(CaptureDigest(first), CaptureDigest(second));
    }
}
