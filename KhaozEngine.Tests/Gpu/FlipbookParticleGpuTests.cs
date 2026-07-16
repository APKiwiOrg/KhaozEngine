using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Particles;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Behavioural GPU proofs of flipbook particle playback that a coarse RGB golden cannot express: per-frame cell
    /// selection, the cross-fade blend between two cells, the motion-vector warp, and the zero-neutral guarantee
    /// (loading an atlas but not using it leaves procedural sprites byte-identical). All sheets are generated
    /// in-test (asset-free), scenes are deterministic (EffectTimeSeconds 0, fixed seeds), soft fade off so only the
    /// atlas look is under test. Skipped unless KE_GPU_TESTS=1 (needs a Metal device). Joins the serialized HdrGpu
    /// collection so it never creates a Metal device context concurrently with the other GPU-heavy classes.
    /// </summary>
    [Collection("HdrGpu")]
    public sealed class FlipbookParticleGpuTests
    {
        const int W = 128, H = 128;
        const int Cols = 4, Rows = 4, CellPx = 32;
        const int FrameCount = Cols * Rows;

        enum Motion { None, Neutral, Offset }

        // Render one centred, screen-filling flipbook sprite at the given continuous frame, over a black background
        // with the soft fade off. Tint is white so the atlas colour passes through unchanged.
        static byte[] RenderOneSprite(float frame, Motion motion, float motionStrength = 1f)
        {
            Scene3D.TextureHandle atlas = default, mv = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    (byte[] ap, int aw, int ah) = FlipbookTestSheets.Atlas(Cols, Rows, CellPx);
                    atlas = scene.LoadTexture(ap, aw, ah);
                    if (motion != Motion.None)
                    {
                        (byte[] mp, int mw, int mh) = motion == Motion.Neutral
                            ? FlipbookTestSheets.UniformMotion(Cols, Rows, CellPx, 128, 128)
                            : FlipbookTestSheets.UniformMotion(Cols, Rows, CellPx, 200, 128);
                        mv = scene.LoadTexture(mp, mw, mh);
                    }
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    scene.ParticleSoftFade = 0f;   // isolate the atlas look from the depth fade
                    scene.EffectTimeSeconds = 0f;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(1.4f, 1.4f, 1.4f));
                },
                drawFrame: scene =>
                {
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = Vector3.Zero,
                        Size = 0.6f,
                        Color = new Color(1f, 1f, 1f, 1f),
                        Flipbook = new ParticleFlipbook(atlas, Cols, Rows, mv, motionStrength, Loop: true),
                        FlipbookFrame = frame,
                        Blend = BillboardBlend.Alpha,
                    });
                },
                frames: 2);
        }

        static (int r, int g, int b) CenterColor(byte[] rgba)
        {
            long sr = 0, sg = 0, sb = 0;
            int n = 0;
            for (int y = H / 2 - 4; y <= H / 2 + 4; y++)
                for (int x = W / 2 - 4; x <= W / 2 + 4; x++)
                {
                    int i = (y * W + x) * 4;
                    sr += rgba[i]; sg += rgba[i + 1]; sb += rgba[i + 2];
                    n++;
                }
            return ((int)(sr / n), (int)(sg / n), (int)(sb / n));
        }

        static int ClosestCell(int r, int g, int b)
        {
            int best = 0;
            long bestD = long.MaxValue;
            for (int k = 0; k < FrameCount; k++)
            {
                (byte cr, byte cg, byte cb) = FlipbookTestSheets.CellRgb(k, FrameCount);
                long d = (long)(r - cr) * (r - cr) + (long)(g - cg) * (g - cg) + (long)(b - cb) * (b - cb);
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }

        static long Dist2(int r, int g, int b, int cell)
        {
            (byte cr, byte cg, byte cb) = FlipbookTestSheets.CellRgb(cell, FrameCount);
            return (long)(r - cr) * (r - cr) + (long)(g - cg) * (g - cg) + (long)(b - cb) * (b - cb);
        }

        [GpuFact]
        public void Flipbook_frame_selection_picks_cells()
        {
            // Integer frames (blend 0) show exactly their cell, so the rendered dominant colour must be nearest to
            // that cell's authored colour among all 16 cells.
            (int r0, int g0, int b0) = CenterColor(RenderOneSprite(0f, Motion.Neutral));
            Assert.Equal(0, ClosestCell(r0, g0, b0));

            (int r10, int g10, int b10) = CenterColor(RenderOneSprite(10f, Motion.Neutral));
            Assert.Equal(10, ClosestCell(r10, g10, b10));
        }

        [GpuFact]
        public void Flipbook_blend_crossfades()
        {
            // Frame 2.5 with a neutral motion sheet is a plain cross-fade: the centre must read as a mix of cells 2
            // and 3, closer to their average than to either pure cell colour.
            (int r, int g, int b) = CenterColor(RenderOneSprite(2.5f, Motion.Neutral));

            (byte r2, byte g2, byte b2) = FlipbookTestSheets.CellRgb(2, FrameCount);
            (byte r3, byte g3, byte b3) = FlipbookTestSheets.CellRgb(3, FrameCount);
            int ar = (r2 + r3) / 2, ag = (g2 + g3) / 2, ab = (b2 + b3) / 2;
            long dAvg = (long)(r - ar) * (r - ar) + (long)(g - ag) * (g - ag) + (long)(b - ab) * (b - ab);

            Assert.True(dAvg < Dist2(r, g, b, 2), "frame 2.5 should not read as pure cell 2");
            Assert.True(dAvg < Dist2(r, g, b, 3), "frame 2.5 should not read as pure cell 3");
        }

        [GpuFact]
        public void Flipbook_motion_vectors_warp()
        {
            // Same frame 2.5, neutral vs offset-encoding motion sheet: the offset warps the atlas taps, so a
            // measurable number of pixels must differ between the two renders.
            byte[] neutral = RenderOneSprite(2.5f, Motion.Neutral, motionStrength: 1f);
            byte[] warped = RenderOneSprite(2.5f, Motion.Offset, motionStrength: 1f);

            int differing = 0;
            for (int i = 0; i < neutral.Length; i += 4)
            {
                if (Math.Abs(neutral[i] - warped[i]) > 20 ||
                    Math.Abs(neutral[i + 1] - warped[i + 1]) > 20 ||
                    Math.Abs(neutral[i + 2] - warped[i + 2]) > 20)
                {
                    differing++;
                }
            }
            Assert.True(differing > 100, $"expected the motion warp to move many pixels, only {differing} differed");
        }

        [GpuFact]
        public void Flipbook_zero_neutral_byte_identical()
        {
            // A procedural-only scene must render byte-identically whether or not an (unused) atlas is loaded: the
            // dummy-texture path keeps procedural sprites out of the flipbook branch.
            byte[] withoutAtlas = RenderProcedural(loadUnusedAtlas: false);
            byte[] withAtlas = RenderProcedural(loadUnusedAtlas: true);
            Assert.Equal(withoutAtlas, withAtlas);
        }

        static byte[] RenderProcedural(bool loadUnusedAtlas)
        {
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    if (loadUnusedAtlas)
                    {
                        (byte[] ap, int aw, int ah) = FlipbookTestSheets.Atlas(Cols, Rows, CellPx);
                        scene.LoadTexture(ap, aw, ah);   // loaded, never referenced by any sprite
                    }
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    scene.ParticleSoftFade = 0f;
                    scene.EffectTimeSeconds = 0f;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(3f, 3f, 3f));
                },
                drawFrame: scene =>
                {
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = new Vector3(-0.5f, 0f, 0f),
                        Size = 0.7f,
                        Color = new Color(1f, 0.6f, 0.25f, 0.95f),
                        Shape = ParticleShape.SoftGlow,
                        ShapeParam = 0.35f,
                        Seed = 0.137f,
                        Blend = BillboardBlend.Additive,
                    });
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = new Vector3(0.6f, 0.1f, 0.2f),
                        Size = 0.5f,
                        Color = new Color(0.5f, 0.8f, 1f, 0.9f),
                        Shape = ParticleShape.Ember,
                        ShapeParam = 0.5f,
                        Seed = 0.61f,
                        Blend = BillboardBlend.Alpha,
                    });
                },
                frames: 2);
        }

        [GpuFact]
        public void Showcase_flipbook_playback()
        {
            const int SW = 640, SH = 360;
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, SW, SH);
            preview.Scene.Post.Starfield = false;
            preview.Scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.08f, 1f);
            preview.Scene.Camera.Frame(new Vector3(0f, 1.1f, 0f), new Vector3(9f, 3f, 5f));
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(24f, 0.1f));

            (byte[] ap, int aw, int ah) = FlipbookTestSheets.Atlas(Cols, Rows, CellPx);
            Scene3D.TextureHandle atlas = preview.Scene.LoadTexture(ap, aw, ah);
            (byte[] mp, int mw, int mh) = FlipbookTestSheets.UniformMotion(Cols, Rows, CellPx, 168, 128);
            Scene3D.TextureHandle mv = preview.Scene.LoadTexture(mp, mw, mh);

            // Left cluster loops on a TimeLoop sheet (frame driven by EffectTimeSeconds), right cluster plays a
            // one-shot burst (frame driven by particle age). Both go through the full adapter timing path.
            var loopSys = new ParticleSystem(64, seed: 5);
            loopSys.Emit(EmitCfg(life: 4f), new Vector3(-3.2f, 1.3f, 0f), 10);
            var burstSys = new ParticleSystem(64, seed: 9);
            burstSys.Emit(EmitCfg(life: 1.2f), new Vector3(3.2f, 1.3f, 0f), 10);

            var loopLook = new ParticleLook
            {
                Blend = BillboardBlend.Alpha,
                Flipbook = new ParticleFlipbook(atlas, Cols, Rows, mv, MotionStrength: 1f, Loop: true),
                FlipbookMode = ParticleFlipbookMode.TimeLoop,
                FlipbookFps = 12f,
            };
            var burstLook = new ParticleLook
            {
                Blend = BillboardBlend.Additive,
                Flipbook = new ParticleFlipbook(atlas, Cols, Rows, Loop: false),
                FlipbookMode = ParticleFlipbookMode.LifeOneShot,
            };

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            float now = 0f;
            foreach (float t in new[] { 0f, 0.6f, 1.2f })
            {
                const float dt = 1f / 60f;
                while (now < t - 1e-4f) { loopSys.Update(dt); burstSys.Update(dt); now += dt; }
                preview.Scene.EffectTimeSeconds = t;
                byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s =>
                {
                    s.Draw(floor, Matrix4x4.Identity, new Color(0.10f, 0.10f, 0.13f, 1f));
                    s.DrawParticles(loopSys, in loopLook);
                    s.DrawParticles(burstSys, in burstLook);
                }).Handle, SW, SH);

                string png = Path.Combine(dir, $"flipbook_playback_t{(int)(t * 100):D3}.png");
                PngWriter.Save(png, px, SW, SH);
                Assert.True(new FileInfo(png).Length > 0, $"empty png at {png}");
            }
        }

        static EmitterConfig EmitCfg(float life) => new()
        {
            LifetimeMin = life, LifetimeMax = life,
            SpeedMin = 0.4f, SpeedMax = 0.9f,
            Direction = Vector3.UnitY, SpreadDegrees = 35f,
            StartSize = 0.7f, EndSize = 0.5f,
            StartColor = new Color(1f, 0.95f, 0.9f, 1f),
            EndColor = new Color(1f, 0.9f, 0.8f, 0.6f),
        };
    }
}
