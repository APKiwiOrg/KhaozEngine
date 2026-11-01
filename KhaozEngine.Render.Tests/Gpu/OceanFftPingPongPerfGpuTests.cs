using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A bounded check of the FFT ocean's cross-frame ping-pong contract (#398). Open water uses the
    /// shipping three cascades at 128 with an advancing 60 fps clock. Each source transition gets two
    /// priming frames, then four steady frames exercise both ping-pong directions more than once.
    /// </summary>
    public sealed class OceanFftPingPongPerfGpuTests
    {
        const int W = 960, H = 600;
        const float FrameSeconds = 1f / 60f;
        const int PrimingFrames = 2, ObservedFrames = 4;

        [GpuFact]
        public void The_fft_ocean_costs_no_steady_state_gpu_drain()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            Assert.True(gd.Capabilities.SupportsCompute, $"{gd.Backend} reports no compute support");

            using var preview = new Render3DPreview(gd, W, H);
            Scene3D scene = preview.Scene;
            scene.EnableTiming = true;
            scene.Post.Starfield = false;
            scene.Post.TransparentBackground = false;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Post.Sky.Enabled = true;
            scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);
            // Metre-scale open water with the seabed below the surface, matching the FFT golden.
            MeshHandle seabed = scene.LoadMesh(MeshPrimitives.Tile(160f, 1f));
            scene.Camera.Frame(Vector3.Zero, new Vector3(46f, 30f, 46f));

            float time = 0f;
            void DrawFrame(Scene3D s)
            {
                s.EffectTimeSeconds = time;
                s.Draw(seabed, Matrix4x4.CreateTranslation(0f, -12f, 0f), new Color(0.18f, 0.20f, 0.18f, 1f));
                s.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 70f));
            }

            // The procedural default changes to FFT and back, preserving the same scene as the comparison.
            foreach (WaterWaveSource source in new[] { WaterWaveSource.FftOcean, WaterWaveSource.Procedural })
            {
                scene.Post.Water.WaveSource = source;
                for (int frame = 0; frame < PrimingFrames; frame++)
                {
                    preview.Capture(DrawFrame);
                    time += FrameSeconds;
                }
                for (int frame = 0; frame < ObservedFrames; frame++)
                {
                    preview.Capture(DrawFrame);
                    WaterFrameStats water = scene.LastWaterStats;
                    Assert.Equal(0, water.OceanStalls);
                    Assert.Equal(0d, water.OceanStallMs);
                    time += FrameSeconds;
                }
                // A boundary drain for fixture cleanup is separate from the production stall counters above.
                gd.WaitForIdle();
            }
        }
    }
}
