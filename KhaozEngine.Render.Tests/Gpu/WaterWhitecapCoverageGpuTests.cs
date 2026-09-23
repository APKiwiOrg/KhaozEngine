using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1100">#1100</see>'s far-field half. The whitecap
    /// fold is evaluated per pixel, which removes the triangle-shaped foam a coarse ring drew
    /// (<see cref="WaterSwellFacetGpuTests"/>), and on its own it also raises the distant whitecap coverage to the near
    /// field's: the per-vertex fold was averaged across cells several metres wide, and averaging lowered the crests
    /// below the threshold. The fragment attenuates the fold toward a floor as the pixel footprint grows, calibrated so
    /// the far field keeps about the coverage the per-vertex fold gave it. This pins that band.
    /// <para>
    /// The scene is the Ruinborne lake's water (the clipmap at its defaults and the 42 m swell) on a 600 m sea, seen
    /// from 6 m up looking out to the horizon, with everything but the foam painted black. Coverage is the fraction of
    /// water pixels at least half white whose still-water point is 64 m or more from the clipmap focus, the 8 m rings
    /// and beyond, pooled over several frozen times and headings.
    /// </para>
    /// <para>
    /// Measured on Metal with this exact scene: the per-vertex fold covered 0.73%, the per-pixel fold without the
    /// attenuation 3.64% (5.0 times as much), and with it 1.04% (1.4 times). The band below is half to double the
    /// per-vertex figure, which keeps room for the other backends' rasterizers and still fails the unattenuated fold
    /// by a wide margin. The attenuation's constants were calibrated over five views and both grid modes rather
    /// than against this one, so it sits inside the band rather than on the per-vertex figure.
    /// </para>
    /// </summary>
    [Collection("HdrGpu")]
    public sealed class WaterWhitecapCoverageGpuTests
    {
        readonly ITestOutputHelper _out;

        public WaterWhitecapCoverageGpuTests(ITestOutputHelper output) => _out = output;

        const int W = 960, H = 540;
        const float SeaHalfExtent = 600f;
        static readonly Vector3 Eye = new(0f, 6f, -200f);
        const float Pitch = -0.03f;
        static readonly float[] Yaws = { 0f, 2.1f, 4.2f };
        static readonly float[] Times = { 3.7f, 5.63f };
        const float FarField = 64f;

        /// <summary>Far-field coverage with the fold interpolated from the vertices, before #1100.</summary>
        const float PerVertexCoverage = 0.0073f;

        [GpuFact]
        public void DistantWhitecapsKeepThePerVertexCoverage()
        {
            long water = 0, foam = 0;
            foreach (float yaw in Yaws)
                foreach (float time in Times)
                {
                    var camera = new FlyCamera3D
                    {
                        Position = Eye, Yaw = yaw, Pitch = Pitch, FieldOfView = MathF.PI / 3f,
                        AspectRatio = (float)W / H, NearPlane = 0.1f, FarPlane = 2000f,
                    };
                    byte[] rgba = Render(camera, time);
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                        {
                            int i = (y * W + x) * 4;
                            if (rgba[i + 2] > rgba[i] + 40) continue;   // the blue background, not water
                            Vector3 still = camera.ScreenToGround(new Vector2(x + 0.5f, y + 0.5f), W, H);
                            float fx = Math.Clamp(Eye.X, -SeaHalfExtent, SeaHalfExtent);
                            float fz = Math.Clamp(Eye.Z, -SeaHalfExtent, SeaHalfExtent);
                            if (MathF.Max(MathF.Abs(still.X - fx), MathF.Abs(still.Z - fz)) < FarField) continue;
                            water++;
                            if (rgba[i] + rgba[i + 1] + rgba[i + 2] >= 3 * 128) foam++;
                        }
                }

            float coverage = (float)foam / Math.Max(water, 1);
            _out.WriteLine($"far-field whitecap coverage {coverage:P2} over {water} water pixels " +
                           $"(per-vertex {PerVertexCoverage:P2}, ratio {coverage / PerVertexCoverage:F2})");
            Assert.True(water >= 200_000, $"only {water} far-field water pixels in frame; the framing drifted");
            Assert.InRange(coverage / PerVertexCoverage, 0.5f, 2f);
        }

        static byte[] Render(FlyCamera3D camera, float time)
        {
            MeshHandle seabed = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(SeaHalfExtent * 2f + 400f, 1f));
                    scene.CameraOverride = camera;
                    scene.EffectTimeSeconds = time;
                    scene.Post.RenderScale = RenderScale.MatchViewport;
                    scene.Post.Hdr.Enabled = false;
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0f, 0f, 1f, 1f);
                    scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.25f, -0.55f, 0.75f));

                    WaterSettings w = scene.Post.Water;
                    w.WaveSource = WaterWaveSource.Procedural;
                    w.GridMode = WaterGridMode.Clipmap;
                    w.ClipmapCellSize = 0.5f;
                    w.ClipmapRingCells = 32;
                    w.ClipmapLevels = 0;
                    w.ClipmapGeomorphBand = 0.5f;
                    w.SwellAmplitude = 0.35f;
                    w.SwellWavelength = 42f;
                    w.SwellDirectionDegrees = 125f;
                    w.SwellSpreadDegrees = 55f;
                    w.SwellSteepness = 0.6f;
                    w.SwellSpeed = 0.6f;
                    w.SwellSeed = 0f;
                    w.SwellComponents = 4;
                    // The foam alone, white on black, at the shipped coverage and pattern.
                    w.DeepColor = new Color(0f, 0f, 0f, 1f);
                    w.ShallowColor = new Color(0f, 0f, 0f, 1f);
                    w.HorizonColor = new Color(0f, 0f, 0f, 1f);
                    w.SkyReflectionStrength = 0f;
                    w.GlintStrength = 0f;
                    w.FoamColor = new Color(1f, 1f, 1f, 1f);
                    w.FoamStrength = 1f;
                    w.FoamShoreWidth = 0f;
                    w.ShoreFadeDistance = 0f;
                    w.SurfStrength = 0f;
                },
                drawFrame: scene =>
                {
                    scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, -41f, 0f), new Color(0f, 0f, 0f, 1f));
                    scene.DrawWater(new WaterPlane(0f, 0f, 0f, SeaHalfExtent));
                },
                frames: 2);
        }
    }
}
