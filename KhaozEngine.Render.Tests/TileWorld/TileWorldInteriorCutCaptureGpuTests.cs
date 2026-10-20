using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldInteriorCutCaptureGpuTests
{
    const int Width = 160, Height = 120;

    [GpuFact]
    public void SnapshotCutsIndoorPropAndPreservesOutdoorPixelsOnTheFirstFrame()
    {
        byte[] solid = Capture(InteriorCaptureWorld.Options(cut: false));
        byte[] cut = Capture(InteriorCaptureWorld.Options());
        byte[] background = Capture(InteriorCaptureWorld.Options(), drawTrees: false);
        int removed = 0, retained = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                // The top-down camera spans 10 m vertically, with +x to the right and +z down.
                float wx = 13.5f + (x + .5f - Width / 2f) * (10f / Height);
                float wz = -10.5f + (y + .5f - Height / 2f) * (10f / Height);
                int at = (y * Width + x) * 4;
                bool changed = !solid.AsSpan(at, 4).SequenceEqual(cut.AsSpan(at, 4));
                if (changed)
                {
                    Assert.InRange(wx, 10f, 12f);
                    Assert.InRange(wz, -12f, -10f);
                    Assert.True(cut.AsSpan(at, 4).SequenceEqual(background.AsSpan(at, 4)));
                    removed++;
                }
                if (wx > 16.25f && wx < 16.75f && wz > -10.75f && wz < -10.25f)
                {
                    Assert.True(solid.AsSpan(at, 4).SequenceEqual(cut.AsSpan(at, 4)));
                    Assert.False(solid.AsSpan(at, 4).SequenceEqual(background.AsSpan(at, 4)));
                    retained++;
                }
            }
        Assert.True(removed > 10 && retained > 10, $"removed {removed}, retained {retained}");
    }

    [GpuFact]
    public void UnsetOptionsPreserveExistingSnapshotBytes()
    {
        Assert.Equal(Capture(null), Capture(new TileWorldViewOptions
        {
            InteriorMeshes = null, RoofMode = RoofVisibility.Interior,
        }));
    }

    static byte[] Capture(TileWorldViewOptions? options, bool drawTrees = true)
    {
        TileWorldDocument doc = InteriorCaptureWorld.Create();
        if (!drawTrees)
        {
            var objects = new System.Collections.Generic.List<TileObject>(doc.AllObjects());
            foreach (TileObject obj in objects)
                if (obj.ArchetypeId == "tree") doc.RemoveObject(obj.Id);
        }
        byte[]? pixels = null;
        TileWorldSnapshot.CapturePerspectiveSequence(doc, TileWorldCatalogs.Greybox(), new CaptureResolver(),
            new Vector3(13.5f, 10f, 0f), new Vector3(13.5f, 0f, -10.5f), Width, Height, 1,
            (_, capture) => pixels = capture.Rgba, options: options, configureScene: scene =>
            {
                scene.RenderOrigin = Vector3.Zero;
                scene.CameraOverride = null;
                scene.Camera.Target = new Vector3(13.5f, 0f, -10.5f);
                scene.Camera.Azimuth = 0f;
                scene.Camera.Elevation = MathF.BitDecrement(MathF.PI / 2f);
                scene.Camera.OrthoSize = 10f;
                scene.Camera.AspectRatio = Width / (float)Height;
                scene.Post.RenderWidth = Width;
                scene.Post.RenderHeight = Height;
                scene.Post.Starfield = false;
                scene.Post.Outline = false;
                scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
                scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            });
        return Assert.IsType<byte[]>(pixels);
    }

    sealed class CaptureResolver : ITileMeshResolver
    {
        readonly GreyboxMeshResolver _fallback = new();
        public System.Collections.Generic.IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) =>
            archetype.Id == "tree"
                ? [new GltfMeshPart(GreyboxMeshResolver.Box(new Vector3(-.25f, 0f, -.25f),
                    new Vector3(.25f, 2f, .25f), new Vector4(.8f, .4f, .1f, 1f)), default)]
                : _fallback.Resolve(archetype);
    }
}
