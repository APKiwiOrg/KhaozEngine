using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

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
        /// <paramref name="setup"/> runs once (load meshes via <see cref="Scene3D.LoadMesh(KhaozEngine.Render3D.GltfMesh)"/>, configure
        /// camera/post); <paramref name="drawFrame"/> runs each frame after <see cref="Scene3D.Begin"/> to
        /// queue instances via <see cref="Scene3D.Draw(KhaozEngine.Render3D.MeshHandle, System.Numerics.Matrix4x4)"/>.
        /// </summary>
        public static byte[] Capture(int width, int height, Action<Scene3D> setup, Action<Scene3D> drawFrame, int frames = 1,
            ShadowSettings? shadows = null)
        {
            // No-arg CreateHeadless uses the exact options the 3D snapshot needs (no depth, no sync, Improved
            // binding, depth-range 0..1, standard clip-Y) so the golden image stays pixel-identical.
            // The headless half of the 18.0.0 registration. AppWindow does this for a windowed game and there is
            // no AppWindow here, so a snapshot tool would otherwise have to know that KhaozEngine.Gpu builds no
            // device of its own any more. Registers the kind the selector resolves to
            // (KE_GRAPHICS_BACKEND decides it here, since a headless host stores no player preference) plus
            // this platform's own as the fallback target, and only where nothing is registered already, so a
            // harness that seated its own provider (the GPU test suite does) keeps it.
            GpuBackends.RegisterResolvedIfUnregistered();
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs, shadows);
            setup(scene);

            using IGpuCommandList renderCl = f.CreateCommandList();
            for (int i = 0; i < Math.Max(1, frames); i++)
            {
                scene.Begin();
                drawFrame(scene);
                // Every producer with GPU work of its own goes here, between the queue being filled and the frame's
                // list being opened. See Scene3D.PrepareFrame - opening a second list after renderCl.Begin() is
                // what corrupts the device on Direct3D11 in immediate-context mode (#423).
                scene.PrepareFrame();
                using (GpuRecording.Open(gd, renderCl, "Render3DSnapshot.Capture"))
                    scene.RenderInternal(renderCl, width, height, finalFB);
                gd.Submit(renderCl);
            }
            gd.WaitForIdle();

            return GpuReadback.ToRgba(gd, finalTex, width, height);
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
