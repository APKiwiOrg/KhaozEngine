using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapLightExclusionTests
{
    [Theory]
    [InlineData("None", false)]
    [InlineData("NearSphere", false)]
    [InlineData("FixtureBox", false)]
    [InlineData("Absent", false)]
    [InlineData("None", true)]
    [InlineData("NearSphere", true)]
    [InlineData("FixtureBox", true)]
    [InlineData("Absent", true)]
    public void PrefabEmitters_RoundTripAndSubmitDeclaredExclusion(string mode, bool tiled)
    {
        string? declaration = Declaration(mode);
        var closure = NativeRenderAssetFixtures.Placements(shadowExclusionJson: declaration);
        using var fixture = NativeSceneFixtures.Placements(closure);
        fixture.Document.TileSize = 64;
        // This light-only fixture needs no room records or roof links from the placement test scene.
        fixture.Document.Surfaces = new();
        fixture.Document.BuildingIntent = null;
        string path = Path.Combine(Path.GetTempPath(), "khaozengine-light-exclusion-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveAs(fixture.Document, path, tiled ? MapDocumentForm.Tiled : MapDocumentForm.Monolithic);
            var doc = MapDocumentFile.Load(path);
            using var documentSource = MapDocumentSource.FromDocument(doc);
            var source = MapSceneSource.Capture(documentSource, closure).Source!;
            var scope = new MapSceneScope(new(WorldFrame.Origin, new(-16), new(16), null, null,
                Enum.GetValues<MapSurfaceRole>(), null, new()), new(-16, -16, -16, 16, 16, 16), MapSceneChannels.Placements | MapSceneChannels.Annotations);
            var acquired = source.Acquire(scope);
            Assert.Equal(MapSceneStatus.CompleteForScope, acquired.Status);
            var recorder = new MapDrawRecorder(); NativeMapLights.Record(acquired.Scene!, NativeMapPlacementTests.Frame, recorder);
            var set = recorder.Snapshot();
            var draw = set.Draws.Single(d => d.Resource.Key.OwnerId == "emitter-00");
            var shadow = draw.Light!.Value.Shadow;
            var expected = LightShadow.Static(shadow.Key);
            if (mode == "NearSphere") expected = LightShadow.Static(shadow.Key, .625f);
            if (mode is "FixtureBox" or "Absent")
            {
                // Independent owner matrix and literal extremal corners for this positive yaw.
                // Asset bounds are centimetres, the declared box is metres.
                double yaw = (double).371f + .2f, scale = (double)1.2f * .75f;
                double x = -1.25 + (Math.Cos(.371f) * .125 + Math.Sin(.371f) * -.375) * 1.2f;
                double z = 3.75 + (-Math.Sin(.371f) * .125 + Math.Cos(.371f) * -.375) * 1.2f;
                float extent = mode == "FixtureBox" ? 1 : 20 * .01f;
                float margin = mode == "FixtureBox" ? .125f : 0;
                var matrix = new Matrix4x4((float)(Math.Cos(yaw) * scale), 0, (float)(-Math.Sin(yaw) * scale), 0,
                    0, (float)scale, 0, 0, (float)(Math.Sin(yaw) * scale), 0, (float)(Math.Cos(yaw) * scale), 0,
                    (float)(x - draw.Anchor.X), (float)(2.5 + .25 * 1.2f - draw.Anchor.Y), (float)(z - draw.Anchor.Z), 1);
                var low = Vector3.Transform(new(-extent), matrix);
                var high = Vector3.Transform(new(extent), matrix);
                var min = new Vector3(low.X, low.Y, Vector3.Transform(new(extent, -extent, -extent), matrix).Z) - new Vector3(margin);
                var max = new Vector3(high.X, high.Y, Vector3.Transform(new(-extent, extent, extent), matrix).Z) + new Vector3(margin);
                expected = LightShadow.Static(shadow.Key, min, max);
            }
            Assert.Equal(expected, shadow);
            Assert.All(set.Draws.Where(d => !d.Light!.Value.Shadow.Requested), d => Assert.Equal(LightShadow.None, d.Light!.Value.Shadow));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    internal static string? Declaration(string mode) => mode switch
    {
        "None" => """{"kind":"None"}""",
        "NearSphere" => """{"kind":"NearSphere","radius":0.625}""",
        "FixtureBox" => """{"kind":"FixtureBox","min":{"x":-1,"y":-1,"z":-1},"max":{"x":1,"y":1,"z":1},"margin":0.125}""",
        _ => null
    };
}
