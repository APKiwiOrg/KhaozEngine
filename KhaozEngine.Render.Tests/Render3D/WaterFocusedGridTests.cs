using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class WaterFocusedGridTests
    {
        const int GridVertices = WaterMath.GridResolution * WaterMath.GridResolution;
        const uint GridBytes = GridVertices * 12u;

        [Fact]
        public void EachDisplacedPlaneGetsItsOwnGridSliceUploadedAheadOfThePass()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.5f };
            WaterPlane[] planes =
            [
                new WaterPlane(-30f, 0f, 0f, 10f),
                new WaterPlane(0f, 0f, 0f, 8f, look: new WaterLook { SwellAmplitude = 0f }),
                new WaterPlane(0f, 2f, 30f, 12f, 6f),
                new WaterPlane(30f, -1f, -30f, 9f),
            ];
            var eye = new Vector3(4f, 12f, -6f);

            WaterTestFrames.Draw(renderer, commands, resources, planes, settings, eye);

            IReadOnlyList<RecordingGpuCommandList.IndexedDraw> draws = commands.IndexedDraws;
            Assert.Equal(4, draws.Count);
            Assert.Equal(6u, draws[1].IndexCount);   // the flat plane between them keeps its quad
            int[] grids = [0, 2, 3];
            IGpuBuffer slices = draws[0].VertexBuffer!;
            for (int k = 0; k < grids.Length; k++)
            {
                RecordingGpuCommandList.IndexedDraw draw = draws[grids[k]];
                Assert.Equal((uint)WaterMath.GridIndexCount, draw.IndexCount);
                Assert.Equal(0u, draw.IndexStart);
                Assert.Equal(k * GridVertices, draw.VertexOffset);
                Assert.Same(slices, draw.VertexBuffer);
            }

            List<RecordingGpuCommandList.Upload> uploads = WaterTestFrames.UploadsTo(commands, slices);
            Assert.Equal(3, uploads.Count);
            var expected = new Vector3[GridVertices];
            var axis = new float[2 * WaterMath.GridResolution];
            for (int k = 0; k < grids.Length; k++)
            {
                RecordingGpuCommandList.Upload upload = uploads[k];
                Assert.Equal((uint)k * GridBytes, upload.Offset);
                Assert.Equal(GridBytes, upload.Bytes);
                Assert.Equal(0, upload.FramebufferBindsBefore);
                WaterMath.BuildGridPositions(planes[grids[k]], eye.X, eye.Z, settings.GridFocusBias, expected, axis);
                Assert.True(MemoryMarshal.Cast<byte, Vector3>(upload.Data!.AsSpan()).SequenceEqual(expected),
                    $"slice {k} does not hold plane {grids[k]}'s grid");
            }
            Assert.Equal(1, commands.FramebufferBinds);
        }

        [Fact]
        public void TheGridBufferGrowsOnlyAndRetiresTheBufferItReplaces()
        {
            using var device = new FakeGpuDevice();
            using var resources = new RenderResources(device, 96, 64, false);
            using GpuRetireQueue retired = GpuRetireQueue.CreateFrameCounted(device, frameDelay: 1);
            var renderer = new WaterRenderer(device, resources.ColorDepthFB.Outputs, retired);
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
            var settings = new WaterSettings { WaveSource = WaterWaveSource.Procedural, SwellAmplitude = 0.5f };
            var eye = new Vector3(0f, 12f, -6f);

            WaterTestFrames.Draw(renderer, commands, resources, Displaced(1), settings, eye);
            var first = (FakeBuffer)commands.IndexedDraws[0].VertexBuffer!;
            Assert.Equal(GridBytes, first.SizeInBytes);   // one plane costs exactly what the old fixed buffer did

            WaterTestFrames.Draw(renderer, commands, resources, Displaced(3), settings, eye);
            var grown = (FakeBuffer)commands.IndexedDraws[0].VertexBuffer!;
            Assert.NotSame(first, grown);
            Assert.True(grown.SizeInBytes >= 3u * GridBytes, $"the grown grid buffer holds only {grown.SizeInBytes} bytes");
            Assert.False(first.Disposed, "the replaced grid buffer was freed at the grow, while a prior frame may still read it");

            WaterTestFrames.Draw(renderer, commands, resources, Displaced(2), settings, eye);
            Assert.Same(grown, commands.IndexedDraws[0].VertexBuffer);

            renderer.Dispose();
            retired.BeginFrame();
            Assert.True(first.Disposed, "the safe retirement boundary must free the replaced grid buffer");
        }

        static WaterPlane[] Displaced(int count)
        {
            var planes = new WaterPlane[count];
            for (int i = 0; i < count; i++) planes[i] = new WaterPlane(-60f + 40f * i, 0f, 0f, 10f);
            return planes;
        }
    }
}
