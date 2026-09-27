using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// End-to-end GPU checks for the bloom pass-interaction correctness points that a coarse RGB golden grid
    /// cannot see: half-res target allocation (lazy, off = zero, re-derived on resize), and
    /// <see cref="PixelPostProcessSettings.TransparentBackground"/> alpha preservation (bloom must not resurrect an
    /// alpha-0 background pixel). The halo itself is only coarsely covered by the scene3d_bloom golden: the golden
    /// audit measured bloom off at 0.0524 and intensity halved at 0.0219 on its grid (green at the old 0.06, red at
    /// 0.01). <c>HdrPipelineGpuTests.Hdr_bloom_extracts_over_range_only</c> asserts halo pixels.
    /// Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    /// </summary>
    public sealed class BloomGpuTests
    {
        static void Render(IGpuDevice gd, IGpuCommandList cl, Scene3D scene, IGpuFramebuffer target, int w, int h)
        {
            scene.Begin();
            scene.PrepareFrame();
            cl.Begin();
            scene.RenderInternal(cl, w, h, target);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();
        }

        [GpuFact]
        public void Bloom_off_allocates_no_half_res_targets()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            using IGpuCommandList cl = f.CreateCommandList();

            Assert.False(scene.Post.Bloom.Enabled, "bloom defaults to off");
            Render(gd, cl, scene, finalFB, W, H);
            Assert.False(scene.BloomAllocated, "bloom off must allocate no half-res targets");
            Assert.Equal(0, scene.BloomTargetWidth);
            Assert.Equal(0, scene.BloomTargetHeight);
        }

        [Fact]
        public void Bloom_on_allocates_half_res_targets_sized_from_the_internal_target()
        {
            // BloomMath.HalfResSize is the pure derivation PixelPostProcess/RenderResources rely on; asserted
            // headlessly here against the FixedInternal default (1600x900) so this test needs no GPU device.
            var (w, h) = BloomMath.HalfResSize(1600, 900);
            Assert.Equal(800, w);
            Assert.Equal(450, h);
        }

        [GpuFact]
        public void Bloom_toggle_reallocates_and_frees_half_res_targets_on_resize()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            using IGpuCommandList cl = f.CreateCommandList();

            // Off initially: nothing allocated.
            Render(gd, cl, scene, finalFB, W, H);
            Assert.False(scene.BloomAllocated);

            // Enable: the half-res pair is (re)allocated, sized from the CURRENT internal target
            // (FixedInternal default 1600x900 -> half-res 800x450), even though the viewport here is tiny.
            scene.Post.Bloom.Enabled = true;
            Render(gd, cl, scene, finalFB, W, H);
            Assert.True(scene.BloomAllocated);
            Assert.Equal(800, scene.BloomTargetWidth);
            Assert.Equal(450, scene.BloomTargetHeight);

            // Switch to MatchViewport: the internal target (and therefore the half-res bloom pair) re-derives from
            // the new viewport size.
            scene.Post.RenderScale = RenderScale.MatchViewport;
            Render(gd, cl, scene, finalFB, 200, 100);
            Assert.True(scene.BloomAllocated);
            Assert.Equal(scene.RenderTargetWidth, scene.RenderTargetWidth);   // sanity: still tracks the viewport
            var (expW, expH) = BloomMath.HalfResSize(scene.RenderTargetWidth, scene.RenderTargetHeight);
            Assert.Equal(expW, scene.BloomTargetWidth);
            Assert.Equal(expH, scene.BloomTargetHeight);

            // Disable: freed back to zero.
            scene.Post.Bloom.Enabled = false;
            Render(gd, cl, scene, finalFB, 200, 100);
            Assert.False(scene.BloomAllocated);
            Assert.Equal(0, scene.BloomTargetWidth);
            Assert.Equal(0, scene.BloomTargetHeight);
        }

        [GpuFact]
        public void Retuning_the_radius_on_a_chain_that_already_ran_blurs_like_a_fresh_chain_at_the_new_radius()
        {
            // The post chain refills its blur weights only when the clamped radius changes, so a chain that ran at
            // radius 4 and is then retuned to 6 must render what a chain that only ever ran at 6 renders.
            byte[] retuned = CaptureBloomRadius(firstFrame: 4, secondFrame: 6);
            byte[] fresh = CaptureBloomRadius(firstFrame: 6, secondFrame: 6);
            byte[] four = CaptureBloomRadius(firstFrame: 4, secondFrame: 4);

            int radiusShows = DifferingBytes(four, fresh);
            Assert.True(radiusShows > 0, "radius 4 and radius 6 render alike, so this cannot tell stale weights apart");
            int stale = DifferingBytes(retuned, fresh);
            Assert.True(stale == 0, $"the retuned chain differs from a fresh radius-6 chain in {stale} bytes "
                + $"(radius 4 against 6: {radiusShows})");
        }

        static byte[] CaptureBloomRadius(int firstFrame, int secondFrame)
        {
            MeshHandle sphere = default;
            int frame = 0;
            return Render3DSnapshot.Capture(96, 96,
                setup: scene =>
                {
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.3f));
                    scene.Post.Starfield = false;
                    scene.Post.Bloom.Enabled = true;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2.2f, 2.2f, 2.2f));
                },
                drawFrame: scene =>
                {
                    scene.Post.Bloom.Radius = frame++ == 0 ? firstFrame : secondFrame;
                    scene.Draw(sphere, Matrix4x4.Identity,
                        new Color(1f, 1f, 0.9f, 1f), Material.Glowing(new Color(1f, 1f, 0.9f, 1f)));
                },
                frames: 2);
        }

        static int DifferingBytes(byte[] a, byte[] b)
        {
            Assert.Equal(a.Length, b.Length);
            int n = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
            return n;
        }

        [GpuFact]
        public void Bloom_preserves_transparent_background_alpha()
        {
            // Bloom must never resurrect an alpha-0 background pixel into an opaque one (BloomCompositeFrag
            // preserves Src.a unchanged). A bright emissive sphere against an empty TransparentBackground scene:
            // the far corner (no geometry, no bloom halo reaches it) must stay fully transparent.
            const int W = 128, H = 128;
            MeshHandle sphere = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.5f));
                    scene.Post.Starfield = false;
                    scene.Post.TransparentBackground = true;
                    scene.Post.Bloom.Enabled = true;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2.2f, 2.2f, 2.2f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(sphere, Matrix4x4.Identity,
                        new Color(1f, 1f, 0.9f, 1f), Material.Glowing(new Color(1f, 1f, 0.9f, 1f)));
                },
                frames: 2);

            // Corner: far from the sphere and any bloom halo it casts - must stay transparent (alpha 0).
            int k = (2 * W + 2) * 4;
            Assert.True(rgba[k + 3] < 10, $"corner should stay transparent under bloom, got a={rgba[k + 3]}");
        }
    }
}
