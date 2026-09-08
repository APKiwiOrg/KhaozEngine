using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MsaaPostFxaaGpuTests
{
    [GpuFact(RequiresFourSampleMsaa = true)]
    public void PostFilterRunsAfterMultisampleResolveAndPreservesColor()
    {
        var reference = Capture(postFxaa: false);
        var result = Capture(postFxaa: true);
        Assert.Equal(ProbeSamples, reference.Samples);
        Assert.Equal(ProbeSamples, result.Samples);
        byte[] unfiltered = reference.Pixels;
        byte[] filtered = result.Pixels;
        int changed = 0;
        double before = 0, after = 0;
        for (int i = 0; i < filtered.Length; i += 4)
        {
            double a = Luma(unfiltered, i), b = Luma(filtered, i);
            if (Math.Abs(a - b) > 2) changed++;
            before += a;
            after += b;
        }

        string? probeDirectory = Environment.GetEnvironmentVariable("KE_FXAA_PROBE_DIR");
        if (!string.IsNullOrEmpty(probeDirectory))
        {
            System.IO.Directory.CreateDirectory(probeDirectory);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(probeDirectory, "unfiltered.png"),
                KhaozEngine.Imaging.PngWriter.Encode(unfiltered, 160, 160));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(probeDirectory, "filtered.png"),
                KhaozEngine.Imaging.PngWriter.Encode(filtered, 160, 160));
            Console.WriteLine($"FXAA probe changed={changed}, beforeMean={before / (filtered.Length / 4)}, afterMean={after / (filtered.Length / 4)}");
        }
        Assert.True(changed > 100, $"FXAA must filter the resolved edges, changed pixels: {changed}");
        Assert.True(before / (filtered.Length / 4) > 10, "The reference must contain visible geometry.");
        Assert.InRange(after / before, 0.95, 1.05);
    }

    static int ProbeSamples => int.TryParse(Environment.GetEnvironmentVariable("KE_FXAA_PROBE_SAMPLES"), out int n) ? n : 2;

    static double Luma(byte[] pixels, int i) =>
        .299 * pixels[i] + .587 * pixels[i + 1] + .114 * pixels[i + 2];

    static (byte[] Pixels, int Samples) Capture(bool postFxaa)
    {
        MeshHandle bar = default;
        int samples = 0;
        byte[] pixels = Render3DSnapshot.Capture(160, 160,
            setup: scene =>
            {
                scene.Post.UseSmoothPreset();
                scene.Post.RenderScale = RenderScale.MatchViewport;
                scene.Post.Quality.AntiAliasing = ProbeSamples == 1 ? (postFxaa ? AntiAliasing.Fxaa : AntiAliasing.Off) : AntiAliasing.Msaa(ProbeSamples, postFxaa);
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
                // On the second frame this observes the MRT allocated by the first render.
                samples = scene.RenderTargetSampleCount;
                for (int i = 0; i < 12; i++)
                {
                    Matrix4x4 world = Matrix4x4.CreateScale(.12f, 6f, .08f)
                        * Matrix4x4.CreateRotationZ(.52f)
                        * Matrix4x4.CreateTranslation(-2.5f + i * .45f, 0f, 0f);
                    scene.Draw(bar, world, new Color(.9f, .92f, .95f, 1f));
                }
            }, frames: 2);
        return (pixels, samples);
    }
}
