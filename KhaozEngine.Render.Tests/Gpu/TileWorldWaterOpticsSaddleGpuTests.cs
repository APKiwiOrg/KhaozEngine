using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.Tests.TileWorld;
using Xunit;
using static KhaozEngine.Tests.Gpu.WaterOpticsProofTests;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A public, nonplanar authored bank through the shipped TileWorld adapter and terrain mesher.</summary>
public sealed class TileWorldWaterOpticsSaddleGpuTests
{
    const int Size = 512;

    [GpuFact]
    public void SaddleBankRetainsTheAuthoredSoftShoreline()
    {
        var doc = new TileWorldDocument { Id = "public-saddle-bank", DisplayName = "Public saddle bank" };
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++) doc.GetOrCreateRegion(new RegionCoord(x, z));
        for (int z = 8; z < 13; z++)
            for (int x = 8; x < 13; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Water);
        // SW and NE remain at zero. SE and NW are one metre lower. The real mesher
        // joins the high SW-NE diagonal. Its waterline has two sloping banks beside it.
        doc.SetCornerHeightCm(11, 10, 0, -100);
        doc.SetCornerHeightCm(10, 11, 0, -100);
        TileWorldView? authored = null, legacy = null;
        var legacyOptions = new TileWorldViewOptions();
        var camera = new FlyCamera3D { AspectRatio = 1f, NearPlane = 0.02f, FarPlane = 20f };
        camera.Position = new Vector3(10.5f, 1.8f, -9.7f);
        Vector3 direction = Vector3.Normalize(new Vector3(10.5f, 0f, -10.5f) - camera.Position);
        camera.Yaw = MathF.Atan2(direction.X, direction.Z);
        camera.Pitch = MathF.Asin(direction.Y);
        var points = new Dictionary<string, Vector2>();
        foreach (float offset in new[] { 0.04f, 0.08f, 0.12f, 0.18f, 0.26f })
        {
            Assert.True(camera.WorldToScreen(new Vector3(10.5f, -0.02f, -10.5f - offset), Size, Size, out Vector2 pixel));
            points.Add(offset.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), pixel);
        }
        var images = new List<WaterOpticsImage>();
        Render3DSnapshot.CaptureSequence(Size, Size, scene =>
        {
            scene.CameraOverride = camera;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            scene.Post.Starfield = false;
            scene.Post.Sky.Enabled = false;
            scene.Post.Background = BackgroundMode.Solid;
            scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.08f, 1f);
            scene.Post.LightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
            scene.Post.AmbientColor = Color.White;
            var look = new WaterLook
            {
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = 0f,
                NormalStrength = 0f,
                ShoreFadeDistance = 0.20f,
                FoamStrength = 0f,
                GlintStrength = 0f,
            };
            var adapter = new Scene3DTileWorldScene(scene);
            authored = new TileWorldView(adapter, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
                new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed, WaterLook = look });
            legacyOptions.WaterLook = look;
            legacy = new TileWorldView(adapter, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), legacyOptions);
            authored.LoadRegion(new RegionCoord(0, 0));
            legacy.LoadRegion(new RegionCoord(0, 0));
            Assert.Equal(1, scene.WaterOpticsStats.LiveFields);
        }, (scene, frame) =>
        {
            scene.EffectTimeSeconds = 0f;
            if (frame < 2) authored!.Draw(new Vector3(10.5f, 0f, -10.5f));
            else if (frame == 2) legacy!.Draw(new Vector3(10.5f, 0f, -10.5f));
            else
            {
                // The exact same opaque geometry is the control for optical influence.
                legacyOptions.DrawWater = false;
                legacy!.Draw(new Vector3(10.5f, 0f, -10.5f));
            }
            Assert.Equal(1, scene.WaterOpticsStats.UploadCount);
            scene.PrepareFrame(Size, Size);
        }, frames: 4, (frame, capture) =>
        {
            images.Add(new WaterOpticsImage(capture.Rgba, Size, Size, points));
            if (frame == 3)
            {
                authored!.Dispose();
                legacy!.Dispose();
            }
        }, warmupFrames: 1);
        Save("tile-saddle-authored", images[0]);
        Save("tile-saddle-scene-depth", images[1]);
        Save("tile-saddle-opaque", images[2]);
        var measurements = points.Keys.Select(name => new
        {
            Offset = name,
            AuthoredInfluence = Vector3.Distance(images[0].Sample(name), images[2].Sample(name)),
            SceneInfluence = Vector3.Distance(images[1].Sample(name), images[2].Sample(name)),
        }).ToArray();
        WriteMetrics("tile-saddle-shoreline", measurements);
        // At 6 cm true triangle depth the 20 cm feather must still be observable.
        // A bilinear-only bed gives approximately 48 cm here and reaches the ridge without fading.
        float authoredNear = measurements[1].AuthoredInfluence;
        float sceneNear = measurements[1].SceneInfluence;
        Assert.True(authoredNear <= sceneNear + 0.04f,
            $"Saddle bank loses its soft edge. Authored influence {authoredNear:F6}, triangle-depth control {sceneNear:F6}.");
    }
}
