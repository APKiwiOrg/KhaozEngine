using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// A STEADY WATER FRAME OF A GRIMHOLLOW-SIZED QUEUE ALLOCATES NOTHING: 29 river planes on the flat quad, four
    /// displaced planes on the grid of the mode under test and two planes outside the view. Every buffer the routing
    /// grows is grow-only, so once the first frames have sized them nothing is left to allocate. Measured over the
    /// fake device, which allocates nothing of its own. Metal staging allocations are frame-cost item 5's and are
    /// not visible here.
    /// </summary>
    [Collection("AllocSensitive")]   // a zero-allocation reading measures its neighbours too (#264)
    public sealed class WaterSteadyFrameAllocationTests
    {
        [Theory]
        [InlineData(WaterGridMode.CameraFocused)]
        [InlineData(WaterGridMode.Clipmap)]
        public void ASteadyFrameOfThirtyFivePlanesAllocatesNothing(WaterGridMode mode)
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new NullGpuCommandList();
            var settings = new WaterSettings
            {
                GridMode = mode,
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = 0.3f,
                ClipmapLevels = 1,
                ClipmapRingCells = 8,
            };
            var planes = new WaterPlane[35];
            for (int i = 0; i < 29; i++) planes[i] = new WaterPlane(-70f + 5f * i, 0f, -20f, 2f, look: TileWaterLooks.River);
            for (int i = 0; i < 4; i++) planes[29 + i] = new WaterPlane(-30f + 20f * i, 0f, 30f, 8f);
            planes[33] = new WaterPlane(400f, 0f, 0f, 8f, look: TileWaterLooks.River);
            planes[34] = new WaterPlane(0f, 0f, -400f, 8f);
            var eye = new Vector3(0f, 100f, 0f);

            void Frame() => WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye);

            for (int i = 0; i < 4; i++) Frame();   // size every grow-only buffer and warm every cached slice
            Assert.Equal(2, renderer.LastCulledPlanes);
            if (mode == WaterGridMode.Clipmap) Assert.Equal(0, renderer.LastClipmapRebuilds);
            else Assert.Equal(4, renderer.LastFocusedGridBuilds);

            AllocAssert.NoPerCallAllocation($"20 steady 35-plane {mode} water frames", () =>
            {
                for (int i = 0; i < 20; i++) Frame();
            });
        }
    }
}
