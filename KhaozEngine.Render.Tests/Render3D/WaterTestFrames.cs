using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Drives one headless water frame through <see cref="WaterRenderer"/> the way Scene3D does, prepare
    /// then draw, for the tests that read the frame's shape from a <see cref="RecordingGpuCommandList"/>. The
    /// cameras are real ones, never the identity matrix, because the renderer culls planes the view cannot see.</summary>
    internal static class WaterTestFrames
    {
        /// <summary>Never mutated. Read by PackUbo only.</summary>
        static readonly SkySettings Sky = new();

        /// <summary>An orthographic camera straight down at world x = <paramref name="x"/>, seeing x in
        /// [x - 80, x + 80], z in [-80, 80] and heights from -300 to 99.</summary>
        public static Matrix4x4 TopDownAt(float x)
            => Matrix4x4.CreateLookAt(new Vector3(x, 100f, 0f), new Vector3(x, 0f, 0f), -Vector3.UnitZ)
                * Matrix4x4.CreateOrthographic(160f, 160f, 1f, 400f);

        /// <summary><see cref="TopDownAt"/> over the origin.</summary>
        public static readonly Matrix4x4 TopDown = TopDownAt(0f);

        public static void Draw(WaterRenderer renderer, IGpuCommandList commands, RenderResources resources,
            ReadOnlySpan<WaterPlane> planes, WaterSettings settings, Vector3 eye)
            => Draw(renderer, commands, resources, planes, settings, eye, TopDown);

        public static void Draw(WaterRenderer renderer, IGpuCommandList commands, RenderResources resources,
            ReadOnlySpan<WaterPlane> planes, WaterSettings settings, Vector3 eye, Matrix4x4 viewProj)
        {
            if (commands is RecordingGpuCommandList recording) recording.Clear();
            renderer.PrepareFrame(new FramePrepare(settings, planes, 0f));
            renderer.Draw(commands, resources, planes, viewProj, -Vector3.UnitY, Color.White, eye, settings, Sky, 0f);
        }

        public static List<RecordingGpuCommandList.Upload> UploadsTo(RecordingGpuCommandList commands,
            IGpuBuffer buffer)
        {
            var hits = new List<RecordingGpuCommandList.Upload>();
            foreach (RecordingGpuCommandList.Upload upload in commands.Uploads)
                if (ReferenceEquals(upload.Buffer, buffer)) hits.Add(upload);
            return hits;
        }
    }
}
