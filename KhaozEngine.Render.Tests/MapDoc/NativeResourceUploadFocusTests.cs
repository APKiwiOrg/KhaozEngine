using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeResourceUploadFocusTests
{
    [Fact]
    public void FocusFixture_ProjectsOuterRowsIntoSeparateExpectedRegions()
    {
        using var host = new NativeUploadTestScene();
        NativeFoliageUploadProof.ConfigureCamera(host.Scene);
        host.Scene.Begin();
        // The front orthographic view covers local x=-1.5 through 8.5 in 64 pixels.
        float[] expectedX = [11.2f, 30.4f, 49.6f];
        for (int row = 0; row < 3; row++)
        {
            Assert.True(host.Scene.Camera.WorldToScreen(new(32000.25f + row * 3, -499.5f, -32000), 64, 48, out var pixel));
            Assert.Equal(expectedX[row], pixel.X, .001f);
            Assert.Equal(24f, pixel.Y, .001f);
        }
        Assert.InRange(expectedX[0], 8, 19);
        Assert.InRange(expectedX[2], 46, 57);
    }
}

internal static class NativeFoliageUploadProof
{
    internal static SceneFoliageSource Source() => new(new(32000, -500, -32000),
        Enumerable.Range(0, 3).Select(i => Matrix4x4.CreateTranslation(i * 3, 0, 0)).ToArray(),
        new float[] { .1f, .2f, .3f }, new[] { 0, 1, 0 }, NativeRenderAssetFixtures.FoliageSource().Models,
        SceneFoliageTransformSpace.AnchorLocal);

    internal static void LoadGreenParts(Scene3D scene)
    {
        foreach (var part in NativeRenderAssetFixtures.FoliageParts())
        {
            var vertices = part.Mesh.Vertices.Select(v => new ModelVertex(v.Position, v.Normal, new(.1f, .8f, .15f, 1))).ToArray();
            scene.LoadMesh(new(vertices, part.Mesh.Indices32), part.Maps);
        }
    }

    internal static void ConfigureCamera(Scene3D scene)
    {
        scene.RenderOrigin = new(32000, -500, -32000);
        scene.Camera.Azimuth = 0;
        scene.Camera.Elevation = 0;
        scene.Camera.Distance = 10;
        scene.Camera.Target = new(32003.5f, -499.5f, -32000);
        scene.Camera.OrthoSize = 7.5f;
        scene.Camera.Zoom = 1;
        scene.Camera.AspectRatio = 64f / 48f;
        scene.EffectTimeSeconds = 12.5f;
        scene.Post.BackgroundColor = new(.08f, .1f, .14f, 1);
    }

    internal static FoliageRenderSettings Settings => new()
    {
        DrawRadius = 4,
        DensityRadius = 2,
        FadeBandWidth = 1,
        QualityDensity = 1,
        DistantDensity = .35f,
        InstanceFadeBandWidth = .5f,
        WindStrength = 0,
        WindDirection = Vector2.UnitX,
        WindSpeed = 0,
        WindSpatialFrequency = 0
    };

    internal static int GreenPixels(byte[] pixels, int firstX, int endX)
    {
        int count = 0;
        for (int y = 0; y < 48; y++)
            for (int x = firstX; x < endX; x++)
            {
                int i = (y * 64 + x) * 4;
                if (pixels[i + 1] > pixels[i] + 12 && pixels[i + 1] > pixels[i + 2] + 12 && pixels[i + 1] > 20) count++;
            }
        return count;
    }
}
