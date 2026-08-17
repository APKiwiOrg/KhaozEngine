using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// GPU proof of the shadow depth-pass dirty-skip (Scene3D.ShadowPassSkippedLastFrame). The 2048^2 light-space
    /// depth map persists across frames, so an unchanged static shadow scene reuses it and skips the caster draws:
    /// (a) proves a static second frame skips AND renders pixel-identically to the freshly-rendered first frame,
    /// (b) proves a moved caster re-renders (the shadow moves and the frame is NOT skipped), (c) proves a scene with
    /// an animated skinned caster never skips, and (d) proves that when that skinned caster VANISHES the pass
    /// re-renders once and takes its shadow off the reused atlas, then goes back to skipping (issue #23). Driven
    /// through Render3DPreview so the per-frame skip flag can be read after each render. Gated on KE_GPU_TESTS.
    /// </summary>
    public sealed class ShadowDepthDirtySkipGpuTests
    {
        const int W = 256, H = 200;
        // How much darker than the unlit-by-the-sun floor a pixel has to be to count as shadowed, and how close to
        // it a pixel has to come back to count as lit again. Both in 0..255 luma, and the gap between them is the
        // margin that keeps a half-lifted shadow from reading as either. Metal measures the shadowed floor at
        // (123,135,177) against (205,206,218) lit, a drop of 71, and 0 once the caster's shadow is off the atlas.
        const int ShadowDrop = 40;
        const int LitTolerance = 12;
        // How much redder than blue an opaque pixel may be before it counts as the warm-tinted caster rather than
        // the floor (see CasterPixels). Measured on Metal: the caster's body is 23 over, the floor 12 under.
        const int WarmthMargin = 12;
        // How many still-dark floor pixels are tolerated once the caster is gone. Metal measures 0 with the fix and
        // 459 without it, so the bar sits an order of magnitude below the ghost and well above rasteriser noise.
        const int GhostTolerance = 40;
        // How high above the floor the ghost test's caster hangs: out of the camera's view, inside the sun's. See
        // that test for why its own body must not be in the picture.
        const float GhostCasterHeight = 3.4f;

        static void ConfigureShadowScene(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.Outline = true;
            scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.Quality.Shadows.ShadowNearDistance = 5f;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
        }

        static byte[] Read(IGpuDevice gd, Texture2D tex) => GpuReadback.ToRgba(gd, tex.Handle, W, H);

        static long Diff(byte[] a, byte[] b)
        {
            long d = 0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
            return d;
        }

        static int Luma(byte[] px, int p) => (px[p * 4] * 299 + px[p * 4 + 1] * 587 + px[p * 4 + 2] * 114) / 1000;

        static bool Opaque(byte[] px, int p) => px[p * 4 + 3] > 200;

        /// <summary>How many opaque pixels carry the skinned caster's warm tint (measured 23 red over blue on its
        /// body, against 1 UNDER on the deepest shadowed floor). The ghost test asserts this is zero: its caster
        /// hangs above the camera's view, so nothing but the caster's SHADOW is ever in the picture, and every
        /// pixel that changes when it despawns is therefore the atlas rather than the colour pass.</summary>
        static int CasterPixels(byte[] px)
        {
            int n = 0;
            for (int p = 0; p < px.Length / 4; p++)
                if (Opaque(px, p) && px[p * 4] - px[p * 4 + 2] > WarmthMargin) n++;
            return n;
        }

        /// <summary>The floor pixel the caster darkened most against the same view with no caster in it: the
        /// deepest point of the shadow it threw. -1 when no floor pixel was darkened enough to measure, which the
        /// caller reports rather than passing vacuously.</summary>
        static int ProbeShadowPixel(byte[] empty, byte[] cast)
        {
            int best = -1, drop = ShadowDrop;
            for (int p = 0; p < empty.Length / 4; p++)
            {
                if (!Opaque(empty, p) || !Opaque(cast, p)) continue;
                int d = Luma(empty, p) - Luma(cast, p);
                if (d > drop) { drop = d; best = p; }
            }
            return best;
        }

        /// <summary>How many floor pixels are still measurably darker than the caster-free control: the ghost's
        /// area, in pixels. 0 once the vanished caster's shadow has been lifted off the atlas.</summary>
        static int GhostPixels(byte[] empty, byte[] px)
        {
            int n = 0;
            for (int p = 0; p < empty.Length / 4; p++)
                if (Opaque(empty, p) && Luma(empty, p) - Luma(px, p) > ShadowDrop) n++;
            return n;
        }

        /// <summary>Whether the floor at <paramref name="p"/> is back within tolerance of the caster-free control,
        /// which is what "lit again" means for a pixel that was in shadow.</summary>
        static bool Lit(byte[] empty, byte[] px, int p) => Math.Abs(Luma(empty, p) - Luma(px, p)) <= LitTolerance;

        static int DarkPixels(byte[] px)
        {
            // Shadowed floor pixels are darker than the lit floor. Count clearly-dark opaque pixels as a shadow proxy.
            int n = 0;
            for (int p = 0; p < px.Length / 4; p++)
            {
                int r = px[p * 4], g = px[p * 4 + 1], b = px[p * 4 + 2], a = px[p * 4 + 3];
                if (a > 200 && r < 90 && g < 90 && b < 90) n++;
            }
            return n;
        }

        [GpuFact]
        public void Static_second_frame_skips_and_is_pixel_identical()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle tallBox = preview.Scene.LoadMesh(MeshPrimitives.Box(1.4f));

            void DrawStatic(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                s.Draw(tallBox, Matrix4x4.CreateTranslation(-1.2f, 0.7f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            }

            // Frame 1: first shadow frame - must RENDER the depth pass (no prior map to reuse).
            byte[] img1 = Read(gd, preview.Capture(DrawStatic));
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame, "first shadow frame must render the depth pass");
            Assert.True(preview.Scene.LastShadowPassDiagnostics.Active);
            Assert.True(preview.Scene.LastShadowPassDiagnostics.Rendered);
            Assert.False(preview.Scene.LastShadowPassDiagnostics.HadPrevious);
            Assert.False(preview.Scene.LastShadowPassDiagnostics.ResolutionChanged);
            Assert.False(preview.Scene.LastShadowPassDiagnostics.LightMatrixChanged);
            Assert.False(preview.Scene.LastShadowPassDiagnostics.CasterDataChanged);

            // Frame 2: identical static scene - must SKIP (reuse the persistent map) and render identically.
            byte[] img2 = Read(gd, preview.Capture(DrawStatic));
            Assert.True(preview.Scene.ShadowPassSkippedLastFrame,
                "an unchanged static shadow scene must skip the depth pass on the second frame");
            ShadowPassDiagnostics staticDiagnostics = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(staticDiagnostics.Skipped);
            Assert.True(staticDiagnostics.HadPrevious);
            Assert.False(staticDiagnostics.AnySkinnedCaster);
            Assert.False(staticDiagnostics.ResolutionChanged);
            Assert.False(staticDiagnostics.LightMatrixChanged);
            Assert.False(staticDiagnostics.CasterDataChanged);

            Assert.True(DarkPixels(img1) > 150, $"expected a visible shadow on the floor, got {DarkPixels(img1)} dark pixels");
            Assert.Equal(0, Diff(img1, img2));   // reusing the map must be byte-identical to re-rendering it
        }

        [GpuFact]
        public void Moving_caster_rerenders_and_shadow_moves()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.2f));

            byte[] Frame(float x) => Read(gd, preview.Capture(s =>
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                s.Draw(box, Matrix4x4.CreateTranslation(x, 0.6f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            }));

            byte[] a = Frame(-1.4f);                 // first frame: renders
            byte[] b = Frame(1.4f);                  // caster moved: must re-render (dirty)
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame, "a moved caster must re-render the shadow map");
            Assert.True(preview.Scene.LastShadowPassDiagnostics.CasterDataChanged);
            Assert.True(Diff(a, b) > 20000, $"moving the caster must move the shadow (image diff {Diff(a, b)})");

            // A third frame with the caster STILL at 1.4 is now static again, so it skips.
            Frame(1.4f);
            Assert.True(preview.Scene.ShadowPassSkippedLastFrame, "a re-settled static caster must skip again");
        }

        [GpuFact]
        public void Moving_light_rerenders_and_reports_matrix_change()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = preview.Scene.LoadMesh(MeshPrimitives.Box(1.2f));

            void DrawStatic(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.Identity);
                s.Draw(box, Matrix4x4.CreateTranslation(-1.4f, 0.6f, -0.4f), new Color(0.15f, 0.75f, 0.2f, 1f));
            }

            preview.Capture(DrawStatic);
            preview.Scene.Post.LightDirection = new Vector3(-0.35f, -0.8f, -0.45f);
            preview.Capture(DrawStatic);

            ShadowPassDiagnostics diagnostics = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(diagnostics.Rendered);
            Assert.True(diagnostics.LightMatrixChanged);
            Assert.False(diagnostics.CasterDataChanged);
        }

        [GpuFact]
        public void Vanished_skinned_caster_takes_its_shadow_off_the_reused_atlas()
        {
            // Issue #23, through the real pass on one scene and one device. Four frames of the SAME frozen view:
            // the floor alone, the floor plus a skinned caster, then the floor alone again twice. Nothing else
            // moves, so the only reason frame 3 can differ from frame 1 is what the depth pass left on the atlas.
            //
            // The caster hangs above the camera's view and inside the sun's, which is the case the issue names (a
            // character the main pass culls and the shadow pass keeps - see ClassifySkinnedVisibility). It is also
            // what makes the picture provable: its own body is never drawn, so every pixel that changes when it
            // despawns is its SHADOW. Standing it in view instead would put its body, and the outline around that
            // body, on the very floor pixels the probe is looking at, and those light up when it despawns whether
            // or not its shadow does.
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            var limb = new SkinnedLimb(preview.Scene, radius: 0.4f, length: 2.5f, ringSegments: 8, radialSegments: 8,
                boneCount: 5, ChainConfig.Writhe, Axis.Z);
            limb.Update(new Vector3(0f, GhostCasterHeight, 0f), Vector3.UnitZ, Vector3.UnitY, 1.0f);

            void DrawFloor(Scene3D s) => s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
            void DrawFloorAndLimb(Scene3D s)
            {
                DrawFloor(s);
                limb.Draw(s, Matrix4x4.CreateTranslation(0f, GhostCasterHeight, 0f), new Color(0.8f, 0.4f, 0.3f, 1f));
            }

            // Frame 1: the caster-free control. This is what the floor looks like with nothing over it, and it is
            // the picture frames 3 and 4 have to come back to.
            byte[] empty = Read(gd, preview.Capture(DrawFloor));
            // Frame 2: the caster arrives and casts. Presence alone dirties the pass, so this one always rendered.
            byte[] cast = Read(gd, preview.Capture(DrawFloorAndLimb));
            Assert.True(preview.Scene.LastShadowPassDiagnostics.AnySkinnedCaster);
            Assert.Equal(0, CasterPixels(cast));   // the caster's body is out of frame: only its shadow is here

            int probe = ProbeShadowPixel(empty, cast);
            Assert.True(probe >= 0,
                "the skinned caster cast no measurable shadow on the floor, so this test cannot see the ghost at " +
                "all. Check the sun direction and the caster height against the camera framing.");

            // Frame 3: the caster is gone and NOTHING else changed. Before #23 every dirty input read false here,
            // the pass was skipped, and the atlas still held the caster's shadow: the ghost.
            byte[] gone = Read(gd, preview.Capture(DrawFloor));

            // The symptom first: the ground where the shadow was must be lit again, and no measurable patch of the
            // floor may still be darker than the control.
            Assert.True(Lit(empty, gone, probe), $"the vanished caster's shadow is still on the floor at pixel {probe} " +
                $"(luma {Luma(gone, probe)} against {Luma(empty, probe)} with no caster and {Luma(cast, probe)} " +
                "with one). The depth pass reused an atlas the caster is still baked into (#23).");
            Assert.True(GhostPixels(empty, gone) < GhostTolerance,
                $"{GhostPixels(empty, gone)} floor pixels are still darker than the caster-free control after the " +
                "caster vanished, so a shadow of it is still on the atlas (#23).");

            // Then the mechanism, so a future regression says which half moved.
            ShadowPassDiagnostics clearing = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(clearing.Rendered, "the frame a skinned caster vanishes on must re-render the depth pass");
            Assert.True(clearing.SkinnedCastersCleared);
            Assert.False(clearing.AnySkinnedCaster);
            Assert.False(clearing.LightMatrixChanged);
            Assert.False(clearing.CasterDataChanged);   // the floor is the only rigid caster, and it never moved

            // Frame 4: still nothing but the floor. The clearing pass committed "no skinned casters", so this one
            // has nothing left to react to and must go back to reusing the atlas - exactly ONE extra render.
            byte[] settled = Read(gd, preview.Capture(DrawFloor));
            Assert.True(preview.Scene.ShadowPassSkippedLastFrame,
                "clearing a vanished caster must cost one pass, not turn the dirty-skip off for good");
            Assert.False(preview.Scene.LastShadowPassDiagnostics.Rendered);
            Assert.False(preview.Scene.LastShadowPassDiagnostics.SkinnedCastersCleared);
            Assert.True(Lit(empty, settled, probe));
            Assert.Equal(0, Diff(gone, settled));   // the reused atlas must render identically to the pass that made it

            limb.Dispose();
        }

        [GpuFact]
        public void Skinned_caster_never_skips()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;
            using var preview = new Render3DPreview(gd, W, H);
            ConfigureShadowScene(preview.Scene);
            MeshHandle floor = preview.Scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            var limb = new SkinnedLimb(preview.Scene, radius: 0.4f, length: 2.5f, ringSegments: 8, radialSegments: 8,
                boneCount: 5, ChainConfig.Writhe, Axis.Z);

            void DrawWithLimb(Scene3D s)
            {
                s.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                limb.Draw(s, Matrix4x4.CreateTranslation(0f, 0.8f, 0f), new Color(0.8f, 0.4f, 0.3f, 1f));
            }

            limb.Update(new Vector3(0f, 0.8f, 0f), Vector3.UnitZ, Vector3.UnitY, 1.0f);
            preview.Capture(DrawWithLimb);
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame, "a shadow scene with a skinned caster renders the first frame");

            // Even with the bone pose held IDENTICAL, a skinned caster forces a re-render (bone palettes are not hashed).
            preview.Capture(DrawWithLimb);
            Assert.False(preview.Scene.ShadowPassSkippedLastFrame,
                "any skinned caster present must force the shadow depth pass to re-render every frame");
            ShadowPassDiagnostics skinnedDiagnostics = preview.Scene.LastShadowPassDiagnostics;
            Assert.True(skinnedDiagnostics.Rendered);
            Assert.True(skinnedDiagnostics.AnySkinnedCaster);
            Assert.True(skinnedDiagnostics.SkinnedCasterCount > 0);

            limb.Dispose();
        }
    }
}
