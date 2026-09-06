using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MsaaPostFxaaGpuTests
{
    [GpuFact]
    public void PostFilterRunsAfterMultisampleResolveAndPreservesColor()
    {
        byte[] unfiltered = Capture(postFxaa: false);
        byte[] filtered = Capture(postFxaa: true);
        int changed = 0;
        double before = 0, after = 0;
        for (int i = 0; i < filtered.Length; i += 4)
        {
            double a = Luma(unfiltered, i), b = Luma(filtered, i);
            if (Math.Abs(a - b) > 2) changed++;
            before += a;
            after += b;
        }

        Assert.True(changed > 100, $"FXAA must filter the resolved edges, changed pixels: {changed}");
        Assert.True(before / (filtered.Length / 4) > 10, "The reference must contain visible geometry.");
        Assert.InRange(after / before, 0.95, 1.05);
    }

    static double Luma(byte[] pixels, int i) =>
        .299 * pixels[i] + .587 * pixels[i + 1] + .114 * pixels[i + 2];

    static byte[] Capture(bool postFxaa)
    {
        MeshHandle bar = default;
        return Render3DSnapshot.Capture(160, 160,
            setup: scene =>
            {
                scene.Post.UseSmoothPreset();
                scene.Post.RenderScale = RenderScale.MatchViewport;
                scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(2, postFxaa);
                scene.Post.AmbientColor = Color.White;
                scene.Camera.Azimuth = 0f;
                scene.Camera.Elevation = 0f;
                scene.Camera.AspectRatio = 1f;
                scene.Camera.OrthoSize = 4f;
                scene.Camera.Target = Vector3.Zero;
                bar = scene.LoadMesh(MeshPrimitives.Box(1f));
            },
            drawFrame: scene =>
            {
                for (int i = 0; i < 12; i++)
                {
                    Matrix4x4 world = Matrix4x4.CreateScale(.12f, 6f, .08f)
                        * Matrix4x4.CreateRotationZ(.52f)
                        * Matrix4x4.CreateTranslation(-2.5f + i * .45f, 0f, 0f);
                    scene.Draw(bar, world, new Color(.9f, .92f, .95f, 1f));
                }
            }, frames: 2);
    }
}
