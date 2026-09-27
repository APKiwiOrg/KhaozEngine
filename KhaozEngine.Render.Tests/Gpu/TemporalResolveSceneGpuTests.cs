using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The resolve inside a real frame: the post chain's bloom and distortion run on the display targets after it, and a
    /// transparent model-pass writer over the sky shows, because the background now draws ahead of it
    /// (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md, plan amendment 5). A later render inside the frame
    /// never resolves, and a Solid frame's opaque copy holds the cleared background.
    /// </summary>
    public sealed class TemporalResolveSceneGpuTests
    {
        [GpuFact]
        public void Bloom_and_distortion_run_on_the_display_chain_after_the_resolve()
        {
            byte[] plain = EffectsFrame(effects: false);
            byte[] rgba = EffectsFrame(effects: true);
            Assert.True(LumaDeviation(rgba) > 2.0, "the frame is not a flat fill");
            double changed = MeanAbsDifference(rgba, plain);
            Assert.True(changed > 0.5, $"bloom and the ripple changed nothing after the resolve: mean abs {changed:0.000}");
        }

        [GpuFact]
        public void A_second_render_at_another_size_leaves_the_history_valid()
        {
            using var h = new Harness(240, 160);
            h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            MeshHandle box = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
            void Draw(Scene3D s) => s.Draw(box, Matrix4x4.CreateScale(2f), new Color(0.8f, 0.6f, 0.4f, 1f));

            h.Render(Draw);
            h.RenderSecond(96, 64);   // an offscreen capture inside the same frame, at another size
            Assert.Equal((240, 160), (h.Scene.TemporalHistory.DisplayWidth, h.Scene.TemporalHistory.DisplayHeight));
            h.Render(Draw);

            Assert.True(h.Scene.TemporalHistory.IsValid, $"the capture reset the history: {h.Scene.TemporalHistory.LastReset}");
            Assert.True(h.Scene.LastTemporalDiagnostics.HistoryValid);
            Assert.Equal(1f, h.Scene.TemporalResolveRendererForTests!.LastUniforms.Jitter.W);
        }

        // A bright box, and with effects on bloom and a distortion ripple over it, resolved at Quality.
        static byte[] EffectsFrame(bool effects)
        {
            using var h = new Harness(240, 160);
            h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            h.Scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
            h.Scene.Post.Bloom.Enabled = effects;
            MeshHandle box = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
            byte[] rgba = Array.Empty<byte>();
            for (int i = 0; i < 4; i++)
                rgba = h.Render(s =>
                {
                    s.Draw(box, Matrix4x4.CreateScale(2f), new Color(3f, 2.5f, 1.5f, 1f));
                    if (!effects) return;
                    s.DrawDistortion(new DistortionSprite
                    {
                        Position = new Vector3(0f, 0f, 1.2f), Size = 2.2f, Shape = DistortionShape.Ripple,
                        Strength = 2.5f, Seed = 0.31f,
                    });
                });

            var post = h.Scene.TemporalPostTargetsForTests!;
            Assert.Equal(effects, post.BloomAllocated);
            if (effects)
            {
                Assert.Equal((120, 80), (post.BloomWidth, post.BloomHeight));
                Assert.True(post.DistortAllocated);
            }
            return rgba;
        }

        static double MeanAbsDifference(byte[] a, byte[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
                if ((i & 3) != 3) sum += Math.Abs(a[i] - b[i]);
            return sum / (a.Length / 4 * 3);
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
            using var h = new Harness(160, 96);
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
                rgba = h.Render(s =>
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
            using var h = new Harness(160, 96);
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
                rgba = h.Render(s =>
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

        /// <summary>A Solid background frame with nothing opaque still copies the cleared background, not the last
        /// frame's colour: the copy follows a framebuffer change that flushes the model pass's owed clear. The copy also
        /// excludes the transparent drawn after it.</summary>
        [GpuFact]
        public void A_solid_background_frame_with_nothing_opaque_copies_the_cleared_background()
        {
            using var h = new Harness(96, 64);
            h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            h.Scene.Post.BackgroundColor = new Color(0.2f, 0.4f, 0.6f, 1f);
            Assert.Equal(BackgroundMode.Solid, h.Scene.Post.Background);
            Assert.True(h.Scene.Post.Hdr.Enabled);
            MeshHandle box = h.Scene.LoadMesh(MeshPrimitives.Box(1f));
            h.Render(s => s.Draw(box, Matrix4x4.CreateScale(200f, 0.2f, 200f), new Color(1f, 0f, 0f, 1f)));
            byte[] rgba = h.Render(s => s.DrawBeam(new Vector3(-3f, 0.5f, 0f), new Vector3(3f, 0.5f, 0f), 0.6f, Color.White));

            Assert.True(LumaDeviation(rgba) > 1.0, "the beam did not draw, so the copy excluding it proves nothing");
            float[] copy = TemporalTextureIo.Read(h.Device, h.Scene.TemporalPostTargetsForTests!.OpaqueColor);
            int wrong = 0;
            for (int p = 0; p < copy.Length; p += 4)
                if (MathF.Abs(copy[p] - 0.2f) > 2e-3f || MathF.Abs(copy[p + 1] - 0.4f) > 2e-3f
                    || MathF.Abs(copy[p + 2] - 0.6f) > 2e-3f)
                    wrong++;
            Assert.True(wrong == 0, $"{wrong} of {copy.Length / 4} opaque copy pixels are not the cleared background");
        }

        static double LumaDeviation(byte[] rgba)
        {
            double sum = 0, sq = 0;
            int n = rgba.Length / 4;
            for (int p = 0; p < n; p++)
            {
                double l = 0.299 * rgba[p * 4] + 0.587 * rgba[p * 4 + 1] + 0.114 * rgba[p * 4 + 2];
                sum += l;
                sq += l * l;
            }
            double mean = sum / n;
            return Math.Sqrt(Math.Max(0, sq / n - mean * mean));
        }

        sealed class Harness : IDisposable
        {
            readonly GpuDeviceContext _gpu;
            readonly IGpuDevice _gd;
            readonly IGpuTexture _tex;
            readonly IGpuFramebuffer _fb;
            readonly IGpuCommandList _cl;
            readonly int _w, _h;

            public Harness(int width, int height)
            {
                _w = width;
                _h = height;
                _gpu = GpuDeviceContext.CreateHeadless();
                _gd = _gpu.GpuDevice;
                _tex = _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
                    GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                _fb = _gd.Factory.CreateFramebuffer(null, _tex);
                Scene = new Scene3D(_gd, _fb.Outputs);
                Scene.Post.UseSmoothPreset();
                _cl = _gd.Factory.CreateCommandList();
            }

            public Scene3D Scene { get; }

            public IGpuDevice Device => _gd;

            /// <summary>A second render inside the frame the last <see cref="Render"/> began, with no Begin, into a
            /// scratch target of another size, as an offscreen capture makes.</summary>
            public void RenderSecond(int width, int height)
            {
                using IGpuTexture tex = _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width,
                    (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                using IGpuFramebuffer fb = _gd.Factory.CreateFramebuffer(null, tex);
                using (GpuRecording.Open(_gd, _cl, nameof(TemporalResolveSceneGpuTests))) Scene.RenderInternal(_cl, width, height, fb);
                _gd.Submit(_cl);
                _gd.WaitForIdle();
            }

            public byte[] Render(Action<Scene3D> draw)
            {
                Scene.Begin();
                draw(Scene);
                Scene.PrepareFrame();
                using (GpuRecording.Open(_gd, _cl, nameof(TemporalResolveSceneGpuTests))) Scene.RenderInternal(_cl, _w, _h, _fb);
                _gd.Submit(_cl);
                _gd.WaitForIdle();
                return GpuReadback.ToRgba(_gd, _tex, _w, _h);
            }

            public void Dispose()
            {
                Scene.Dispose();
                _cl.Dispose();
                _fb.Dispose();
                _tex.Dispose();
                _gpu.Dispose();
            }
        }
    }
}
