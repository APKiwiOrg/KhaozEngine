using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class WaterPlaneCullingTests
    {
        const int GridVertices = WaterMath.GridResolution * WaterMath.GridResolution;

        static readonly Matrix4x4 Perspective =
            Matrix4x4.CreateLookAt(new Vector3(0f, 10f, 30f), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(1f, 4f / 3f, 0.5f, 500f);

        [Fact]
        public void TheReachBoundsEveryOffsetTheGerstnerMirrorProduces()
        {
            Span<GerstnerWaves.Component> scratch = stackalloc GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            (float Amplitude, float Wavelength, float Steepness, int Components, float Spread)[] swells =
            [
                (0.45f, 42f, 0.6f, 4, 55f),
                (0.45f, 42f, 1f, 1, 0f),
                (2f, 8f, 1f, 8, 180f),
                (0.1f, 120f, 0.3f, 8, 30f),
            ];
            foreach (var swell in swells)
            {
                int n = GerstnerWaves.BuildComponents(swell.Amplitude, swell.Wavelength, 0.3f,
                    GerstnerWaves.DegreesToRadians(swell.Spread), swell.Steepness, 0.6f, 0f, swell.Components, scratch);
                Vector2 reach = WaterSwellReach.Of(swell.Amplitude, swell.Wavelength, swell.Steepness);
                for (float x = -60f; x <= 60f; x += 1.7f)
                    for (float t = 0f; t < 20f; t += 1.3f)
                    {
                        GerstnerWaves.Sample s = GerstnerWaves.Evaluate(x, x * 0.37f, t, swell.Steepness,
                            scratch.Slice(0, n));
                        float horizontal = MathF.Sqrt(s.Offset.X * s.Offset.X + s.Offset.Z * s.Offset.Z);
                        Assert.True(horizontal <= reach.X * 1.0001f + 1e-6f,
                            $"a {swell} swell moved ({x}, {t}) sideways by {horizontal}, past its reach {reach.X}");
                        Assert.True(MathF.Abs(s.Offset.Y) <= reach.Y * 1.0001f + 1e-6f,
                            $"a {swell} swell moved ({x}, {t}) vertically by {s.Offset.Y}, past its reach {reach.Y}");
                    }
            }
        }

        [Fact]
        public void ASwellThatDoesNotDisplaceHasNoReach()
        {
            Assert.Equal(Vector2.Zero, WaterSwellReach.Of(0f, 42f, 0.6f));
            Assert.Equal(Vector2.Zero, WaterSwellReach.Of(-0.5f, 42f, 0.6f));
            Assert.Equal(Vector2.Zero, WaterSwellReach.Of(0.45f, 0f, 0.6f));
            Assert.Equal(Vector2.Zero, WaterSwellReach.Of(0.45f, -10f, 0.6f));
        }

        [Theory]
        [InlineData(100f, 0f, 8f, 0f, 0f, false)]     // wholly beside the view
        [InlineData(90f, 0f, 8f, 5f, 0f, true)]       // beside the view by less than its sideways reach
        [InlineData(85f, 0f, 8f, 0f, 0f, true)]       // straddling the view's edge
        [InlineData(-85f, 0f, -8f, 0f, 0f, true)]     // straddling, with a negative half extent
        [InlineData(0f, 0f, 1000f, 0f, 0f, true)]     // far larger than the view
        [InlineData(0f, 105f, 8f, 0f, 0f, false)]     // above the camera, behind its near plane
        [InlineData(0f, 105f, 8f, 0f, 10f, true)]     // above the camera, its troughs reaching in front of it
        [InlineData(0f, -310f, 8f, 0f, 0f, false)]    // past the far plane
        [InlineData(0f, -310f, 8f, 0f, 20f, true)]    // past the far plane, its crests reaching back inside
        public void AnOrthographicCameraKeepsExactlyThePlanesItCanReach(float centerX, float surfaceY, float half,
            float reachX, float reachY, bool visible)
        {
            FrustumPlanes frustum = FrustumPlanes.Extract(WaterTestFrames.TopDown);
            Assert.Equal(visible, WaterSwellReach.MayBeVisible(new WaterPlane(centerX, surfaceY, 0f, half),
                new Vector2(reachX, reachY), frustum));
        }

        [Theory]
        [InlineData(0f, 0f, 20f, true)]          // in front of the camera
        [InlineData(0f, 60f, 8f, false)]         // behind the camera
        [InlineData(0f, 30f, 1000f, true)]       // under the camera and far larger than the view
        [InlineData(400f, -100f, 20f, false)]    // far off to the side
        public void APerspectiveCameraKeepsExactlyThePlanesItCanReach(float centerX, float centerZ, float half,
            bool visible)
        {
            FrustumPlanes frustum = FrustumPlanes.Extract(Perspective);
            Assert.Equal(visible, WaterSwellReach.MayBeVisible(new WaterPlane(centerX, 0f, centerZ, half),
                Vector2.Zero, frustum));
        }

        [Fact]
        public void CulledPlanesAreNeitherBuiltUploadedNorDrawn()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.5f };
            var flat = new WaterLook { SwellAmplitude = 0f };
            WaterPlane[] planes =
            [
                new WaterPlane(200f, 0f, 0f, 8f, look: flat),   // flat, outside
                new WaterPlane(-40f, 0f, 0f, 8f, look: flat),   // flat, inside
                new WaterPlane(0f, 0f, 300f, 8f),               // displaced, outside
                new WaterPlane(40f, 0f, 0f, 8f),                // displaced, inside
                new WaterPlane(90f, 0f, 0f, 8f),                // displaced, outside by less than its 4 m pinch
                new WaterPlane(0f, 0f, -500f, 8f,
                    look: new WaterLook { WaveSource = WaterWaveSource.FftOcean }),   // ocean: never culled
            ];

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 100f, 0f));

            Assert.Equal(2, renderer.LastCulledPlanes);
            Assert.Equal(WaterRenderer.PlaneRoute.Culled, renderer.LastRoute(0));
            Assert.Equal(WaterRenderer.PlaneRoute.FlatQuad, renderer.LastRoute(1));
            Assert.Equal(WaterRenderer.PlaneRoute.Culled, renderer.LastRoute(2));
            Assert.Equal(WaterRenderer.PlaneRoute.FocusedGrid, renderer.LastRoute(3));
            Assert.Equal(WaterRenderer.PlaneRoute.FocusedGrid, renderer.LastRoute(4));
            Assert.Equal(WaterRenderer.PlaneRoute.FocusedGrid, renderer.LastRoute(5));
            Assert.Equal(4, commands.IndexedDraws.Count);
            Assert.Equal(48u, Assert.Single(
                WaterTestFrames.UploadsTo(commands, commands.IndexedDraws[0].VertexBuffer!)).Bytes);
            Assert.Equal(3, WaterTestFrames.UploadsTo(commands, commands.IndexedDraws[1].VertexBuffer!).Count);
            Assert.Equal(3, renderer.LastFocusedGridBuilds);
        }

        [Fact]
        public void AFrameWithEveryPlaneOutsideOpensNoPass()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            WaterPlane[] planes = [new WaterPlane(300f, 0f, 0f, 8f), new WaterPlane(0f, 0f, -300f, 8f)];

            WaterTestFrames.Draw(renderer, commands, resources, planes, new WaterSettings(), new Vector3(0f, 100f, 0f));

            Assert.Equal(2, renderer.LastCulledPlanes);
            Assert.Empty(commands.IndexedDraws);
            Assert.Empty(commands.Uploads);
            Assert.Equal(0, commands.FramebufferBinds);
        }

        [Fact]
        public void AQueueThatShrinksAndGrowsKeepsEveryDrawOnItsOwnGeometry()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.5f };
            var flat = new WaterLook { SwellAmplitude = 0f };
            WaterPlane[] six =
            [
                new WaterPlane(-60f, 0f, 0f, 6f, look: flat),
                new WaterPlane(-30f, 0f, 0f, 6f),
                new WaterPlane(500f, 0f, 0f, 6f, look: flat),   // culled
                new WaterPlane(0f, 0f, 0f, 6f, look: flat),
                new WaterPlane(30f, 0f, 0f, 6f),
                new WaterPlane(60f, 0f, 0f, 6f, look: flat),
            ];
            WaterPlane[] two = [six[3], six[4]];

            int[] first = Offsets(renderer, commands, resources, six, settings);
            Assert.Equal(new[] { 0, 0, 4, GridVertices, 8 }, first);
            Assert.Equal(new[] { 0, 0 }, Offsets(renderer, commands, resources, two, settings));
            Assert.Equal(48u, Assert.Single(
                WaterTestFrames.UploadsTo(commands, commands.IndexedDraws[0].VertexBuffer!)).Bytes);
            Assert.Equal(first, Offsets(renderer, commands, resources, six, settings));
        }

        [Fact]
        public void AClipmapPlaneCulledForAFrameComesBackWithoutARebuild()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings
            {
                GridMode = WaterGridMode.Clipmap,
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = 0.5f,
                ClipmapLevels = 1,
                ClipmapRingCells = 8,
            };
            WaterPlane[] planes = [new WaterPlane(-40f, 0f, 0f, 8f), new WaterPlane(40f, 0f, 0f, 8f)];
            var eye = new Vector3(0f, 100f, 0f);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye, WaterTestFrames.TopDownAt(0f));
            Assert.Equal(2, renderer.LastClipmapRebuilds);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye, WaterTestFrames.TopDownAt(60f));
            Assert.Equal(WaterRenderer.PlaneRoute.Culled, renderer.LastRoute(0));
            Assert.Single(commands.IndexedDraws);
            Assert.Equal(0, renderer.LastClipmapRebuilds);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye, WaterTestFrames.TopDownAt(0f));
            Assert.Equal(2, commands.IndexedDraws.Count);
            Assert.Equal(0, renderer.LastClipmapRebuilds);
        }

        static int[] Offsets(WaterRenderer renderer, RecordingGpuCommandList commands, RenderResources resources,
            WaterPlane[] planes, WaterSettings settings)
        {
            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 100f, 0f));
            var offsets = new int[commands.IndexedDraws.Count];
            for (int i = 0; i < offsets.Length; i++) offsets[i] = commands.IndexedDraws[i].VertexOffset;
            return offsets;
        }
    }
}
