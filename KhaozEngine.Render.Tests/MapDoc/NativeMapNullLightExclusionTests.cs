using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapNullLightExclusionTests
{
    [Theory]
    [InlineData("leaf")]
    [InlineData("prefab")]
    [InlineData("monolithic")]
    [InlineData("tiled")]
    public void ExplicitNull_NormalizesToOmissionThroughEveryAuthoredBoundary(string route)
    {
        var omitted = Capture(route, false);
        var explicitNull = Capture(route, true);
        Assert.NotEmpty(explicitNull.Lights);
        Assert.All(explicitNull.Lights, light => Assert.Null(light.ShadowExclusion));
        Assert.Equal(omitted.Lights, explicitNull.Lights);
        Assert.Equal(omitted.Annotations, explicitNull.Annotations);
        Assert.Equal(omitted.Lighting, explicitNull.Lighting);
        Assert.Equal(omitted.Draws.Resources.Select(r => r.PayloadDigest), explicitNull.Draws.Resources.Select(r => r.PayloadDigest));
        Assert.Equal(omitted.Draws.Draws.Select(d => d.Light), explicitNull.Draws.Draws.Select(d => d.Light));
        var shadows = explicitNull.Draws.Draws.Select(d => d.Light!.Value.Shadow).Where(s => s.Requested).ToArray();
        Assert.NotEmpty(shadows);
        Assert.All(shadows, shadow => { Assert.True(shadow.HasExclusionBox); Assert.Equal(0, shadow.NearRadius); });
    }

    static (MapEffectiveLight[] Lights, string Annotations, string Lighting, MapDrawSet Draws) Capture(string route, bool explicitNull)
    {
        bool resource = route is "leaf" or "prefab";
        var closure = NativeRenderAssetFixtures.Placements(shadowExclusionJson: resource && explicitNull ? "null" : null,
            leafLightResource: route == "leaf");
        using var fixture = NativeSceneFixtures.Placements(closure);
        fixture.Document.TileSize = 64;
        fixture.Document.Surfaces = new();
        if (route == "leaf")
        {
            fixture.Document.Placements.Add(new() { Id = "leaf-lamp", Kind = "fixture-kind", AssetId = "lamp", NumericId = 106, Y = 2 });
            fixture.Document.NumericIdHighWaterMark = 106;
        }
        fixture.Document.BuildingIntent = resource ? null : new(1, [], [],
            [new MapLocalLightDoc(1, "world-emitter", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "leaf-a"),
                Vector3.UnitY, Vector3.One, 2, 3, true)], []);
        string path = Path.Combine(Path.GetTempPath(), "khaozengine-null-light-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool tiled = route == "tiled";
            MapDocumentFile.SaveAs(fixture.Document, path, tiled ? MapDocumentForm.Tiled : MapDocumentForm.Monolithic);
            string manifest = tiled ? Path.Combine(path, "map.json") : path;
            string canonical = MapDocumentFile.SaveText(fixture.Document);
            if (!resource && explicitNull)
            {
                var json = JsonNode.Parse(File.ReadAllText(manifest))!;
                json["buildingIntent"]!["lights"]![0]!["shadowExclusion"] = null;
                File.WriteAllText(manifest, json.ToJsonString());
            }
            var document = MapDocumentFile.Load(path);
            Assert.Equal(canonical, MapDocumentFile.SaveText(document));
            var effective = MapPrefabResolver.Expand(document, closure);
            Assert.DoesNotContain("shadowExclusion", System.Text.Encoding.UTF8.GetString(MapPrefabJson.Bytes(effective.BuildingIntent)));
            using var disk = MapDocumentSource.FromDocument(document);
            var source = MapSceneSource.Capture(disk, closure).Source!;
            var scope = new MapSceneScope(new(WorldFrame.Origin, new(-16, -16), new(16, 16), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, new()), new(-16, -16, -16, 16, 16, 16), MapSceneChannels.Placements | MapSceneChannels.Annotations);
            var acquired = source.Acquire(scope);
            Assert.Equal(MapSceneStatus.CompleteForScope, acquired.Status);
            var recorder = new MapDrawRecorder();
            NativeMapLights.Record(acquired.Scene!, NativeMapPlacementTests.Frame, recorder, NativeMapPlacementTests.Stamp);
            var dependencies = MapCaptureDependencies.Compute(CaptureDependencyFixtures.Request() with { Source = source, Closure = closure, Scope = scope });
            Assert.Equal(MapSceneStatus.CompleteForScope, dependencies.Status);
            Assert.NotEmpty(dependencies.LightingDigest);
            return (MapEffectiveAnnotations.Lights(acquired.Scene!).ToArray(), effective.AnnotationDigest, dependencies.LightingDigest, recorder.Snapshot());
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
