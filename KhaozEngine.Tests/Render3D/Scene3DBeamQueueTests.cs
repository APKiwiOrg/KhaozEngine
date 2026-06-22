using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // DrawBeam queues onto a live Scene3D (its ctor needs a GPU device), so these run gated behind
    // KE_GPU_TESTS=1, mirroring Scene3DFillTests. They assert queue accounting + colour resolution only;
    // geometry is covered headlessly by BeamGeometryTests and the on-screen look by the golden snapshot.
    public sealed class Scene3DBeamQueueTests
    {
        static void WithScene(Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        [GpuFact]
        public void DrawBeam_Queues_Then_Begin_Clears() => WithScene(scene =>
        {
            scene.Begin();
            Assert.Equal(0, scene.BeamCount);

            scene.DrawBeam(new Vector3(-1, 0, 0), new Vector3(1, 0, 0), 0.3f, Color.White);
            Assert.Equal(1, scene.BeamCount);

            scene.DrawBeam(Vector3.Zero, new Vector3(0, 2, 0), 0.2f, new Color(1f, 0f, 0f, 1f));
            Assert.Equal(2, scene.BeamCount);

            scene.Begin();
            Assert.Equal(0, scene.BeamCount);
        });

        [GpuFact]
        public void DrawBeam_Degenerate_IsNoOp() => WithScene(scene =>
        {
            scene.Begin();
            scene.DrawBeam(Vector3.One, Vector3.One, 0.5f, Color.White);          // a == b
            scene.DrawBeam(Vector3.Zero, new Vector3(0, 1, 0), 0f, Color.White);  // width 0
            Assert.Equal(0, scene.BeamCount);
        });

        [GpuFact]
        public void DrawBeam_NullColours_ResolveFromColourArg() => WithScene(scene =>
        {
            scene.Begin();
            scene.DrawBeam(Vector3.Zero, new Vector3(1, 0, 0), 0.3f, new Color(1f, 0f, 0f, 1f)); // style null => Default
            var item = scene.BeamItems[0];
            // core resolves to the colour arg (red)
            Assert.Equal(1f, item.CoreColor.X, 4);
            Assert.Equal(0f, item.CoreColor.Y, 4);
            Assert.Equal(1f, item.CoreColor.W, 4);
            // glow derives from the core at 0.4x alpha, same hue
            Assert.Equal(1f, item.GlowColor.X, 4);
            Assert.Equal(0.4f, item.GlowColor.W, 4);
        });

        [GpuFact]
        public void DrawBeam_StyleColours_OverrideTheArg() => WithScene(scene =>
        {
            scene.Begin();
            var style = BeamStyle.Default with
            {
                CoreColor = new Color(0f, 1f, 0f, 1f),
                GlowColor = new Color(0f, 0f, 1f, 0.5f),
            };
            scene.DrawBeam(Vector3.Zero, new Vector3(1, 0, 0), 0.3f, Color.White, style);
            var item = scene.BeamItems[0];
            Assert.Equal(1f, item.CoreColor.Y, 4);   // core = green
            Assert.Equal(1f, item.GlowColor.Z, 4);   // glow = blue
            Assert.Equal(0.5f, item.GlowColor.W, 4);
        });

        [GpuFact]
        public void EffectTimeSeconds_RoundTrips_AndBeginDoesNotClearIt() => WithScene(scene =>
        {
            scene.EffectTimeSeconds = 3.5f;
            scene.Begin();
            Assert.Equal(3.5f, scene.EffectTimeSeconds, 4);   // a clock the host owns: NOT cleared by Begin
        });
    }
}
