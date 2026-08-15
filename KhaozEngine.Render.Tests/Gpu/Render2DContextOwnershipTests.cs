using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT THE SNAPSHOT CALLBACK CREATES, THE CAPTURE FREES (#618). A resource handed out by
    /// <see cref="Render2DContext"/> and never disposed used to be left to the per-capture device teardown, which
    /// every Veldrid backend reclaims silently and the native Vulkan backend reports as a
    /// VUID-vkDestroyDevice-device-05137 object leak: six of them across four capture devices turned the
    /// synchronisation-validation gate red while the suite stayed green.
    /// <para>
    /// Device-free on purpose. The leak is a C# ownership bug rather than a Vulkan one, so it is visible through
    /// <see cref="FakeGpuDevice"/>, whose textures record their own disposal. The CI gate is the other half of the
    /// proof and the only one that sees a real vkDestroyDevice.
    /// </para>
    /// </summary>
    public sealed class Render2DContextOwnershipTests
    {
        const int W = 32, H = 32;

        [Fact]
        public void Everything_the_context_hands_out_is_freed_when_the_capture_ends()
        {
            var (core, context) = Setup();

            Texture2D white = context.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            SpriteFont font = context.LoadDefaultFont(24f);

            Assert.Equal(2, context.OwnedCount);
            Assert.False(Handle(white).Disposed);
            Assert.False(Handle(font.Atlas).Disposed);

            context.DisposeOwned();

            Assert.True(Handle(white).Disposed,
                "The white pixel the callback created through the context outlived the capture. On the native "
                + "Vulkan backend that is a VkImage plus its view still alive at vkDestroyDevice.");
            Assert.True(Handle(font.Atlas).Disposed,
                "The font atlas the callback baked through the context outlived the capture.");
            Assert.Equal(0, context.OwnedCount);

            // Idempotent, because the capture's finally runs on the throwing path too.
            context.DisposeOwned();
            core.Dispose();
        }

        /// <summary>
        /// THE SURFACE IS CHECKED RATHER THAN A LIST OF NAMES, so a new loader added to the context is covered on
        /// the day it lands. Every public method that hands back something disposable is invoked here, and each
        /// one has to register what it made. A method that returns a resource and tracks nothing fails this row
        /// with its own name in the message.
        /// </summary>
        [Fact]
        public void Every_resource_returning_context_method_registers_what_it_made()
        {
            var (core, context) = Setup();
            string stem = Path.Combine(Path.GetTempPath(), $"ke-618-{Guid.NewGuid():N}");
            string png = stem + ".png", ttf = stem + ".ttf";
            Png.Write(png, new byte[] { 255, 255, 255, 255 }, 1, 1);
            File.WriteAllBytes(ttf, DefaultFont.Bytes);

            try
            {
                MethodInfo[] resourceReturning = typeof(Render2DContext)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName && typeof(IDisposable).IsAssignableFrom(m.ReturnType))
                    .ToArray();

                Assert.True(resourceReturning.Length >= 7,
                    $"Only {resourceReturning.Length} resource-returning methods found on Render2DContext. The "
                    + "reflection filter has drifted away from the surface it is meant to cover.");

                var untracked = new List<string>();
                foreach (MethodInfo method in resourceReturning)
                {
                    int before = context.OwnedCount;
                    method.Invoke(context, method.GetParameters().Select(p => Argument(p, png, ttf)).ToArray());
                    if (context.OwnedCount != before + 1) untracked.Add(Signature(method));
                }

                Assert.True(untracked.Count == 0,
                    "These Render2DContext members hand a GPU resource to the snapshot callback and register "
                    + "nothing, so nothing frees it before the per-capture device is destroyed (#618): "
                    + string.Join(", ", untracked));

                context.DisposeOwned();
                Assert.Equal(0, context.OwnedCount);
            }
            finally
            {
                core.Dispose();
                File.Delete(png);
                File.Delete(ttf);
            }
        }

        // Each parameter answered BY NAME wherever one type means two things (a byte[] is pixels or a face, a
        // string is a png path, a ttf path or a font key), because the wrong file in the right type is not a test
        // failure: handing a png to the font baker walks off the end of stb_truetype and takes the host with it.
        static object Argument(ParameterInfo p, string pngPath, string ttfPath) => p.Name switch
        {
            "rgba" => new byte[] { 255, 255, 255, 255 },
            "ttf" => DefaultFont.Bytes,
            "pngPath" => pngPath,
            "ttfPath" => ttfPath,
            "key" => FontManager.DefaultKey,
            "fonts" => new FontManager(),
            "width" or "height" or "oversample" or "cacheSlots" => 1,
            "pixelHeight" => 16f,
            _ => throw new NotSupportedException(
                $"Render2DContext.{p.Member.Name} takes a parameter '{p.Name}' this row cannot synthesise. Add it "
                + "here rather than narrowing the filter, or the new loader ships uncovered."),
        };

        static string Signature(MethodInfo m)
            => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})";

        static FakeTexture Handle(Texture2D texture) => (FakeTexture)texture.Handle;

        static (Render2DCore Core, Render2DContext Context) Setup()
        {
            var gd = new FakeGpuDevice();
            IGpuTexture target = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = gd.Factory.CreateFramebuffer(null, target);
            var core = new Render2DCore(gd, fb.Outputs, ownsDevice: false);
            return (core, new Render2DContext(core, W, H));
        }
    }
}
