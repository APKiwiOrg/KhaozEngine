using System;
using System.Numerics;
using Veldrid;
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
            var opts = new GraphicsDeviceOptions(
                debug: false, swapchainDepthFormat: null, syncToVerticalBlank: false,
                resourceBindingModel: ResourceBindingModel.Improved,
                preferDepthRangeZeroToOne: true, preferStandardClipSpaceYDirection: true);
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless(opts);
            GraphicsDevice gd = gpu.Device;
            var f = gd.ResourceFactory;

            using Texture finalTex = f.CreateTexture(TextureDescription.Texture2D(
                (uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.RenderTarget | TextureUsage.Sampled));
            using Framebuffer finalFB = f.CreateFramebuffer(new FramebufferDescription(null, finalTex));

            using var scene = new Scene3D(gd, finalFB.OutputDescription);
            setup(scene);

            using CommandList renderCl = f.CreateCommandList();
            for (int i = 0; i < Math.Max(1, frames); i++)
            {
                scene.Begin();
                drawFrame(scene);
                renderCl.Begin();
                scene.RenderInternal(renderCl, width, height, finalFB);
                renderCl.End();
                gd.SubmitCommands(renderCl);
            }
            gd.WaitForIdle();

            using Texture staging = f.CreateTexture(TextureDescription.Texture2D(
                (uint)width, (uint)height, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Staging));
            using (CommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.CopyTexture(finalTex, staging);
                cl.End();
                gd.SubmitCommands(cl);
                gd.WaitForIdle();
            }

            var outBytes = new byte[width * height * 4];
            MappedResource map = gd.Map(staging, MapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        uint si = (uint)(y * (int)map.RowPitch + x * 4);
                        int di = (y * width + x) * 4;
                        outBytes[di + 0] = data[si + 0];
                        outBytes[di + 1] = data[si + 1];
                        outBytes[di + 2] = data[si + 2];
                        outBytes[di + 3] = data[si + 3];
                    }
            }
            gd.Unmap(staging);
            return outBytes;
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
