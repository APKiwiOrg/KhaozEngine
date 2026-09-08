using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class WaterFlatPlaneTests
    {
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

            renderer.PrepareFrame(new FramePrepare(settings, planes, 0f));
            renderer.Draw(commands, resources, planes, Matrix4x4.Identity, -Vector3.UnitY, Color.White,
                new Vector3(0f, 5f, -5f), settings, new SkySettings(), 0f);

            Assert.Equal(3, commands.IndexedDraws.Count);
            Assert.Equal(6u, commands.IndexedDraws[0].IndexCount);
            Assert.True(commands.IndexedDraws[1].IndexCount > 6);
            Assert.True(commands.IndexedDraws[2].IndexCount > 6);
            Assert.NotSame(commands.IndexedDraws[0].Pipeline, commands.IndexedDraws[1].Pipeline);
            Assert.Same(commands.IndexedDraws[1].Pipeline, commands.IndexedDraws[2].Pipeline);
            Assert.NotSame(commands.IndexedDraws[0].VertexBuffer, commands.IndexedDraws[1].VertexBuffer);
            Assert.NotSame(commands.IndexedDraws[0].IndexBuffer, commands.IndexedDraws[1].IndexBuffer);
            Assert.Equal(2, renderer.LastClipmapRebuilds);
        }
    }

    public sealed class WaterFlatPlaneGoldenTests
    {
        const int Width = 320;
        const int Height = 240;

        [GpuFact]
        public void FlatClipmapQuadMatchesTheCameraFocusedReference()
        {
            float[] reference = Capture(WaterGridMode.CameraFocused);
            float[] quad = Capture(WaterGridMode.Clipmap);
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
            Assert.True(peak > 0.2f, $"the optimized frame is blank or too dark, with a peak channel of {peak}");
            Assert.True(mean < 0.002f && worst < 0.02f,
                $"the six-index quad differs from the tessellated flat reference by mean {mean} and worst {worst}");
        }

        static float[] Capture(WaterGridMode mode)
        {
            MeshHandle ground = default;
            byte[] rgba = Render3DSnapshot.Capture(Width, Height,
                setup: scene =>
                {
                    ground = scene.LoadMesh(MeshPrimitives.Tile(120f, 1f));
                    scene.Post.Starfield = false;
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Water.WaveSource = WaterWaveSource.Procedural;
                    scene.Post.Water.SwellAmplitude = 0f;
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
