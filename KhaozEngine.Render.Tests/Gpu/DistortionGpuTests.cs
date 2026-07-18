using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Behavioural GPU proofs of the screen-space distortion pass that a coarse RGB golden cannot express: a ripple
    /// actually displaces pixels of the scene behind it, geometry occludes the offset field (the depth recipe fades
    /// it to zero), a ripple over open background warps the starfield sitting there (see
    /// docs/design/BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md), the reduced-quality tier still renders, and the
    /// whole feature is zero-neutral (a queued-then-cleared frame is byte-identical to one that never queued
    /// distortion). Scenes are deterministic (EffectTimeSeconds 0, fixed seeds) and asset-free (an in-test
    /// checkerboard albedo gives the high-frequency content a warp needs to show). Skipped unless KE_GPU_TESTS=1
    /// (needs a Metal device). Joins the serialized HdrGpu collection so it never creates a Metal device context
    /// concurrently with the other GPU-heavy classes.
    /// </summary>
    [Collection("HdrGpu")]
    public sealed class DistortionGpuTests
    {
        const int W = 192, H = 192;

        // A fine checkerboard albedo (two contrasting colours) so a screen-space warp bends visible edges. Sampled
        // 0..1 across the tile's top face, so `cells` gives that many checker squares each way.
        static (byte[] px, int w, int h) Checker(int size = 256, int cells = 16)
        {
            int cell = Math.Max(1, size / cells);
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool on = (((x / cell) + (y / cell)) & 1) == 0;
                    int i = (y * size + x) * 4;
                    byte r = on ? (byte)235 : (byte)25;
                    byte g = on ? (byte)225 : (byte)35;
                    byte b = on ? (byte)210 : (byte)90;
                    px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
                }
            return (px, size, size);
        }

        // Render the checkerboard-floor scene, optionally with one distortion sprite and/or an occluding wall between
        // the camera and the sprite. Deterministic (frozen time, fixed camera). Outline off so the compare is about
        // the warp, not edge lines.
        static byte[] Render(bool distort, DistortionShape shape, float strength, DistortionQuality quality,
            bool wall, bool starfield)
        {
            MeshHandle floor = default, wallMesh = default;
            Scene3D.TextureHandle checker = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    (byte[] cp, int cw, int ch) = Checker();
                    checker = scene.LoadTexture(cp, cw, ch);
                    floor = scene.LoadMesh(MeshPrimitives.Tile(16f, 0.1f), checker);
                    wallMesh = scene.LoadMesh(MeshPrimitives.Box(1f));
                    scene.Post.Starfield = starfield;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.03f, 0.04f, 0.07f, 1f);
                    scene.DistortionQuality = quality;
                    scene.ParticleSoftFade = 0.35f;
                    scene.EffectTimeSeconds = 0f;
                    scene.Camera.Frame(new Vector3(0f, 0.6f, 0f), new Vector3(5.5f, 2.6f, 4.2f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity);
                    // A large wall between the camera and the (behind-the-wall) distortion sprite, scaled to fully
                    // cover the sprite's screen footprint, so the depth recipe fades ALL its offsets to zero.
                    if (wall) scene.Draw(wallMesh,
                        Matrix4x4.CreateScale(9f, 6f, 0.4f) * Matrix4x4.CreateTranslation(0f, 1.4f, 0.6f),
                        new Color(0.6f, 0.2f, 0.2f, 1f));
                    if (distort)
                    {
                        scene.DrawDistortion(new DistortionSprite
                        {
                            Position = new Vector3(0f, 1.2f, -1.4f),
                            Size = 1.7f,
                            Shape = shape,
                            ShapeParam = 0.3f,
                            Strength = strength,
                            Seed = 0.31f,
                        });
                    }
                },
                frames: 2);
        }

        // Count pixels whose colour differs by more than `tol` (0..255 per channel) between two same-size frames.
        static int DiffCount(byte[] a, byte[] b, int tol = 10)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (Math.Abs(a[i] - b[i]) > tol || Math.Abs(a[i + 1] - b[i + 1]) > tol || Math.Abs(a[i + 2] - b[i + 2]) > tol)
                    n++;
            return n;
        }

        // Count differing pixels inside a rectangular region [x0,x1) x [y0,y1).
        static int DiffCountRegion(byte[] a, byte[] b, int x0, int y0, int x1, int y1, int tol = 10)
        {
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * W + x) * 4;
                    if (Math.Abs(a[i] - b[i]) > tol || Math.Abs(a[i + 1] - b[i + 1]) > tol || Math.Abs(a[i + 2] - b[i + 2]) > tol)
                        n++;
                }
            return n;
        }

        [GpuFact]
        public void Distortion_ripple_displaces_pixels()
        {
            byte[] plain = Render(distort: false, DistortionShape.Ripple, 0f, DistortionQuality.Full, wall: false, starfield: false);
            byte[] warped = Render(distort: true, DistortionShape.Ripple, 2.5f, DistortionQuality.Full, wall: false, starfield: false);

            // The ripple ring bends the checker under the sprite: many pixels must differ.
            int differing = DiffCount(plain, warped);
            Assert.True(differing > 300, $"expected the ripple to displace many pixels, only {differing} differed");

            // A control region in a top corner, far from the centred sprite (its footprint fades past its radius), is
            // untouched by the warp.
            int corner = DiffCountRegion(plain, warped, 0, 0, W / 5, H / 5);
            Assert.True(corner < 12, $"expected the far corner untouched by the warp, {corner} pixels differed");
        }

        [GpuFact]
        public void Distortion_occluded_by_geometry()
        {
            byte[] plain = Render(distort: false, DistortionShape.Ripple, 2.5f, DistortionQuality.Full, wall: true, starfield: false);
            byte[] occluded = Render(distort: true, DistortionShape.Ripple, 2.5f, DistortionQuality.Full, wall: true, starfield: false);
            // A no-wall control, to prove the same sprite WOULD warp a lot when not occluded.
            byte[] plainNoWall = Render(distort: false, DistortionShape.Ripple, 2.5f, DistortionQuality.Full, wall: false, starfield: false);
            byte[] warpedNoWall = Render(distort: true, DistortionShape.Ripple, 2.5f, DistortionQuality.Full, wall: false, starfield: false);

            int occludedDiff = DiffCount(plain, occluded);
            int openDiff = DiffCount(plainNoWall, warpedNoWall);
            // The wall in front of the sprite fades its offsets to zero (the depth recipe), so the warp all but
            // vanishes: far fewer differing pixels than the same sprite drawn in the open.
            Assert.True(occludedDiff < openDiff / 10, $"occlusion should suppress the warp: occluded {occludedDiff} vs open {openDiff}");
            Assert.True(occludedDiff < 60, $"occluded distortion should barely differ from no-distortion, {occludedDiff} pixels differed");
        }

        [GpuFact]
        public void Distortion_warps_the_starfield()
        {
            // Pre-migration, the starfield was painted in the final blit, AFTER the whole post chain including
            // distortion, from the colour target's alpha marker. The apply pass resamples RGB at a warped UV but
            // keeps each pixel's OWN (unwarped) alpha, so the old blit always repainted the same star pattern at
            // that exact screen pixel regardless of what the warp had done there: the stars were structurally immune
            // to distortion. That mechanism no longer exists. StarfieldRenderer now paints bg.rgb + star (alpha 1)
            // into ColorTex BEFORE the post chain runs (see docs/design/BACKGROUND-PASS-VOID-DECALS-DESIGN-2026-07-17.md),
            // so the stars are ordinary scene content by the time distortion's apply pass resamples ColorTex at an
            // offset UV, exactly as it would for a checkerboard floor. A ripple queued over open background must
            // therefore perturb the stars behind it. This is correct, not a regression: a heat-haze or ripple
            // should distort whatever is behind it, and the old immunity was only an artifact of the stars being
            // pasted on last.
            byte[] plain = RenderStarfieldDistortion(distort: false);
            byte[] warped = RenderStarfieldDistortion(distort: true);

            // The sprite is centred on the camera target, which the isometric camera always projects to the exact
            // viewport centre, so a box around the centre covers its whole footprint (open background all around,
            // no geometry anywhere in this scene).
            int centre = DiffCountRegion(plain, warped, W / 2 - 40, H / 2 - 40, W / 2 + 40, H / 2 + 40);
            Assert.True(centre > 20, $"a ripple over the starfield should perturb it, only {centre} pixels differed");

            // A far corner, well outside the sprite's footprint, stays untouched: proves the perturbation above is
            // localised to the ripple and not some unrelated frame-to-frame difference.
            int farCorner = DiffCountRegion(plain, warped, 0, 0, W / 6, H / 6);
            Assert.True(farCorner < 10, $"the far corner should be untouched by the ripple, {farCorner} pixels differed");
        }

        // A pure starfield background, no geometry at all, with one distortion sprite centred on the camera target
        // (which always projects to the screen centre for this orthographic camera), so its entire footprint sits
        // over background pixels. Isolates "does distortion warp the void" from occlusion, which
        // Distortion_occluded_by_geometry already covers separately.
        static byte[] RenderStarfieldDistortion(bool distort)
        {
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Post.Background = BackgroundMode.Starfield;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.02f, 0.05f, 1f);
                    scene.EffectTimeSeconds = 0f;
                    scene.Camera.Frame(Vector3.Zero, new Vector3(8f, 8f, 8f));
                },
                drawFrame: scene =>
                {
                    if (distort)
                    {
                        scene.DrawDistortion(new DistortionSprite
                        {
                            Position = Vector3.Zero, Size = 2.6f,
                            Shape = DistortionShape.Ripple, ShapeParam = 0.3f, Strength = 2.5f, Seed = 0.31f,
                        });
                    }
                },
                frames: 2);
        }

        [GpuFact]
        public void Distortion_reduced_quality_renders()
        {
            // The Reduced tier (quarter-res field, single heat octave) must render without throwing and still warp.
            byte[] plain = Render(distort: false, DistortionShape.Heat, 2.5f, DistortionQuality.Reduced, wall: false, starfield: false);
            byte[] warped = Render(distort: true, DistortionShape.Heat, 2.5f, DistortionQuality.Reduced, wall: false, starfield: false);
            Assert.True(DiffCount(plain, warped) > 100, "reduced-quality distortion should still warp the scene");
        }

        [GpuFact]
        public void Distortion_zero_neutral_byte_identical()
        {
            // A frame that never queued distortion vs a frame that queued then cleared it (a Begin between): the lazy
            // offset field is allocated then freed, so the second frame renders byte-identically. Proves the
            // allocate/free cycle leaves no residue and the apply pass never runs without a queued sprite.
            byte[] never = RenderZeroNeutral(queueThenClear: false);
            byte[] cleared = RenderZeroNeutral(queueThenClear: true);
            Assert.Equal(never, cleared);
        }

        // Render one no-distortion frame. When queueThenClear is set, a PRIOR frame queues a distortion sprite (so the
        // offset field allocates), then this frame's Begin clears the queue and it renders distortion-free.
        static byte[] RenderZeroNeutral(bool queueThenClear)
        {
            const int SW = 128, SH = 128;
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, SW, SH);
            preview.Scene.Post.Starfield = false;
            preview.Scene.Post.Outline = false;
            preview.Scene.Post.BackgroundColor = new Color(0.03f, 0.04f, 0.07f, 1f);
            preview.Scene.EffectTimeSeconds = 0f;
            preview.Scene.Camera.Frame(new Vector3(0f, 0.6f, 0f), new Vector3(5.5f, 2.6f, 4.2f));
            (byte[] cp, int cw, int ch) = Checker();
            Scene3D.TextureHandle checker = preview.Scene.LoadTexture(cp, cw, ch);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(16f, 0.1f), checker);

            void DrawPlain(Scene3D s) => s.Draw(floor, Matrix4x4.Identity);

            if (queueThenClear)
            {
                // A distortion frame first: this allocates the offset field and runs the apply pass.
                preview.Capture(s =>
                {
                    DrawPlain(s);
                    s.DrawDistortion(new DistortionSprite { Position = new Vector3(0f, 1.2f, -1.4f), Size = 1.7f, Shape = DistortionShape.Ripple, Strength = 2.5f, Seed = 0.31f });
                });
            }

            // The measured frame: no distortion queued (Capture calls Begin, clearing any prior queue), so the field
            // is freed and the apply pass does not run.
            byte[] px = GpuReadback.ToRgba(gd, preview.Capture(DrawPlain).Handle, SW, SH);
            return px;
        }

        [GpuFact]
        public void Showcase_distortion_trio()
        {
            const int SW = 640, SH = 360;
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, SW, SH);
            preview.Scene.Post.Starfield = false;
            preview.Scene.Post.Outline = false;
            preview.Scene.Post.Hdr.Enabled = true;
            preview.Scene.Post.Hdr.Exposure = 1f;
            preview.Scene.Post.Bloom.Enabled = true;
            preview.Scene.Post.Bloom.Threshold = 0.75f;
            preview.Scene.Post.Bloom.Intensity = 0.9f;
            preview.Scene.Post.Bloom.Radius = 6;
            preview.Scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.08f, 1f);
            preview.Scene.Camera.Frame(new Vector3(0f, 1f, 0f), new Vector3(8f, 3f, 5.5f));

            (byte[] cp, int cw, int ch) = Checker();
            Scene3D.TextureHandle checker = preview.Scene.LoadTexture(cp, cw, ch);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(24f, 0.1f), checker);
            MeshHandle sphere = preview.Scene.LoadMesh(MeshPrimitives.Sphere(0.7f));

            string dir = Environment.GetEnvironmentVariable("KE_PNG_DUMP_DIR") ?? Path.GetTempPath();
            Directory.CreateDirectory(dir);

            foreach (float t in new[] { 0f, 0.5f, 1f })
            {
                preview.Scene.EffectTimeSeconds = t;
                byte[] px = GpuReadback.ToRgba(gd, preview.Capture(s =>
                {
                    s.Draw(floor, Matrix4x4.Identity, new Color(0.6f, 0.6f, 0.65f, 1f));
                    // A hot bloomed sphere for the heat haze to shimmer in front of.
                    s.Draw(sphere, Matrix4x4.CreateTranslation(2.6f, 1.1f, 0f), new Color(3.2f, 1.6f, 0.5f, 1f), Material.Glowing(new Color(3.2f, 1.6f, 0.5f, 1f)));

                    // An expanding refractive shockwave ring over the checkerboard (flat on the ground).
                    s.DrawDistortion(new DistortionSprite
                    {
                        Position = new Vector3(-2.6f, 0.1f, 0f), Size = 0.8f + t * 2.4f,
                        Shape = DistortionShape.Ripple, ShapeParam = 0.15f, Strength = 2.2f,
                        Orientation = ParticleOrientation.FlatGround, SoftFadeScale = 0.14f, Seed = 0.2f,
                    });
                    // Heat haze rising in front of the hot sphere.
                    s.DrawDistortion(new DistortionSprite
                    {
                        Position = new Vector3(2.6f, 1.2f, 0f), Size = 1.6f,
                        Shape = DistortionShape.Heat, ShapeParam = 0.5f, Strength = 1.4f, Seed = 0.7f,
                    });
                    // A lens bulge over the checker pattern.
                    s.DrawDistortion(new DistortionSprite
                    {
                        Position = new Vector3(0f, 1.1f, -0.5f), Size = 1.3f,
                        Shape = DistortionShape.Lens, ShapeParam = 0.4f, Strength = 2.0f, Seed = 0.4f,
                    });
                }).Handle, SW, SH);

                string png = Path.Combine(dir, $"distortion_trio_t{(int)(t * 100):D3}.png");
                PngWriter.Save(png, px, SW, SH);
                Assert.True(new FileInfo(png).Length > 0, $"empty png at {png}");
            }
        }
    }
}
