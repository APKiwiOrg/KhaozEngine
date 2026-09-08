using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class SparseMsaaFallbackGpuTests
    {
        [GpuFact]
        public void RequestedTwoSampleModeResolvesToASupportedCountAndRendersVisibleGeometry()
        {
            using GpuDeviceContext context = GpuDeviceContext.CreateHeadless();
            IGpuDevice device = context.GpuDevice;
            AntiAliasing requested = AntiAliasing.Msaa(2);
            AntiAliasing resolved = requested.ResolveFor(device.Capabilities);
            int expectedSamples = resolved.Mode == AntiAliasingMode.Msaa ? resolved.MsaaSamples : 1;

            Assert.True(device.Capabilities.SupportsMsaaSampleCount(expectedSamples));
            if (!device.Capabilities.SupportsMsaaSampleCount(2)
                && device.Capabilities.SupportsMsaaSampleCount(4))
            {
                Assert.Equal(AntiAliasing.Fxaa, resolved);
                Assert.Equal(1, expectedSamples);
            }

            using var preview = new Render3DPreview(device, 160, 160);
            Scene3D scene = preview.Scene;
            scene.Post.TransparentBackground = false;
            scene.Post.UseSmoothPreset();
            scene.Post.RenderScale = RenderScale.MatchViewport;
            scene.Post.Quality.AntiAliasing = requested;
            scene.Post.AmbientColor = Color.White;
            scene.Camera.Azimuth = 0f;
            scene.Camera.Elevation = 0f;
            scene.Camera.AspectRatio = 1f;
            scene.Camera.OrthoSize = 4f;
            scene.Camera.Target = Vector3.Zero;
            MeshHandle bar = scene.LoadMesh(MeshPrimitives.Box(1f));

            void Draw(Scene3D target)
            {
                for (int i = 0; i < 8; i++)
                {
                    Matrix4x4 world = Matrix4x4.CreateScale(.16f, 5f, .1f)
                        * Matrix4x4.CreateRotationZ(.52f)
                        * Matrix4x4.CreateTranslation(-1.8f + i * .5f, 0f, 0f);
                    target.Draw(bar, world, new Color(.9f, .92f, .95f, 1f));
                }
            }

            preview.Capture(Draw);
            preview.Capture(Draw);
            byte[] pixels = preview.ReadbackRgba();

            Assert.Equal(expectedSamples, scene.RenderTargetSampleCount);
            double luma = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                luma += .299 * pixels[i] + .587 * pixels[i + 1] + .114 * pixels[i + 2];
            Assert.True(luma / (pixels.Length / 4) > 10,
                "the resolved two-sample request produced a black frame instead of visible geometry");
        }
    }
}
