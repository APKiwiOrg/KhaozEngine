using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Imaging;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>Pixel-readback coverage for the actual sculpt overlay geometry and debug-line draw path.</summary>
[Collection("NativeDeviceLifecycle")]
public sealed class SculptBrushOverlayGoldenGpuTests
{
    const int Width = 480;
    const int Height = 320;
    static readonly SculptBounds Bounds = new(-100, -100, 100, 100);
    static readonly TerrainField Field = new(new TerrainConfig
    {
        WaterLevel = 0f,
        GentleAmplitude = 0f,
        Features = new ITerrainFeature[] { new PlaneSlope() },
    });
    static readonly TerrainChunkRegion Region = new() { OriginX = -32f, OriginZ = -32f, Size = 64f };
    static readonly Vector3 Center = new(0f, SculptBrushOverlay.Lift, 0f);

    [GpuFact]
    public void Golden_slope_overlay_stays_readable_near_and_far_and_suppresses_cleanly()
    {
        byte[] near = Capture(distance: 24f, drawOverlay: true, suppressByNavigation: false);
        byte[] far = Capture(distance: 100f, drawOverlay: true, suppressByNavigation: false);
        byte[] suppressed = Capture(distance: 24f, drawOverlay: true, suppressByNavigation: true);
        byte[] nearBaseline = Capture(distance: 24f, drawOverlay: false, suppressByNavigation: false);
        byte[] farBaseline = Capture(distance: 100f, drawOverlay: false, suppressByNavigation: false);

        int nearOverlay = CountChangedPixels(near, nearBaseline);
        int farOverlay = CountChangedPixels(far, farBaseline);
        int nearMarker = CountCenterMarkerPixels(near, nearBaseline);
        int farMarker = CountCenterMarkerPixels(far, farBaseline);
        Assert.True(nearOverlay > 100, $"near overlay drew only {nearOverlay} readable pixels");
        Assert.True(farOverlay > 40, $"far overlay drew only {farOverlay} readable pixels");
        Assert.True(nearMarker >= 8, $"near center marker drew only {nearMarker} pixels");
        Assert.True(farMarker >= 8, $"far center marker drew only {farMarker} pixels");
        Assert.Equal(0, CountChangedPixels(suppressed, nearBaseline));

        string dir = Path.Combine(FindRepositoryRoot(), ".superpowers", "sdd",
            "MAP-EDITOR-REFRESH-IMPLEMENTATION-2026-09-20", "task-3-artifacts");
        Directory.CreateDirectory(dir);
        PngWriter.Save(Path.Combine(dir, "terrain-overlay-near.png"), near, Width, Height);
        PngWriter.Save(Path.Combine(dir, "terrain-overlay-far.png"), far, Width, Height);
        PngWriter.Save(Path.Combine(dir, "terrain-overlay-suppressed.png"), suppressed, Width, Height);
    }

    static byte[] Capture(float distance, bool drawOverlay, bool suppressByNavigation)
    {
        SculptOverlayLine[] lines = new SculptOverlayLine[SculptBrushOverlay.MaxLines];
        int count = 0;
        if (drawOverlay && suppressByNavigation)
        {
            var controller = new EditorToolController(new EditorDocument(new MapDocument()))
            {
                Mode = EditorToolMode.SculptTerrain,
                BrushRadius = 8f,
                Field = Field,
            };
            var input = new EditorFrameInput(new Vector3(0f, 100f, 0f), -Vector3.UnitY);
            count = SculptCursor.Build(controller, input, Bounds, 1f,
                pointerInViewport: true, navigationOwnsPointer: true, modalOpen: false,
                static (_, _) => true, static _ => 1f,
                static (_, _) => true,
                lines, out SculptOverlayFrame frame);
            Assert.Equal(0, count);
            Assert.False(frame.Visible);
        }
        else if (drawOverlay)
        {
            float markerHalfSize = SculptBrushOverlay.ScreenMarkerHalfSize(distance);
            count = SculptBrushOverlay.Build(Center, 8f, markerHalfSize,
                Bounds, 1f, Field.SampleHeight, static (_, _) => true,
                static (_, _) => true, lines);
            Assert.Equal(SculptBrushOverlay.MaxLines, count);
            Assert.True(lines[0].Start.Y != lines[SculptBrushOverlay.Segments / 2].Start.Y,
                "the GPU fixture must carry the generated overlay across a slope");
        }

        TerrainChunkMesh chunk = TerrainChunkBuilder.Build(Field, Region, lod: 0);
        MeshHandle terrain = default;
        return Render3DSnapshot.Capture(Width, Height,
            setup: scene =>
            {
                Scene3D.SplatMaterialHandle material = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural(32));
                terrain = scene.LoadTerrainChunk(chunk, material);
                scene.Post.Starfield = false;
                scene.Post.Outline = false;
                scene.Post.TransparentBackground = false;
                scene.Post.BackgroundColor = new Color(0.035f, 0.045f, 0.07f, 1f);
                scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
                scene.Post.Water.SwellAmplitude = 0f;
                scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.3f));
                scene.EffectTimeSeconds = 1f;
                var camera = new FlyCamera3D
                {
                    Position = new Vector3(distance * 0.45f, distance * 0.55f, -distance * 0.7f),
                    AspectRatio = (float)Width / Height,
                    FarPlane = 300f,
                };
                PointAt(camera, Center);
                scene.CameraOverride = camera;
            },
            drawFrame: scene =>
            {
                scene.DrawTerrainChunk(terrain, Region);
                scene.DrawWater(new WaterPlane(0f, 0f, 0f, 32f));
                if (!drawOverlay) return;
                Color operation = MapEditorScene.SculptOperationColor(
                    SculptBrush.Raise, SculptOverlayState.Hover);
                SculptBrushOverlay.Draw(scene, lines, count, operation);
            },
            frames: 1);
    }

    sealed class PlaneSlope : ITerrainFeature
    {
        public float Apply(float x, float z, float h) => h + x * 0.18f - z * 0.07f;
    }

    static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))
                || File.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the repository root for overlay evidence.");
    }

    static void PointAt(FlyCamera3D camera, Vector3 target)
    {
        Vector3 direction = Vector3.Normalize(target - camera.Position);
        camera.Yaw = MathF.Atan2(direction.X, direction.Z);
        camera.Pitch = MathF.Asin(direction.Y);
    }

    static int CountChangedPixels(byte[] overlay, byte[] baseline)
    {
        int count = 0;
        for (int i = 0; i < overlay.Length; i += 4)
        {
            int delta = Math.Abs(overlay[i] - baseline[i])
                + Math.Abs(overlay[i + 1] - baseline[i + 1])
                + Math.Abs(overlay[i + 2] - baseline[i + 2]);
            if (delta > 40) count++;
        }
        return count;
    }

    static int CountCenterMarkerPixels(byte[] overlay, byte[] baseline)
    {
        int count = 0;
        for (int y = Height / 2 - 20; y <= Height / 2 + 20; y++)
        {
            for (int x = Width / 2 - 20; x <= Width / 2 + 20; x++)
            {
                int i = (y * Width + x) * 4;
                int delta = Math.Abs(overlay[i] - baseline[i])
                    + Math.Abs(overlay[i + 1] - baseline[i + 1])
                    + Math.Abs(overlay[i + 2] - baseline[i + 2]);
                if (overlay[i] > 200 && overlay[i + 1] > 200 && overlay[i + 2] > 200 && delta > 80) count++;
            }
        }
        return count;
    }
}
