using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class WaterFlatPlaneTests
    {
        [Theory]
        [InlineData(0f, 42f, true)]
        [InlineData(-0.5f, 42f, true)]
        [InlineData(0.45f, 0f, true)]
        [InlineData(0.45f, -10f, true)]
        [InlineData(-0.5f, -10f, true)]
        [InlineData(0.45f, 42f, false)]
        [InlineData(1e-30f, 42f, false)]
        public void EverySwellTheGerstnerGateSwitchesOffDrawsAsAFlatQuad(float amplitude, float wavelength, bool flat)
        {
            var scene = new WaterSettings
            {
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = amplitude,
                SwellWavelength = wavelength,
            };
            Assert.Equal(flat, WaterRenderer.UsesFlatQuad(new WaterPlane(0f, 0f, 0f, 8f), scene));

            // The same knobs arriving through a look, over a scene whose own swell displaces.
            var look = new WaterLook { SwellAmplitude = amplitude, SwellWavelength = wavelength };
            Assert.Equal(flat, WaterRenderer.UsesFlatQuad(new WaterPlane(0f, 0f, 0f, 8f, look: look),
                new WaterSettings { WaveSource = WaterWaveSource.Procedural }));

            // The CPU mirror of the swell agrees: these swells, and only these, build no component at all.
            Span<GerstnerWaves.Component> scratch = stackalloc GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            Assert.Equal(flat, GerstnerWaves.BuildComponents(amplitude, wavelength, 0f, 0f, 0.6f, 0.6f, 0f, 4,
                scratch) == 0);
        }

        [Fact]
        public void ClipmapFrameUsesSixIndicesOnlyForEffectiveProceduralZeroSwellPlanes()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings
            {
                GridMode = WaterGridMode.Clipmap,
                WaveSource = WaterWaveSource.Procedural,
                SwellAmplitude = 1f,
                ClipmapLevels = 1,
                ClipmapRingCells = 8,
            };
            WaterPlane[] planes =
            [
                new WaterPlane(-20f, 0f, 0f, 8f, look: new WaterLook { SwellAmplitude = 0f }),
                new WaterPlane(0f, 0f, 0f, 8f),
                new WaterPlane(20f, 0f, 0f, 8f,
                    look: new WaterLook { WaveSource = WaterWaveSource.FftOcean, SwellAmplitude = 0f }),
            ];

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 5f, -5f));

            Assert.Equal(3, commands.IndexedDraws.Count);
            Assert.Equal(6u, commands.IndexedDraws[0].IndexCount);
            Assert.True(commands.IndexedDraws[1].IndexCount > 6);
            Assert.True(commands.IndexedDraws[2].IndexCount > 6);
            Assert.NotSame(commands.IndexedDraws[0].Pipeline, commands.IndexedDraws[1].Pipeline);
            Assert.Same(commands.IndexedDraws[1].Pipeline, commands.IndexedDraws[2].Pipeline);
            Assert.NotSame(commands.IndexedDraws[0].VertexBuffer, commands.IndexedDraws[1].VertexBuffer);
            Assert.NotSame(commands.IndexedDraws[0].IndexBuffer, commands.IndexedDraws[1].IndexBuffer);
            Assert.Equal(2, renderer.LastClipmapRebuilds);
            // The quad lands before the pass opens, as the clipmap slices always did.
            RecordingGpuCommandList.Upload quad = Assert.Single(
                WaterTestFrames.UploadsTo(commands, commands.IndexedDraws[0].VertexBuffer!));
            Assert.Equal(0, quad.FramebufferBindsBefore);
            Assert.Equal(1, commands.FramebufferBinds);
        }

        [Fact]
        public void CameraFocusedFrameDrawsEveryFlatPlaneFromOneQuadUploadAheadOfThePass()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 1f };
            WaterPlane[] planes =
            [
                new WaterPlane(-20f, 0f, 0f, 8f, look: new WaterLook { SwellAmplitude = 0f }),
                new WaterPlane(0f, 0f, 0f, 8f),
                new WaterPlane(20f, 1f, 0f, 8f, 4f, look: new WaterLook { SwellWavelength = -1f }),
                new WaterPlane(0f, 0f, 20f, 8f, look: new WaterLook { SwellAmplitude = -2f }),
            ];

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 5f, -5f));

            IReadOnlyList<RecordingGpuCommandList.IndexedDraw> draws = commands.IndexedDraws;
            Assert.Equal(4, draws.Count);
            Assert.Equal((uint)WaterMath.GridIndexCount, draws[1].IndexCount);
            IGpuBuffer quads = draws[0].VertexBuffer!;
            int[] quadOffsets = [0, -1, 4, 8];
            for (int i = 0; i < 4; i++)
            {
                if (i == 1) continue;
                Assert.Equal(WaterRenderer.FlatIndexCount, draws[i].IndexCount);
                Assert.Same(quads, draws[i].VertexBuffer);
                Assert.Equal(quadOffsets[i], draws[i].VertexOffset);
                Assert.Same(draws[1].Pipeline, draws[i].Pipeline);   // the regular water pipeline, as the grid uses
            }

            RecordingGpuCommandList.Upload upload = Assert.Single(WaterTestFrames.UploadsTo(commands, quads));
            Assert.Equal(0u, upload.Offset);
            Assert.Equal(3u * 48u, upload.Bytes);
            Assert.Equal(0, upload.FramebufferBindsBefore);
            Assert.Equal(1, commands.FramebufferBinds);

            // The third plane's quad is the second one written: its own rectangle at its own height.
            ReadOnlySpan<Vector3> corners = MemoryMarshal.Cast<byte, Vector3>(upload.Data!.AsSpan()).Slice(4, 4);
            Assert.Equal(new Vector3(12f, 1f, -4f), corners[0]);
            Assert.Equal(new Vector3(28f, 1f, -4f), corners[1]);
            Assert.Equal(new Vector3(12f, 1f, 4f), corners[2]);
            Assert.Equal(new Vector3(28f, 1f, 4f), corners[3]);
        }

        [Fact]
        public void ASteadyRiverOfThirtyFivePlanesUploadsTwiceAndBothLandBeforeThePass()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings();   // the scene's own sea displaces, only the river look is flat
            WaterPlane[] planes = RiverPlanes(35);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 9f, 6f));
            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, new Vector3(0f, 9f, 6f));

            Assert.Equal(35, commands.IndexedDraws.Count);
            Assert.Equal(2, commands.Uploads.Count);   // the uniform slots and the quads, nothing per plane
            foreach (RecordingGpuCommandList.Upload upload in commands.Uploads)
                Assert.Equal(0, upload.FramebufferBindsBefore);
            Assert.Equal(1, commands.FramebufferBinds);
        }

        [Fact]
        public void TheQuadBufferGrowsOnlyAndRetiresTheBufferItReplaces()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
            var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs, retired);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings();
            var eye = new Vector3(0f, 9f, 6f);

            WaterTestFrames.Draw(renderer, commands, resources, RiverPlanes(3), settings, eye);
            var first = (FakeBuffer)commands.IndexedDraws[0].VertexBuffer!;
            WaterTestFrames.Draw(renderer, commands, resources, RiverPlanes(9), settings, eye);
            var grown = (FakeBuffer)commands.IndexedDraws[0].VertexBuffer!;
            Assert.NotSame(first, grown);
            Assert.True(grown.SizeInBytes >= 9u * 48u, $"the grown quad buffer holds only {grown.SizeInBytes} bytes");
            Assert.False(first.Disposed, "the replaced quad buffer was freed at the grow, while a prior frame may still read it");

            WaterTestFrames.Draw(renderer, commands, resources, RiverPlanes(2), settings, eye);
            Assert.Same(grown, commands.IndexedDraws[0].VertexBuffer);   // shrinking reallocates nothing

            renderer.Dispose();
            retired.BeginFrame();
            Assert.True(first.Disposed, "the safe retirement boundary must free the replaced quad buffer");
        }

        static WaterPlane[] RiverPlanes(int count)
        {
            var planes = new WaterPlane[count];
            for (int i = 0; i < count; i++)
                planes[i] = new WaterPlane(-68f + 4f * i, 0f, 0f, 1.5f, look: TileWaterLooks.River);
            return planes;
        }
    }

    public sealed class WaterFlatPlaneGoldenTests
    {
        const int Width = 320;
        const int Height = 240;

        // Positive, so the plane takes the tessellated grid, and far too small to move a vertex or tilt a normal by
        // a representable amount. Every component amplitude sits under the 1e-6 guard that zeroes the horizontal
        // pinch, and the vertical offset and the slope round away. That makes it the tessellated flat reference now
        // that a switched-off swell never reaches the grid.
        const float VanishingSwell = 1e-30f;

        [GpuFact]
        public void FlatQuadsMatchTheTessellatedFlatReferenceInEveryGridMode()
        {
            float[] reference = Capture(WaterGridMode.CameraFocused, VanishingSwell, 42f);
            AssertWithinFlatBound(reference, Capture(WaterGridMode.CameraFocused, 0f, 42f), "camera-focused, zero amplitude");
            AssertWithinFlatBound(reference, Capture(WaterGridMode.Clipmap, 0f, 42f), "clipmap, zero amplitude");
            AssertWithinFlatBound(reference, Capture(WaterGridMode.CameraFocused, -0.5f, 42f), "camera-focused, negative amplitude");
            AssertWithinFlatBound(reference, Capture(WaterGridMode.Clipmap, 0.45f, -10f), "clipmap, negative wavelength");
        }

        static void AssertWithinFlatBound(float[] reference, float[] quad, string what)
        {
            double sum = 0;
            float worst = 0f;
            float peak = 0f;
            for (int i = 0; i < quad.Length; i++)
            {
                float delta = MathF.Abs(quad[i] - reference[i]);
                sum += delta;
                worst = MathF.Max(worst, delta);
                peak = MathF.Max(peak, quad[i]);
            }

            float mean = (float)(sum / quad.Length);
            Assert.True(peak > 0.2f, $"{what}: the optimized frame is blank or too dark, with a peak channel of {peak}");
            Assert.True(mean < 0.002f && worst < 0.02f,
                $"{what}: the six-index quad differs from the tessellated flat reference by mean {mean} and worst {worst}");
        }

        static float[] Capture(WaterGridMode mode, float amplitude, float wavelength)
        {
            MeshHandle ground = default;
            byte[] rgba = Render3DSnapshot.Capture(Width, Height,
                setup: scene =>
                {
                    ground = scene.LoadMesh(MeshPrimitives.Tile(120f, 1f));
                    scene.Post.Starfield = false;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Water.WaveSource = WaterWaveSource.Procedural;
                    scene.Post.Water.SwellAmplitude = amplitude;
                    scene.Post.Water.SwellWavelength = wavelength;
                    scene.Post.Water.GridMode = mode;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(34f, 24f, 34f));
                    scene.EffectTimeSeconds = 2f;
                },
                drawFrame: scene =>
                {
                    scene.Draw(ground, Matrix4x4.CreateTranslation(0f, -8f, 0f),
                        new Color(0.16f, 0.18f, 0.14f, 1f));
                    scene.DrawWater(new WaterPlane(0f, 0f, 0f, 50f));
                },
                frames: 2);
            return GoldenCompare.Downsample(rgba, Width, Height);
        }
    }
}
