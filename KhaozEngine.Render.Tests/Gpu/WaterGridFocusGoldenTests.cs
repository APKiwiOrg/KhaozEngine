using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Golden coverage for the shipped camera-focused water grid on a plane large enough that a uniform grid
    /// cannot resolve the default Gerstner swell near the camera. The eye is far from the plane centre, which
    /// also pins the focus calculation to the camera instead of accidentally accepting a plane-centred warp.
    /// </summary>
    [Collection("HdrGpu")]
    public sealed class WaterGridFocusGoldenTests
    {
        const int W = 480, H = 320;
        const float HalfExtent = 600f;
        const float DefaultFocusBias = 1.8f;

        [GpuFact]
        public void Golden3D_LargeWaterPlaneUsesTheDefaultOffCentreGridFocus()
        {
            var defaults = new WaterSettings();
            Assert.Equal(DefaultFocusBias, defaults.GridFocusBias);

            byte[] focused = Capture(focusBias: null);
            byte[] uniform = Capture(focusBias: 1f);
            float[] focusedGrid = GoldenCompare.Downsample(focused, W, H);
            float[] uniformGrid = GoldenCompare.Downsample(uniform, W, H);

            int waterCells = 0;
            float greatestFocusDelta = 0f;
            for (int cell = 0; cell < focusedGrid.Length / 3; cell++)
            {
                int i = cell * 3;
                float r = focusedGrid[i], g = focusedGrid[i + 1], b = focusedGrid[i + 2];
                if (b >= r - 0.02f && MathF.Max(r, MathF.Max(g, b)) > 0.05f) waterCells++;

                greatestFocusDelta = MathF.Max(greatestFocusDelta,
                    MathF.Max(MathF.Abs(r - uniformGrid[i]),
                        MathF.Max(MathF.Abs(g - uniformGrid[i + 1]), MathF.Abs(b - uniformGrid[i + 2]))));
            }

            Assert.True(waterCells >= 80,
                $"focused water has only {waterCells} visible water cells out of {focusedGrid.Length / 3}");
            Assert.True(greatestFocusDelta >= 0.02f,
                $"focused and uniform grids differ by only {greatestFocusDelta:F4}. The scene no longer observes grid focus.");

            GoldenCompare.AssertOrUpdate("scene3d_water_grid_focus", focused, W, H);
        }

        static byte[] Capture(float? focusBias)
        {
            var camera = new FlyCamera3D
            {
                Position = new Vector3(220f, 18f, -240f),
                Yaw = 0f,
                Pitch = -0.12f,
                FieldOfView = MathF.PI / 3f,
                AspectRatio = (float)W / H,
                NearPlane = 0.1f,
                FarPlane = 900f,
            };
            MeshHandle seabed = default;

            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    seabed = scene.LoadMesh(MeshPrimitives.Tile(HalfExtent * 2f, 1f));
                    scene.CameraOverride = camera;
                    scene.EffectTimeSeconds = 1.37f;
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.12f, 0.22f, 0.36f, 1f);
                    scene.Post.LightDirection = Vector3.Normalize(new Vector3(-0.25f, -0.75f, -0.55f));

                    WaterSettings water = scene.Post.Water;
                    water.WaveSource = WaterWaveSource.Procedural;
                    water.SwellAmplitude = 0.45f;
                    water.SwellWavelength = 42f;
                    water.SwellDirectionDegrees = 30f;
                    water.SwellSpreadDegrees = 55f;
                    water.SwellSteepness = 0.6f;
                    water.SwellSpeed = 0.6f;
                    water.SwellSeed = 0f;
                    water.SwellComponents = 4;
                    if (focusBias.HasValue) water.GridFocusBias = focusBias.Value;
                },
                drawFrame: scene =>
                {
                    scene.Draw(seabed, Matrix4x4.CreateTranslation(0f, -40f, 0f),
                        new Color(0.12f, 0.15f, 0.13f, 1f));
                    scene.DrawWater(new WaterPlane(0f, 0f, 0f, HalfExtent));
                },
                frames: 2);
        }
    }
}
