using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Runs <see cref="PixelPostProcess"/> over a set of <see cref="IPostChainTargets"/> into a stand-in
    /// swapchain and reads the result back, for the tests of the chain's targets.</summary>
    internal static class PostChainRig
    {
        /// <summary>The legacy order with every optional pass off, so the chain is the blit alone, plus FXAA when asked.</summary>
        public static PixelPostProcessSettings LegacyPlain()
        {
            var s = new PixelPostProcessSettings();
            s.Hdr.Enabled = false;
            return s;
        }

        /// <summary>A perspective camera for the outline's depth linearisation.</summary>
        public static CameraDepth Camera => new(true, 0.1f, 100f);

        /// <summary>Record one run of the chain into <paramref name="output"/> and read it back as RGBA8.</summary>
        public static byte[] RunChain(IGpuDevice gd, PixelPostProcess post, IPostChainTargets res, Output output,
            PixelPostProcessSettings s, bool runFxaa, bool distortionActive = false)
        {
            using (IGpuCommandList cl = gd.Factory.CreateCommandList())
            {
                using (GpuRecording.Open(gd, cl, nameof(PostChainRig)))
                {
                    post.PrepareUniforms(cl, res, s, Camera, runFxaa, distortionActive);
                    post.Run(cl, res, output.Framebuffer, s, runFxaa, distortionActive);
                }
                gd.Submit(cl);
                gd.WaitForIdle();
            }
            return GpuReadback.ToRgba(gd, output.Texture, (int)output.Texture.Width, (int)output.Texture.Height);
        }

        /// <summary>The refusal <see cref="PixelPostProcess.Run"/> throws for <paramref name="res"/>, inside an open
        /// recording, so a run that did not refuse would have recorded its passes.</summary>
        public static InvalidOperationException RunRefused(IGpuDevice gd, PixelPostProcess post, IPostChainTargets res,
            Output output, PixelPostProcessSettings s, bool distortionActive = false)
        {
            InvalidOperationException refused;
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            using (GpuRecording.Open(gd, cl, nameof(PostChainRig)))
                refused = Assert.Throws<InvalidOperationException>(
                    () => post.Run(cl, res, output.Framebuffer, s, runFxaa: false, distortionActive));
            gd.Submit(cl);
            gd.WaitForIdle();
            return refused;
        }

        /// <summary>Every pixel's colour within one RGBA8 step of <paramref name="expected"/>.</summary>
        public static void AssertEveryPixel(byte[] rgba, Color expected)
        {
            for (int i = 0; i < rgba.Length; i += 4)
            {
                Assert.InRange(rgba[i + 0], Byte(expected.R) - 1, Byte(expected.R) + 1);
                Assert.InRange(rgba[i + 1], Byte(expected.G) - 1, Byte(expected.G) + 1);
                Assert.InRange(rgba[i + 2], Byte(expected.B) - 1, Byte(expected.B) + 1);
            }
        }

        static int Byte(float c) => (int)MathF.Round(c * 255f);

        /// <summary>An RGBA8 target standing in for the swapchain the chain's blit writes.</summary>
        public sealed class Output : IDisposable
        {
            public Output(IGpuDevice gd, uint width, uint height)
            {
                Texture = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(width, height, GpuPixelFormat.R8G8B8A8UNorm,
                    GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                Framebuffer = gd.Factory.CreateFramebuffer(null, Texture);
            }

            public IGpuTexture Texture { get; }
            public IGpuFramebuffer Framebuffer { get; }

            public void Dispose()
            {
                Framebuffer.Dispose();
                Texture.Dispose();
            }
        }
    }
}
