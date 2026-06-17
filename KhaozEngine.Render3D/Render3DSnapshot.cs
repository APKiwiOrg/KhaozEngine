using System;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Headless offscreen capture: renders a scene to a CPU RGBA buffer with no window (Metal device with
    /// no swapchain). Useful for dev-Mac smoke checks and tooling. Not for CI (needs a Metal GPU).
    /// </summary>
    public static class Render3DSnapshot
    {
        /// <summary>
        /// Render a multi-instance scene offscreen and return the final image as RGBA8 (w*h*4 bytes).
        /// <paramref name="setup"/> runs once (load meshes via <see cref="Scene3D.LoadMesh"/>, configure
        /// camera/post); <paramref name="drawFrame"/> runs each frame after <see cref="Scene3D.Begin"/> to
        /// queue instances via <see cref="Scene3D.Draw"/>.
        /// </summary>
        public static byte[] Capture(int width, int height, Action<Scene3D> setup, Action<Scene3D> drawFrame, int frames = 1)
        {
            // No-arg CreateHeadless uses the exact options the 3D snapshot needs (no depth, no sync, Improved
            // binding, depth-range 0..1, standard clip-Y) so the golden image stays pixel-identical.
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            setup(scene);

            using IGpuCommandList renderCl = f.CreateCommandList();
            for (int i = 0; i < Math.Max(1, frames); i++)
            {
                scene.Begin();
                drawFrame(scene);
                renderCl.Begin();
                scene.RenderInternal(renderCl, width, height, finalFB);
                renderCl.End();
                gd.Submit(renderCl);
            }
            gd.WaitForIdle();

            return GpuReadback.ToRgba(gd, finalTex, width, height);
        }

        /// <summary>Single-mesh convenience: load <paramref name="mesh"/>, draw one instance at the origin each frame.</summary>
        public static byte[] Capture(GltfMesh mesh, Action<Scene3D>? configure, int width, int height, int frames)
        {
            MeshHandle handle = default;
            return Capture(width, height,
                setup: scene => { handle = scene.LoadMesh(mesh); configure?.Invoke(scene); },
                drawFrame: scene => scene.Draw(handle, Matrix4x4.Identity),
                frames: frames);
        }
    }
}
