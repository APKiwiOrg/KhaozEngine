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

        static (int r, int g, int b) CenterColor(byte[] rgba) => PatchColor(rgba, W / 2, H / 2);

        // Off-centre sampler: the mean colour of a 9x9 patch anywhere in the frame, so a test can read WHERE in the
        // sprite footprint a marker landed instead of only what sits at dead centre. CenterColor is the middle case.
        static (int r, int g, int b) PatchColor(byte[] rgba, int px, int py)
        {
            long sr = 0, sg = 0, sb = 0;
            int n = 0;
            for (int y = Math.Max(py - 4, 0); y <= Math.Min(py + 4, H - 1); y++)
                for (int x = Math.Max(px - 4, 0); x <= Math.Min(px + 4, W - 1); x++)
                {
                    int i = (y * W + x) * 4;
                    sr += rgba[i]; sg += rgba[i + 1]; sb += rgba[i + 2];
                    n++;
                }
            return ((int)(sr / n), (int)(sg / n), (int)(sb / n));
        }

        // Luminance-weighted centroid of the lit pixels. The background is black and the asymmetric sheet's only
        // lit thing is its one-quadrant blob, so this reads the blob's screen position directly, with no assumption
        // about where the sprite's footprint falls.
        static (double x, double y) MarkerCentroid(byte[] rgba)
        {
            double sx = 0, sy = 0, sw = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    double lum = rgba[i] + rgba[i + 1] + rgba[i + 2];
                    if (lum <= 90) continue;   // background and bloom skirt
                    sx += x * lum; sy += y * lum; sw += lum;
                }
            Assert.True(sw > 0, "no lit pixels: the marker blob did not render");
            return (sx / sw, sy / sw);
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

        // One centred sprite on the ASYMMETRIC sheet at an integer frame (blend 0, so exactly one cell shows),
        // with the given UV flips. Same scene setup as RenderOneSprite, no motion sheet.
        static byte[] RenderAsymmetric(float frame, bool flipU, bool flipV)
        {
            Scene3D.TextureHandle atlas = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    (byte[] ap, int aw, int ah) = FlipbookTestSheets.AsymmetricAtlas(Cols, Rows, CellPx);
                    atlas = scene.LoadTexture(ap, aw, ah);
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    scene.ParticleSoftFade = 0f;
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
                        Flipbook = new ParticleFlipbook(atlas, Cols, Rows, Loop: true, FlipU: flipU, FlipV: flipV),
                        FlipbookFrame = frame,
                        Blend = BillboardBlend.Alpha,
                    });
                },
                frames: 2);
        }

        [GpuFact]
        public void Flipbook_flips_mirror_the_cell_on_each_axis()
        {
            // The asymmetric sheet paints its hue into ONE quadrant of the cell, so the lit centroid says exactly
            // where that quadrant landed on screen. FlipU must mirror it horizontally and leave y alone, FlipV must
            // mirror it vertically and leave x alone, and each mirror must be about the sprite centre (the screen
            // centre, since the sprite is centred on the framed origin) rather than an arbitrary shift.
            const float Frame = 5f;
            (double nx, double ny) = MarkerCentroid(RenderAsymmetric(Frame, flipU: false, flipV: false));
            (double ux, double uy) = MarkerCentroid(RenderAsymmetric(Frame, flipU: true, flipV: false));
            (double vx, double vy) = MarkerCentroid(RenderAsymmetric(Frame, flipU: false, flipV: true));

            // The marker is genuinely off-centre, so a mirror is a big, unambiguous move.
            Assert.True(Math.Abs(ux - nx) > 8.0, $"FlipU barely moved the marker in x ({nx:F1} -> {ux:F1})");
            Assert.True(Math.Abs(vy - ny) > 8.0, $"FlipV barely moved the marker in y ({ny:F1} -> {vy:F1})");

            // Each flip is confined to its own axis.
            Assert.True(Math.Abs(uy - ny) < 4.0, $"FlipU must not move y ({ny:F1} -> {uy:F1})");
            Assert.True(Math.Abs(vx - nx) < 4.0, $"FlipV must not move x ({nx:F1} -> {vx:F1})");

            // Mirror, not translation: each pair straddles the sprite centre.
            Assert.True(Math.Abs((nx + ux) / 2.0 - W / 2.0) < 4.0, $"FlipU is not a mirror about the sprite centre ({nx:F1}, {ux:F1})");
            Assert.True(Math.Abs((ny + vy) / 2.0 - H / 2.0) < 4.0, $"FlipV is not a mirror about the sprite centre ({ny:F1}, {vy:F1})");
        }

        [GpuFact]
        public void Flipbook_both_flips_are_the_180_rotation()
        {
            // FlipU and FlipV compose: the marker's x must match the FlipU-only render and its y the FlipV-only
            // render, which is exactly a 180 degree rotation of the unflipped cell.
            const float Frame = 5f;
            (double nx, double ny) = MarkerCentroid(RenderAsymmetric(Frame, flipU: false, flipV: false));
            (double ux, _) = MarkerCentroid(RenderAsymmetric(Frame, flipU: true, flipV: false));
            (_, double vy) = MarkerCentroid(RenderAsymmetric(Frame, flipU: false, flipV: true));
            (double bx, double by) = MarkerCentroid(RenderAsymmetric(Frame, flipU: true, flipV: true));

            Assert.True(Math.Abs(bx - ux) < 4.0, $"both-flip x should equal the FlipU x ({ux:F1} vs {bx:F1})");
            Assert.True(Math.Abs(by - vy) < 4.0, $"both-flip y should equal the FlipV y ({vy:F1} vs {by:F1})");
            // And it is a real 180 rotation, so it differs from the unflipped render on BOTH axes.
            Assert.True(Math.Abs(bx - nx) > 8.0 && Math.Abs(by - ny) > 8.0,
                $"both-flip should move on both axes ({nx:F1},{ny:F1} -> {bx:F1},{by:F1})");
        }

        [GpuFact]
        public void Flipbook_flips_do_not_change_cell_selection()
        {
            // The flips mirror WITHIN a cell. The cell the frame index picks must not move, so the marker still
            // carries frame 10's hue under every flip combination.
            const int Frame = 10;
            foreach ((bool fu, bool fv) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                byte[] px = RenderAsymmetric(Frame, fu, fv);
                (double cx, double cy) = MarkerCentroid(px);
                (int r, int g, int b) = PatchColor(px, (int)Math.Round(cx), (int)Math.Round(cy));
                Assert.True(Frame == ClosestCell(r, g, b),
                    $"FlipU={fu} FlipV={fv} selected cell {ClosestCell(r, g, b)}, expected {Frame}");
            }
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

        // How many lit pixels read as CELL, judged on HUE alone: each pixel is scaled so its brightest channel is
        // 255 before ClosestCell sees it. The normalisation is load-bearing rather than tidiness. A tap that
        // leaves the sheet under a CLAMP-addressed sampler lands on the atlas edge, where the cell's disc is
        // transparent, so it contributes its hue with zero coverage and halves the pixel's brightness. Raw
        // ClosestCell against full-value hues then drifts toward whichever cell happens to sit nearest a dimmed
        // colour, which would make the count below say wrap on a clamped device.
        static int CellPixels(byte[] rgba, int cell)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                if (r + g + b <= 90) continue;   // background and bloom skirt, as MarkerCentroid
                int m = Math.Max(1, Math.Max(r, Math.Max(g, b)));
                if (ClosestCell(r * 255 / m, g * 255 / m, b * 255 / m) == cell) n++;
            }
            return n;
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

            // WHERE THE WARPED TAP LANDS, which the pixel count above cannot see: a moved pixel is a moved pixel
            // under either address mode, so that assertion held while the native Direct3D 11 backend was sampling
            // this scene through a CLAMPED shared sampler and the golden moved by 0.359 (CI run 30963173087).
            //
            // Frame 2.5 blends cell 2 (tap A) with cell 3 (tap B) half and half. The offset sheet pushes tap B a
            // fifth of a cell further along u, so over one lobe of the sprite it walks off the right edge of the
            // sheet. WRAPPED it re-enters at cell 0, and that lobe reads as the 50/50 blend of cells 2 and 0.
            // CLAMPED it holds the sheet's last column, which is cell 3 again, so the lobe stays the plain
            // cross-fade the neutral render already shows and cell 0 is never sampled at all.
            //
            // On this sheet's hue ramp the blend of cells 2 and 0 IS cell 1's own colour, asserted rather than
            // asserted-in-a-comment below, which lets the whole check run through ClosestCell.
            (byte r0, byte g0, byte b0) = FlipbookTestSheets.CellRgb(0, FrameCount);
            (byte r2, byte g2, byte b2) = FlipbookTestSheets.CellRgb(2, FrameCount);
            Assert.Equal(1, ClosestCell((r0 + r2) / 2, (g0 + g2) / 2, (b0 + b2) / 2));

            int wrappedLobe = CellPixels(warped, 1);
            int neutralLobe = CellPixels(neutral, 1);
            Assert.True(neutralLobe < 50,
                $"control failed: the UNWARPED render already has {neutralLobe} pixels reading as the wrapped-to "
                + "blend, so the assertion below cannot mean anything");
            Assert.True(wrappedLobe > 300,
                $"only {wrappedLobe} pixels of the warped sprite read as the blend of cells 2 and 0. The tap that "
                + "leaves the sheet is landing on the clamped edge (cell 3) instead of wrapping to cell 0, which "
                + "is what a clamp-addressed IGpuDevice.LinearSampler does to this scene");
        }

        // A grazing, flat-on-ground quad on the CONTRAST sheet, minified hard along one axis. The camera is
        // orthographic, so a low elevation foreshortens the quad's Z axis by sin(Elevation) uniformly: the sprite
        // stays MinStripPx wide and collapses to a couple of pixels tall. That is what drives the sampled LOD past
        // the level where a cell still owns whole texels, without shrinking the sprite to something unmeasurable.
        const int MinCols = 4, MinRows = 4, MinCellPx = 64;   // a 256x256 sheet, 64-texel cells, 9 mip levels
        const int HotCell = 5;                                // interior cell, so every neighbour is a cold one
        const float MinOrthoSize = 8f;                        // 128px viewport / 8 world units = 16 px per unit
        const float MinHalfSize = 1.5f;                       // 48 px along the uncompressed axis
        const float MinGrazeSin = 1.5f / 48f;                 // ~1.5 px along the compressed one

        static byte[] RenderMinifiedStrip()
        {
            Scene3D.TextureHandle atlas = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    (byte[] ap, int aw, int ah) = FlipbookTestSheets.ContrastAtlas(MinCols, MinRows, MinCellPx, HotCell);
                    atlas = scene.LoadTexture(ap, aw, ah);   // full chain, so the coarse levels really exist
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
                    // Render at the readback size. The default fixed 1600x900 internal target would rasterize the
                    // quad at a completely different pixel footprint and then blit it down, which is exactly the
                    // variable under test.
                    scene.Post.RenderScale = RenderScale.MatchViewport;
                    scene.ParticleSoftFade = 0f;
                    scene.EffectTimeSeconds = 0f;
                    scene.Camera.Target = Vector3.Zero;
                    scene.Camera.Azimuth = 0f;
                    scene.Camera.Elevation = MathF.Asin(MinGrazeSin);
                    scene.Camera.OrthoSize = MinOrthoSize;
                    scene.Camera.Zoom = 1f;
                },
                drawFrame: scene =>
                {
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = Vector3.Zero,
                        Size = MinHalfSize,
                        Color = new Color(1f, 1f, 1f, 1f),
                        Orientation = ParticleOrientation.FlatGround,
                        Flipbook = new ParticleFlipbook(atlas, MinCols, MinRows, Loop: true),
                        FlipbookFrame = HotCell,
                        Blend = BillboardBlend.Alpha,
                    });
                },
                frames: 2);
        }

        // Share of the lit sprite's colour that came from a COLD (green) cell: 0 is the hot cell alone, 1 is pure
        // neighbour. The sheet has no other source of green, so this is cross-cell bleed and nothing else.
        static double NeighbourShare(byte[] rgba, out int lit)
        {
            double r = 0, g = 0;
            lit = 0;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i] + rgba[i + 1] + rgba[i + 2] <= 24) continue;   // background
                r += rgba[i];
                g += rgba[i + 1];
                lit++;
            }
            return g / Math.Max(r + g, 1e-9);
        }

        [GpuFact]
        public void Flipbook_minified_atlas_samples_its_own_cell()
        {
            // The whole reason the fragment shader picks its own LOD. Left to the derivatives, this quad samples a
            // level where its cell is about one texel wide, and the bilinear tap there spends half of the cell's UV
            // range straddling the cells either side of it: the icon dissolves into the sheet around it, which is
            // the shimmer a Windows tester reported on a distant loot orb. Clamped, the coarsest level a tap can
            // reach still gives the cell 4 texels, so the neighbours contribute a fringe instead of a quarter of
            // the picture.
            // Measured on Metal: 8.0% with the clamp, 37.5% with the taps back on plain texture(). The threshold
            // sits between them with room on both sides, so this fails loudly if the clamp is ever simplified away.
            byte[] px = RenderMinifiedStrip();
            double share = NeighbourShare(px, out int lit);

            Assert.True(lit >= 24, $"the minified strip did not rasterize, only {lit} lit pixels");
            Assert.True(share < 0.15, $"neighbouring cells contributed {share:P1} of the sprite over {lit} lit " +
                "pixels, so the atlas taps are running past the clamped LOD");
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
