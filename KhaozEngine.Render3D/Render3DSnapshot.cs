using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Headless offscreen capture: renders a scene to a CPU RGBA buffer with no window. Useful for GPU test
    /// lanes and tooling. The capture is window-free and still requires a GPU device.
    /// </summary>
    public static class Render3DSnapshot
    {
        /// <summary>
        /// Render a multi-instance scene offscreen and return the final image as RGBA8 (w*h*4 bytes).
        /// <paramref name="setup"/> runs once (load meshes via <see cref="Scene3D.LoadMesh(KhaozEngine.Render3D.GltfMesh)"/>, configure
        /// camera/post); <paramref name="drawFrame"/> runs each frame after <see cref="Scene3D.Begin"/> to
        /// queue instances via <see cref="Scene3D.Draw(KhaozEngine.Render3D.MeshHandle, System.Numerics.Matrix4x4)"/>.
        /// </summary>
        public static byte[] Capture(int width, int height, Action<Scene3D> setup, Action<Scene3D> drawFrame, int frames = 1,
            ShadowSettings? shadows = null)
            => CaptureWithBackend(width, height, setup, drawFrame, frames, shadows).Rgba;

        /// <summary>
        /// Render a multi-instance scene offscreen and return the final RGBA8 image, its dimensions, and the
        /// backend reported by the device that rendered it.
        /// </summary>
        public static Render3DCapture CaptureWithBackend(int width, int height, Action<Scene3D> setup,
            Action<Scene3D> drawFrame, int frames = 1, ShadowSettings? shadows = null)
        {
            int renderedFrames = Math.Max(1, frames);
            Render3DCapture capture = default;
            Render3DSnapshotRunner.Capture(width, height, setup, (scene, _) => drawFrame(scene), renderedFrames,
                (_, frame) => capture = frame, renderedFrames - 1, shadows);
            return capture;
        }

        /// <summary>Render one continuous offscreen sequence, reusing its device, scene and render target.
        /// <paramref name="setup"/> runs once. After <see cref="Scene3D.Begin"/>, <paramref name="drawFrame"/>
        /// receives the scene and zero-based frame index. After rendering, <paramref name="onFrame"/> receives
        /// that same index and an independent RGBA8 capture, synchronously on the calling thread.</summary>
        /// <param name="width">Image width in pixels, at least 1.</param>
        /// <param name="height">Image height in pixels, at least 1.</param>
        /// <param name="setup">Loads meshes and configures the scene once before any frame.</param>
        /// <param name="drawFrame">Queues draws and updates animation or camera state for each frame.</param>
        /// <param name="frames">Total rendered frames, including warm-up, at least 1.</param>
        /// <param name="onFrame">Consumes each capture. Its pixel array remains valid after the callback returns
        /// and is never reused by the engine. Captures are streamed rather than accumulated.</param>
        /// <param name="warmupFrames">Initial frames rendered without readback, from 0 through
        /// <paramref name="frames"/>. They still call <paramref name="drawFrame"/> and advance temporal history.
        /// The first delivered index is this value, not zero.</param>
        /// <param name="shadows">Optional construction-time shadow settings.</param>
        /// <remarks>Readback fences the GPU on each delivered frame. A callback exception stops the sequence and
        /// propagates after pending GPU work drains and capture resources are disposed. The scene belongs to
        /// this call and must not be disposed or used after it returns.</remarks>
        public static void CaptureSequence(int width, int height, Action<Scene3D> setup,
            Action<Scene3D, int> drawFrame, int frames, Action<int, Render3DCapture> onFrame,
            int warmupFrames = 0, ShadowSettings? shadows = null)
        {
            Render3DSnapshotRunner.Validate(width, height, frames, warmupFrames);
            ArgumentNullException.ThrowIfNull(setup);
            ArgumentNullException.ThrowIfNull(drawFrame);
            ArgumentNullException.ThrowIfNull(onFrame);
            Render3DSnapshotRunner.Capture(width, height, setup, drawFrame, frames, onFrame, warmupFrames, shadows);
        }

        /// <summary>Single-mesh convenience: load <paramref name="mesh"/>, draw one instance at the origin each frame.</summary>
        public static byte[] Capture(GltfMesh mesh, Action<Scene3D>? configure, int width, int height, int frames,
            ShadowSettings? shadows = null)
        {
            MeshHandle handle = default;
            return Capture(width, height,
                setup: scene => { handle = scene.LoadMesh(mesh); configure?.Invoke(scene); },
                drawFrame: scene => scene.Draw(handle, Matrix4x4.Identity),
                frames: frames, shadows: shadows);
        }
    }
}
