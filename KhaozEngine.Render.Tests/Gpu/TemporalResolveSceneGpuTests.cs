using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.Gpu.Rgba8Stats;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The resolve inside a real frame: the post chain's bloom and distortion run on the display targets after it, and a
    /// transparent model-pass writer over the sky shows, because the background now draws ahead of it
    /// (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md, section 1). A later render inside the frame
    /// never resolves and looks like a render without temporal anti-aliasing at the internal size, and a Solid frame's
    /// opaque copy holds the cleared background.
    /// </summary>
    public sealed class TemporalResolveSceneGpuTests
    {
        /// <summary>Each effect on its own against neither, so one cannot pass for the other.</summary>
        [GpuFact]
        public void Bloom_and_distortion_run_on_the_display_chain_after_the_resolve()
        {
            byte[] plain = EffectsFrame(bloom: false, distortion: false);
            byte[] bloomed = EffectsFrame(bloom: true, distortion: false);
            byte[] rippled = EffectsFrame(bloom: false, distortion: true);
            Assert.True(LumaDeviation(plain) > 2.0, "the frame is not a flat fill");
            double bloomChange = MeanAbs(bloomed, plain);
            Assert.True(bloomChange > 0.5, $"bloom changed nothing after the resolve: mean abs {bloomChange:0.000}");
            double rippleChange = MeanAbs(rippled, plain);
            Assert.True(rippleChange > 0.5, $"the ripple changed nothing after the resolve: mean abs {rippleChange:0.000}");
        }

        [GpuFact]
        public void A_second_render_at_another_size_leaves_the_history_valid()
        {
            using var h = new TemporalFixture(240, 160);
            h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            MeshHandle box = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
            void Draw(Scene3D s) => s.Draw(box, Matrix4x4.CreateScale(2f), new Color(0.8f, 0.6f, 0.4f, 1f));

            h.Frame((s, _) => Draw(s));
            h.RenderSecond(96, 64);   // an offscreen capture inside the same frame, at another size
            Assert.Equal((240, 160), (h.Scene.TemporalHistory.DisplayWidth, h.Scene.TemporalHistory.DisplayHeight));
            h.Frame((s, _) => Draw(s));

            Assert.True(h.Scene.TemporalHistory.IsValid, $"the capture reset the history: {h.Scene.TemporalHistory.LastReset}");
            Assert.True(h.Scene.LastTemporalDiagnostics.HistoryValid);
            Assert.Equal(1f, h.Scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);
        }

        // A bright box, with bloom and a distortion ripple over it as asked, resolved at Quality.
        static byte[] EffectsFrame(bool bloom, bool distortion)
        {
            using var h = new TemporalFixture(240, 160);
            h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            h.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            h.Scene.Post.Bloom.Enabled = bloom;
            MeshHandle box = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
            byte[] rgba = Array.Empty<byte>();
            for (int i = 0; i < 4; i++)
                rgba = h.Frame((s, _) =>
                {
                    s.Draw(box, Matrix4x4.CreateScale(2f), new Color(3f, 2.5f, 1.5f, 1f));
                    if (!distortion) return;
                    s.DrawDistortion(new DistortionSprite
                    {
                        Position = new Vector3(0f, 0f, 1.2f), Size = 2.2f, Shape = DistortionShape.Ripple,
                        Strength = 2.5f, Seed = 0.31f,
                    });
                });

            var post = h.Scene.TemporalPostTargetsForTests!;
            Assert.Equal(bloom, post.BloomAllocated);
            if (bloom) Assert.Equal((120, 80), (post.BloomWidth, post.BloomHeight));
            Assert.Equal(distortion, post.DistortAllocated);
            return rgba;
        }

        [GpuFact]
        public void A_beam_over_the_sky_shows_under_temporal()
        {
            byte[] without = SkyFrame(beam: false), with = SkyFrame(beam: true);
            int changed = 0;
            for (int y = 0; y < 40; y++)                          // the top rows are sky only
                for (int x = 0; x < 160; x++)
                {
                    int i = (y * 160 + x) * 4;
                    int d = Math.Max(Math.Abs(with[i] - without[i]), Math.Max(Math.Abs(with[i + 1] - without[i + 1]),
                        Math.Abs(with[i + 2] - without[i + 2])));
                    if (d > 24) changed++;
                }
            Assert.True(changed >= 20, $"only {changed} sky pixels changed under the beam");
        }

        static byte[] SkyFrame(bool beam)
        {
            using var h = new TemporalFixture(160, 96);
            h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            h.Scene.Post.Starfield = false;
            h.Scene.Post.Sky.Enabled = true;
            h.Scene.CameraOverride = new FlyCamera3D
            {
                Position = new Vector3(0f, 1.5f, 0f), Yaw = 0f, Pitch = 0.25f, FieldOfView = MathF.PI / 3f,
                AspectRatio = 160f / 96f, NearPlane = 0.1f, FarPlane = 400f,
            };
            MeshHandle ground = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
            byte[] rgba = Array.Empty<byte>();
            for (int i = 0; i < 6; i++)
                rgba = h.Frame((s, _) =>
                {
                    s.Draw(ground, Matrix4x4.CreateScale(200f, 0.2f, 200f) * Matrix4x4.CreateTranslation(0f, -0.1f, 60f),
                        new Color(0.3f, 0.35f, 0.25f, 1f));
                    if (beam) s.DrawBeam(new Vector3(-2f, 0f, 12f), new Vector3(4f, 30f, 30f), 1.2f, new Color(1f, 0.55f, 0.1f, 1f));
                });
            return rgba;
        }

        /// <summary>A capture from another camera at the display size, inside a resolving frame, never writes the history
        /// or its previous depth, so the main view's next frames come out byte-identical to the same run without it.</summary>
        [GpuFact]
        public void A_capture_from_another_camera_at_the_same_size_leaves_the_main_view_unchanged()
        {
            byte[] plain = CaptureRun(capture: false);
            byte[] captured = CaptureRun(capture: true);
            Assert.True(LumaDeviation(plain) > 2.0, "the frame is not a flat fill");
            int differing = 0;
            for (int i = 0; i < plain.Length; i++)
                if (plain[i] != captured[i]) differing++;
            Assert.True(differing == 0, $"the capture changed {differing} bytes of the main view's later frame");
        }

        // Five frames of a slowly panning view over a box and a ground plane, with a capture from another camera at the
        // same size inside the third when asked.
        static byte[] CaptureRun(bool capture)
        {
            using var h = new TemporalFixture(160, 96);
            h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            h.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            h.Scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            MeshHandle box = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
            var other = new FlyCamera3D
            {
                Position = new Vector3(4f, 3f, 6f), Yaw = 0.6f, Pitch = -0.4f, AspectRatio = 160f / 96f,
            };
            byte[] rgba = Array.Empty<byte>();
            for (int i = 0; i < 5; i++)
            {
                h.Scene.Camera.Target = new Vector3(0.07f * i, 0f, 0f);
                rgba = h.Frame((s, _) =>
                {
                    s.Draw(box, Matrix4x4.CreateScale(200f, 0.2f, 200f) * Matrix4x4.CreateTranslation(0f, -0.6f, 0f),
                        new Color(0.3f, 0.35f, 0.25f, 1f));
                    s.Draw(box, Matrix4x4.CreateScale(2f), new Color(0.9f, 0.6f, 0.3f, 1f));
                });
                if (!capture || i != 2) continue;
                h.Scene.CameraOverride = other;
                h.RenderSecond(160, 96);
                h.Scene.CameraOverride = null;
            }
            return rgba;
        }

        /// <summary>
        /// A capture inside a resolving frame is unjittered and unresolved and runs the internal post chain with bloom,
        /// so its own pixels are not blank, stand upright, and match the same view rendered without temporal
        /// anti-aliasing at the capture's internal size and upscaled the same way.
        /// </summary>
        [GpuFact]
        public void A_capture_inside_a_resolving_frame_looks_like_a_render_without_temporal_at_the_internal_size()
        {
            const int W = 160, H = 96;
            byte[] capture;
            int internalWidth, internalHeight;
            using (var h = new TemporalFixture(W, H))
            {
                MeshHandle box = CaptureScene(h.Scene);
                h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
                h.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
                for (int i = 0; i < 3; i++) h.Frame((s, _) => DrawCaptureScene(s, box));
                h.Scene.CameraOverride = CaptureCamera(W, H);
                capture = h.RenderSecond(W, H);
                Assert.Equal(System.Numerics.Vector2.Zero, h.Scene.CurrentFrameView.JitterPixels);
                Assert.True(h.Scene.BloomAllocated, "the capture's internal chain has no bloom pair");
                (internalWidth, internalHeight) = (h.Scene.RenderTargetWidth, h.Scene.RenderTargetHeight);
            }
            Assert.Equal((107, 64), (internalWidth, internalHeight));

            byte[] reference = Array.Empty<byte>();
            using (var h = new TemporalFixture(W, H))
            {
                MeshHandle box = CaptureScene(h.Scene);
                h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
                h.Scene.Post.RenderScale = RenderScale.FixedInternal;
                (h.Scene.Post.RenderWidth, h.Scene.Post.RenderHeight) = (internalWidth, internalHeight);
                h.Scene.CameraOverride = CaptureCamera(W, H);
                for (int i = 0; i < 3; i++) reference = h.Frame((s, _) => DrawCaptureScene(s, box));
            }

            Assert.True(LumaDeviation(capture) > 2.0, "the capture is a flat fill");
            // The bright box sits above the view axis, so an upright image is brighter in its top half.
            double top = MeanLuma(capture, W, 0, H / 2), bottom = MeanLuma(capture, W, H / 2, H);
            Assert.True(top > bottom + 5.0, $"the capture is not upright: top half luma {top:0.0}, bottom {bottom:0.0}");
            int worst = 0;
            for (int i = 0; i < capture.Length; i++)
                if ((i & 3) != 3) worst = Math.Max(worst, Math.Abs(capture[i] - reference[i]));
            double mean = MeanAbs(capture, reference);
            Assert.True(mean <= 0.25 && worst <= 4,
                $"the capture differs from the non-temporal render: mean abs {mean:0.000}, worst {worst}");
        }

        // Bloom on, and a bright box above a dim one, so the capture camera sees a lit upper half.
        static MeshHandle CaptureScene(Scene3D scene)
        {
            scene.Post.Bloom.Enabled = true;
            scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
            return scene.LoadMesh(MeshPrimitives.Box(1f));
        }

        static void DrawCaptureScene(Scene3D s, MeshHandle box)
        {
            s.Draw(box, Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateTranslation(0f, 2.4f, 8f), new Color(3f, 2.6f, 2f, 1f));
            s.Draw(box, Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateTranslation(1.5f, -0.4f, 8f), new Color(0.2f, 0.25f, 0.3f, 1f));
        }

        static FlyCamera3D CaptureCamera(int width, int height) => new()
        {
            Position = new Vector3(0f, 1f, 0f), Yaw = 0f, Pitch = 0f, AspectRatio = width / (float)height,
            NearPlane = 0.1f, FarPlane = 100f,
        };

        /// <summary>A Solid background frame with nothing opaque still copies the cleared background, not the last
        /// frame's colour: the copy follows a framebuffer change that flushes the model pass's owed clear. The copy also
        /// excludes the transparent drawn after it.</summary>
        [GpuFact]
        public void A_solid_background_frame_with_nothing_opaque_copies_the_cleared_background()
        {
            using var h = new TemporalFixture(96, 64);
            h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            h.Scene.Post.BackgroundColor = new Color(0.2f, 0.4f, 0.6f, 1f);
            Assert.Equal(BackgroundMode.Solid, h.Scene.Post.Background);
            Assert.True(h.Scene.Post.Hdr.Enabled);
            MeshHandle box = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
            h.Frame((s, _) => s.Draw(box, Matrix4x4.CreateScale(200f, 0.2f, 200f), new Color(1f, 0f, 0f, 1f)));
            byte[] rgba = h.Frame((s, _) =>
                s.DrawBeam(new Vector3(-3f, 0.5f, 0f), new Vector3(3f, 0.5f, 0f), 0.6f, Color.White));

            Assert.True(LumaDeviation(rgba) > 1.0, "the beam did not draw, so the copy excluding it proves nothing");
            float[] copy = TemporalTextureIo.Read(h.Device, h.Scene.TemporalPostTargetsForTests!.OpaqueColor);
            int wrong = 0;
            for (int p = 0; p < copy.Length; p += 4)
                if (MathF.Abs(copy[p] - 0.2f) > 2e-3f || MathF.Abs(copy[p + 1] - 0.4f) > 2e-3f
                    || MathF.Abs(copy[p + 2] - 0.6f) > 2e-3f)
                    wrong++;
            Assert.True(wrong == 0, $"{wrong} of {copy.Length / 4} opaque copy pixels are not the cleared background");
        }
    }
}
