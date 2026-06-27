using System;
using System.Numerics;
using Xunit;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Gated GPU image-regression net. Renders a FIXED asymmetric 3D scene and a FIXED 2D scene to CPU RGBA via
    /// the headless snapshot helpers, downsamples to a coarse grid, and compares to committed reference grids
    /// with a per-channel tolerance. Catches shader/UBO/blend/winding/orientation regressions that a headless
    /// geometry test cannot. Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    /// </summary>
    public sealed class GoldenSnapshotTests
    {
        const int W = 480, H = 320;

        // Bundled libre font (copied next to the test assembly), so the 2D golden's glyph input is identical on
        // macOS / Windows / Linux runners. A hard-coded OS system-font path would only exist on one platform.
        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        [GpuFact]
        public void Golden3D_FixedAsymmetricScene()
        {
            MeshHandle floor = default, sphere = default, box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(6f, 0.1f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.6f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    // Fixed framing of an asymmetric region so an orientation flip moves content visibly.
                    scene.Camera.Frame(new Vector3(0.4f, 0.4f, -0.2f), new Vector3(5f, 3f, 5f));
                },
                drawFrame: scene =>
                {
                    // Tile floor under everything.
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    // Red shiny sphere off to one side and raised.
                    scene.Draw(sphere,
                        Matrix4x4.CreateTranslation(-1.4f, 0.6f, 0.9f),
                        new Color(0.85f, 0.12f, 0.12f, 1f),
                        Material.Shiny(0.8f));
                    // Green matte box on the other side, distinct position.
                    scene.Draw(box,
                        Matrix4x4.CreateTranslation(1.3f, 0.45f, -1.1f),
                        new Color(0.15f, 0.75f, 0.2f, 1f));
                    // Debug ring on the ground, off-centre so it breaks symmetry.
                    scene.DebugCircle(new Vector3(0.2f, 0.02f, 1.6f), Vector3.UnitY, 1.1f,
                        new Color(0.9f, 0.85f, 0.2f, 1f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_FilledOverlay()
        {
            MeshHandle floor = default, box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(6f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    // Same fixed asymmetric framing as the line golden so a flip moves content visibly.
                    scene.Camera.Frame(new Vector3(0.4f, 0.4f, -0.2f), new Vector3(5f, 3f, 5f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    // An opaque box the fill must NOT wash out where it doesn't overlap.
                    scene.Draw(box, Matrix4x4.CreateTranslation(1.3f, 0.45f, -1.1f),
                        new Color(0.15f, 0.75f, 0.2f, 1f));

                    // Translucent green ground tile: alpha < 1 so the floor reads THROUGH it (blend assertion).
                    scene.DebugFilledQuad(new Vector3(-1.0f, 0.045f, 0.6f), halfSize: 1.2f,
                        new Color(0.30f, 0.85f, 0.45f, 0.45f));
                    // Translucent magenta ground disc off to the other side.
                    scene.DebugFilledCircle(new Vector3(1.4f, 0.045f, 1.4f), Vector3.UnitY, 1.0f,
                        new Color(0.85f, 0.25f, 0.8f, 0.45f), segments: 40);
                    // A crisp outline ON TOP of the filled tile: locks draw order (fill under line).
                    scene.DebugCircle(new Vector3(-1.0f, 0.05f, 0.6f), Vector3.UnitY, 1.0f,
                        new Color(1f, 1f, 0.3f, 1f), segments: 40);
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_fill", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_GroundDecals()
        {
            MeshHandle floor = default, box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(8f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    scene.Camera.Frame(new Vector3(0.4f, 0.4f, -0.2f), new Vector3(6f, 4f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    // A box the cone decal must be occluded by where it overlaps.
                    scene.Draw(box, Matrix4x4.CreateTranslation(1.3f, 0.45f, -1.1f),
                        new Color(0.15f, 0.75f, 0.2f, 1f));

                    // Red filled circle, partway through its sweep (fill smaller than the outline ring).
                    scene.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Circle, Center = new Vector3(-1.2f, 0.0f, 0.6f),
                        Size = new Vector4(1.4f, 0, 0, 0),
                        FillColor = new Color(0.95f, 0.15f, 0.1f, 0.55f),
                        OutlineColor = new Color(1f, 0.8f, 0.2f, 0.9f),
                        EdgeThickness = 0.08f, FillFraction = 0.7f, FlashAdd = 0f,
                        Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
                    });
                    // Cyan ring (annulus) off to the other side.
                    scene.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Ring, Center = new Vector3(1.6f, 0.0f, 1.6f),
                        Size = new Vector4(0.7f, 1.3f, 0, 0),
                        FillColor = new Color(0.2f, 0.8f, 0.9f, 0.55f),
                        OutlineColor = new Color(0.7f, 1f, 1f, 0.9f),
                        EdgeThickness = 0.08f, FillFraction = 1f, FlashAdd = 0f,
                        Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
                    });
                    // Orange cone facing +X, running under the box (occlusion check on an oriented shape).
                    scene.DrawGroundDecal(new GroundDecal
                    {
                        Shape = DecalShape.Cone, Center = new Vector3(0.2f, 0.0f, -1.6f),
                        Rotation = 0f, Size = new Vector4(2.2f, 0.5f, 0, 0),
                        FillColor = new Color(0.9f, 0.5f, 0.1f, 0.55f),
                        OutlineColor = new Color(1f, 0.85f, 0.3f, 0.9f),
                        EdgeThickness = 0.08f, FillFraction = 1f, FlashAdd = 0f,
                        Blend = DecalBlend.Alpha, YTolerance = 0.3f, MaxStep = 0.4f,
                    });
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("telegraph_ground", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_TexturedMesh()
        {
            // Deterministic 64x64 checkerboard (8x8 cells) in two contrasting colours.
            const int TexN = 64, Cell = 8;
            var checker = new byte[TexN * TexN * 4];
            for (int y = 0; y < TexN; y++)
                for (int x = 0; x < TexN; x++)
                {
                    bool a = ((x / Cell) + (y / Cell)) % 2 == 0;
                    int i = (y * TexN + x) * 4;
                    checker[i + 0] = (byte)(a ? 235 : 30);
                    checker[i + 1] = (byte)(a ? 70 : 200);
                    checker[i + 2] = (byte)(a ? 40 : 220);
                    checker[i + 3] = 255;
                }

            MeshHandle plane = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    // Texture API: a valid handle textures the mesh; an invalid/default handle falls back to
                    // untextured without throwing. Both asserted inline (Scene3D needs a device, so this rides the
                    // gated golden rather than a separate headless test).
                    Scene3D.TextureHandle tex = scene.LoadTexture(checker, TexN, TexN);
                    Assert.True(tex.IsValid);
                    Assert.False(Scene3D.TextureHandle.Invalid.IsValid);

                    // Invalid handle into LoadMesh => untextured fallback, no throw.
                    MeshHandle fallback = scene.LoadMesh(MeshPrimitives.Box(0.2f), Scene3D.TextureHandle.Invalid);
                    Assert.NotEqual(default, fallback);

                    plane = scene.LoadMesh(MeshPrimitives.Plane(3f, 3f), tex);
                    // Fixed top-ish framing so the checker fills the view deterministically.
                    scene.Camera.Frame(new Vector3(0f, 0f, 0f), new Vector3(2.6f, 4.2f, 2.6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(plane, Matrix4x4.Identity);
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_textured", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_TexturedBillboard_DepthInterleaved()
        {
            // A 2-cell horizontal "sprite sheet": left cell magenta, right cell cyan. Each cell is solid so a
            // source-UV sub-rect selects one frame's colour cleanly.
            var sheet = new byte[2 * 1 * 4];
            sheet[0] = 230; sheet[1] = 30; sheet[2] = 215; sheet[3] = 255;  // left cell: magenta
            sheet[4] = 30; sheet[5] = 215; sheet[6] = 230; sheet[7] = 255;  // right cell: cyan
            var leftCell = new Vector4(0f, 0f, 0.5f, 1f);
            var rightCell = new Vector4(0.5f, 0f, 1f, 1f);

            MeshHandle box = default;
            Scene3D.TextureHandle tex = default;
            Vector3 fwd = default;   // the camera's (fixed iso) view direction; depth runs along it

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                    tex = scene.LoadTexture(sheet, 2, 1);
                    scene.Post.Starfield = false;   // keep the background flat so occlusion reads clearly
                    scene.Camera.Frame(Vector3.Zero, new Vector3(4.5f, 4.5f, 4.5f));
                    fwd = scene.Camera.Forward;
                },
                drawFrame: scene =>
                {
                    // Opaque green box at the origin.
                    scene.Draw(box, Matrix4x4.Identity, new Color(0.15f, 0.7f, 0.2f, 1f));
                    // CYAN billboard BEHIND the box (+forward, away from the eye), large so it pokes out around the
                    // box's silhouette: the box must occlude its centre, the corners stay visible.
                    scene.DrawBillboard(tex, fwd * 2.2f, 1.5f, rightCell, Color.White);
                    // MAGENTA billboard IN FRONT of the box (-forward, toward the eye): it must draw over the box.
                    scene.DrawBillboard(tex, -fwd * 2.2f, 0.7f, leftCell, Color.White);
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_texbillboard", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_Beam_DepthInterleaved()
        {
            MeshHandle box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    scene.Post.Starfield = false;   // flat background so the occlusion + glow read clearly
                    scene.Camera.Frame(Vector3.Zero, new Vector3(4.5f, 4.5f, 4.5f));
                    scene.EffectTimeSeconds = 0f;   // static frame => deterministic golden (no pulse/scroll)
                },
                drawFrame: scene =>
                {
                    // Opaque green box at the origin.
                    scene.Draw(box, Matrix4x4.Identity, new Color(0.15f, 0.7f, 0.2f, 1f));
                    // A bright magenta beam straight through the box (left -> right): the box occludes the centre,
                    // the glowing tapered ends poke out either side - locks the depth-interleave AND the additive glow.
                    scene.DrawBeam(new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), 0.5f,
                        new Color(1f, 0.2f, 0.9f, 1f),
                        BeamStyle.Default with { Taper = 0.15f });
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_beam", rgba, W, H);
        }

        [GpuFact]
        public void Golden2D_FixedScene()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.07f, 0.08f, 0.11f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                SpriteFont font = ctx.LoadFont(FontPath, 48f);
                ctx.Batch.Begin();
                ctx.Batch.Draw(white, new Vector4(40, 40, 180, 90), new Color(0.85f, 0.2f, 0.2f, 1f));
                ctx.Batch.Draw(white, new Vector4(260, 150, 150, 120), new Color(0.2f, 0.7f, 0.3f, 1f));
                ctx.Batch.Draw(white, new Vector4(120, 220, 110, 70), new Color(0.25f, 0.4f, 0.9f, 0.9f));
                ctx.Batch.DrawString(font, "KE", new Vector2(60, 200), new Color(0.95f, 0.95f, 0.4f, 1f));
                ctx.Batch.End();
            });

            GoldenCompare.AssertOrUpdate("scene2d", rgba, W, H);
        }

        [GpuFact]
        public void Golden2D_Primitives()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.06f, 0.07f, 0.10f, 1f), ctx =>
            {
                // NOT 'using': prim owns a 1x1 white texture that the batch's recorded draws reference. Capture
                // submits the command list AFTER this callback returns, so disposing prim here use-after-frees the
                // texture. Vulkan rejects that at submit (ResourceRefCount.Increment on a disposed resource);
                // Metal/D3D11 silently tolerate it. Left to process teardown, like Capture leaks the device.
                var prim = new PrimitiveRenderer(ctx);
                ctx.Batch.Begin();

                // Filled rect + outline rect on top of it.
                prim.DrawFilledRect(ctx.Batch, new KhaozEngine.Windowing.Rect(30, 30, 130, 80), new Color(0.20f, 0.45f, 0.85f, 1f));
                prim.DrawRect(ctx.Batch, new KhaozEngine.Windowing.Rect(30, 30, 130, 80), new Color(0.95f, 0.95f, 0.95f, 1f), 3f);

                // A couple of diagonal lines (rotated quads).
                prim.DrawLine(ctx.Batch, new Vector2(40, 130), new Vector2(180, 210), new Color(0.95f, 0.35f, 0.2f, 1f), 4f);
                prim.DrawLine(ctx.Batch, new Vector2(40, 210), new Vector2(180, 130), new Color(0.2f, 0.9f, 0.4f, 1f), 4f);

                // Circle outline + ring, distinct radii.
                prim.DrawCircle(ctx.Batch, new Vector2(280, 80), 45f, new Color(0.9f, 0.8f, 0.2f, 1f), segments: 40, thickness: 2f);
                prim.DrawRing(ctx.Batch, new Vector2(400, 80), 50f, 6f, new Color(0.85f, 0.3f, 0.85f, 1f));

                // Filled circle.
                prim.DrawFilledCircle(ctx.Batch, new Vector2(280, 200), 42f, new Color(0.3f, 0.7f, 0.9f, 1f));

                // Vertical gradient panel.
                prim.DrawVerticalGradient(ctx.Batch, new KhaozEngine.Windowing.Rect(360, 150, 90, 110),
                    new Color(0.9f, 0.9f, 0.95f, 1f), new Color(0.15f, 0.1f, 0.3f, 1f), bands: 16);

                // Progress bar near the bottom.
                prim.DrawProgressBar(ctx.Batch, new KhaozEngine.Windowing.Rect(40, 280, 400, 24), 0.62f,
                    new Color(0.2f, 0.8f, 0.35f, 1f), new Color(0.15f, 0.15f, 0.18f, 1f),
                    new Color(0.8f, 0.8f, 0.85f, 1f), 2f);

                ctx.Batch.End();
            });

            GoldenCompare.AssertOrUpdate("scene2d_primitives", rgba, W, H);
        }

        [GpuFact]
        public void Golden2D_Modern()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.10f, 0.11f, 0.14f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var atlas = KhaozEngine.Gui.IconAtlas.Bake(ctx, cell: 64);
                atlas.TryGet(KhaozEngine.Gui.Icons.Coin, out Texture2D atlasTex, out System.Numerics.Vector4 coinUv);
                atlas.TryGet(KhaozEngine.Gui.Icons.Heart, out _, out System.Numerics.Vector4 heartUv);
                atlas.TryGet(KhaozEngine.Gui.Icons.Gear, out _, out System.Numerics.Vector4 gearUv);

                var style = KhaozEngine.Gui.GuiStyle.Modern;
                ctx.Batch.Begin();

                // Soft drop shadow (expanded), then a rounded vertical-gradient panel on top.
                float ss = style.ShadowSize;
                ctx.Batch.DrawRounded(white,
                    new System.Numerics.Vector4(40 - ss * 0.5f, 50 - ss * 0.5f + 4f, 220 + ss, 130 + ss),
                    (Color)style.ShadowColor, style.CornerRadius + ss * 0.5f, softness: ss);
                ctx.Batch.DrawRounded(white, new System.Numerics.Vector4(40, 50, 220, 130),
                    new System.Numerics.Vector4(0, 0, 1, 1),
                    (Color)new System.Numerics.Vector4(0.30f, 0.55f, 0.95f, 1f),
                    (Color)new System.Numerics.Vector4(0.10f, 0.20f, 0.45f, 1f),
                    style.CornerRadius);
                // Rounded border ring.
                ctx.Batch.DrawRounded(white, new System.Numerics.Vector4(40, 50, 220, 130),
                    (Color)new System.Numerics.Vector4(0.6f, 0.8f, 1f, 1f), style.CornerRadius, 0f, 3f);

                // Tinted icons across the top-right.
                ctx.Batch.Draw(atlasTex, new System.Numerics.Vector4(290, 50, 48, 48), coinUv, new Color(0.95f, 0.8f, 0.2f, 1f));
                ctx.Batch.Draw(atlasTex, new System.Numerics.Vector4(290, 110, 48, 48), heartUv, new Color(0.9f, 0.25f, 0.3f, 1f));
                ctx.Batch.Draw(atlasTex, new System.Numerics.Vector4(290, 170, 48, 48), gearUv, new Color(0.8f, 0.85f, 0.9f, 1f));

                // Bottom-anchored rounded gradient bar: occupies the lower frame so a transform/viewport
                // regression that shifts content vertically moves a downsampled cell.
                ctx.Batch.DrawRounded(white, new System.Numerics.Vector4(40, 268, 400, 34),
                    new System.Numerics.Vector4(0, 0, 1, 1),
                    (Color)new System.Numerics.Vector4(0.25f, 0.75f, 0.40f, 1f),
                    (Color)new System.Numerics.Vector4(0.10f, 0.35f, 0.18f, 1f),
                    8f);

                ctx.Batch.End();
            });

            GoldenCompare.AssertOrUpdate("scene2d_modern", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_NormalRoughness()
        {
            const int TexN = 64;
            // Normal map: tangent-space normal tilts toward +x as u increases (a smooth diffuse gradient under
            // the fixed key light). Roughness map: 0 at u=0 (smooth, full spec) -> 1 at u=1 (matte).
            var normalPx = new byte[TexN * TexN * 4];
            var roughPx = new byte[TexN * TexN * 4];
            for (int y = 0; y < TexN; y++)
                for (int x = 0; x < TexN; x++)
                {
                    float u = (x + 0.5f) / TexN;
                    float tiltX = (u - 0.5f) * 1.4f;                 // -0.7 .. +0.7
                    float nz = MathF.Sqrt(MathF.Max(0f, 1f - tiltX * tiltX));
                    int i = (y * TexN + x) * 4;
                    normalPx[i + 0] = (byte)(System.Math.Clamp((tiltX * 0.5f + 0.5f) * 255f, 0f, 255f));
                    normalPx[i + 1] = 128;                            // no tilt along bitangent
                    normalPx[i + 2] = (byte)(System.Math.Clamp((nz * 0.5f + 0.5f) * 255f, 0f, 255f));
                    normalPx[i + 3] = 255;
                    byte rough = (byte)System.Math.Clamp(u * 255f, 0f, 255f);
                    roughPx[i + 0] = rough; roughPx[i + 1] = rough; roughPx[i + 2] = rough; roughPx[i + 3] = 255;
                }

            MeshHandle quad = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    // A flat XZ quad (normal +Y supplied), UVs mapping +X->+U, +Z->+V, built via MeshAssembler
                    // so it carries a real tangent (along +X). Large (spans [-4,4]) so it fills the frame: the
                    // normal-tilt + roughness gradient then reads across most of the grid, not a thin diamond.
                    Vector3 A = new(-4f, 0, -4f), B = new(4f, 0, -4f), C = new(4f, 0, 4f), D = new(-4f, 0, 4f);
                    Vector3 up = Vector3.UnitY;
                    var corners = new System.Collections.Generic.List<MeshCorner>
                    {
                        new(A, up, Vector4.One, new Vector2(0, 0)), new(B, up, Vector4.One, new Vector2(1, 0)), new(C, up, Vector4.One, new Vector2(1, 1)),
                        new(A, up, Vector4.One, new Vector2(0, 0)), new(C, up, Vector4.One, new Vector2(1, 1)), new(D, up, Vector4.One, new Vector2(0, 1)),
                    };
                    GltfMesh mesh = MeshAssembler.Build(corners);

                    Scene3D.TextureHandle nrm = scene.LoadTexture(normalPx, TexN, TexN);
                    Scene3D.TextureHandle rgh = scene.LoadTexture(roughPx, TexN, TexN);
                    quad = scene.LoadMesh(mesh, new Scene3D.SurfaceMaps(default, nrm, rgh));

                    scene.Post.UseSmoothPreset();   // smooth look so the normal/roughness gradient reads cleanly
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2.4f, 3.2f, 2.4f));
                },
                drawFrame: scene =>
                {
                    // Light grey, shiny: the spec highlight is visible on the smooth (low-u) side and fades to
                    // matte on the rough (high-u) side.
                    scene.Draw(quad, Matrix4x4.Identity, new Color(0.75f, 0.76f, 0.8f, 1f), Material.Shiny(0.9f, 48f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_normalmap", rgba, W, H);
        }

        /// <summary>
        /// Skinned PBR-lite (gap E): a bent procedural tube drawn through the CPU-skinning path with a tangent-space
        /// normal map + roughness gradient bound via <see cref="Scene3D.LoadSkinnedMesh(SkinnedGltfMesh,Scene3D.SurfaceMaps)"/>.
        /// The tube carries computed tangents that ride the per-frame skin deform, so the TBN tracks the bent pose
        /// and the normal map perturbs the lit surface (vs the rest-pose albedo-only render). Locks the skinned
        /// normal/roughness shading on Metal; D3D11 + Vulkan follow in CI.
        /// </summary>
        [GpuFact]
        public void Golden3D_SkinnedNormalRoughness()
        {
            const int TexN = 64;
            // Normal map tilts toward +x as u (along the tube) increases; roughness 0 at the base -> 1 at the tip.
            var normalPx = new byte[TexN * TexN * 4];
            var roughPx = new byte[TexN * TexN * 4];
            for (int y = 0; y < TexN; y++)
                for (int x = 0; x < TexN; x++)
                {
                    float u = (x + 0.5f) / TexN;
                    float tiltX = (u - 0.5f) * 1.4f;
                    float nz = MathF.Sqrt(MathF.Max(0f, 1f - tiltX * tiltX));
                    int i = (y * TexN + x) * 4;
                    normalPx[i + 0] = (byte)(System.Math.Clamp((tiltX * 0.5f + 0.5f) * 255f, 0f, 255f));
                    normalPx[i + 1] = 128;
                    normalPx[i + 2] = (byte)(System.Math.Clamp((nz * 0.5f + 0.5f) * 255f, 0f, 255f));
                    normalPx[i + 3] = 255;
                    byte rough = (byte)System.Math.Clamp(u * 255f, 0f, 255f);
                    roughPx[i + 0] = rough; roughPx[i + 1] = rough; roughPx[i + 2] = rough; roughPx[i + 3] = 255;
                }

            // A tube along Z (carries computed tangents), and a fixed bent pose so the deformed TBN is exercised.
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 12, 12, 6, Axis.Z);
            var bent = (Matrix4x4[])tube.RestPose.Clone();
            {
                const float perJoint = 0.30f;
                Matrix4x4 accum = Matrix4x4.Identity;
                Vector3 prevRest = tube.RestPose[0].Translation;
                Vector3 tip = prevRest;
                for (int b = 0; b < tube.BoneCount; b++)
                {
                    Vector3 restPos = tube.RestPose[b].Translation;
                    Vector3 seg = Vector3.Transform(restPos - prevRest, accum);
                    tip += seg;
                    accum = Matrix4x4.CreateRotationX(perJoint) * accum;
                    bent[b] = Matrix4x4.CreateTranslation(-restPos) * accum * Matrix4x4.CreateTranslation(tip);
                    prevRest = restPos;
                }
            }

            SkinnedMeshHandle handle = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    Scene3D.TextureHandle nrm = scene.LoadTexture(normalPx, TexN, TexN);
                    Scene3D.TextureHandle rgh = scene.LoadTexture(roughPx, TexN, TexN);
                    handle = scene.LoadSkinnedMesh(tube, new Scene3D.SurfaceMaps(default, nrm, rgh));

                    scene.Post.UseSmoothPreset();
                    // Frame the bent tube: it bows off the Z axis, centred roughly around (0, 0.8, 1.6).
                    scene.Camera.Frame(new Vector3(0, 0.8f, 1.6f), new Vector3(3.6f, 3.4f, 4.6f));
                },
                drawFrame: scene =>
                {
                    scene.DrawSkinned(handle, bent, Matrix4x4.Identity,
                        new Color(0.75f, 0.76f, 0.8f, 1f), Material.Shiny(0.9f, 48f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_skinned_normalmap", rgba, W, H);
        }

        /// <summary>
        /// Hover-glow bloom regression: renders two hovered Modern buttons through the production
        /// <see cref="KhaozEngine.Gui.GuiDraw.HoverGlow"/> + <see cref="KhaozEngine.Gui.GuiDraw.FillStyled"/> path
        /// at two GlowSize values, so the committed grid locks the soft additive halo (peak on the body edge,
        /// fading to zero outward). The pre-fix code drew a hard ~50%-coverage rim hugging the edge; that reads as
        /// a bright outer ring in the cells just outside the body, which this golden pins against.
        /// </summary>
        [GpuFact]
        public void Golden2D_HoverGlow()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.10f, 0.11f, 0.14f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                ctx.Batch.Begin();

                // Default Modern glow (GlowSize 11), top-left.
                var style = KhaozEngine.Gui.GuiStyle.Modern;
                KhaozEngine.Gui.GuiDraw.HoverGlow(ctx.Batch, white, new KhaozEngine.Windowing.Rect(60, 60, 200, 80), style);
                KhaozEngine.Gui.GuiDraw.FillStyled(ctx.Batch, white, new KhaozEngine.Windowing.Rect(60, 60, 200, 80), style, style.Hover, style.Border);

                // Wider glow (GlowSize 22), bottom-right, to capture the falloff at a second value.
                var wide = KhaozEngine.Gui.GuiStyle.Modern; wide.GlowSize = 22f;
                KhaozEngine.Gui.GuiDraw.HoverGlow(ctx.Batch, white, new KhaozEngine.Windowing.Rect(250, 190, 180, 90), wide);
                KhaozEngine.Gui.GuiDraw.FillStyled(ctx.Batch, white, new KhaozEngine.Windowing.Rect(250, 190, 180, 90), wide, wide.Hover, wide.Border);

                ctx.Batch.End();
            });

            GoldenCompare.AssertOrUpdate("gui_button_glow", rgba, W, H);
        }

        // Bug A: outline-on and outline-off must render the SAME vertical orientation. Each fullscreen post pass
        // flips vertically, so the parity of (quantize + outline + blit) used to leak through as an upside-down
        // image when the optional passes toggled. Render a vertically asymmetric scene (a bright emissive sphere
        // high in the world => near the TOP of the frame) both ways and assert the top third is brighter than the
        // bottom third in BOTH (i.e. upright in both).
        [GpuFact]
        public void Golden3D_OutlineToggle_DoesNotFlip()
        {
            float TopMinusBottom(bool outline)
            {
                MeshHandle floor = default, sphere = default;
                byte[] rgba = Render3DSnapshot.Capture(W, H,
                    setup: scene =>
                    {
                        floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                        sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.8f));
                        scene.Post.Outline = outline;
                        scene.Post.Starfield = false;
                        scene.Camera.Frame(new Vector3(0, 0.5f, 0), new Vector3(6f, 5f, 6f));
                    },
                    drawFrame: scene =>
                    {
                        scene.Draw(floor, Matrix4x4.Identity);
                        scene.Draw(sphere, Matrix4x4.CreateTranslation(0, 3f, 0),
                            new Color(1f, 0.1f, 0.1f, 1f), Material.Glowing(new Color(1f, 0.1f, 0.1f, 1f)));
                    },
                    frames: 2);
                // "Redness" = R - G isolates the red sphere (high) from the white floor (~0) and dark bg (~0).
                double top = 0, bot = 0; int nt = 0, nb = 0;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int i = (y * W + x) * 4;
                        double redness = (rgba[i] - rgba[i + 1]) / 255.0;
                        if (y < H / 3) { top += redness; nt++; } else if (y >= 2 * H / 3) { bot += redness; nb++; }
                    }
                return (float)(top / nt - bot / nb);
            }
            // The bright red sphere sits high => the top third is redder than the bottom in BOTH configs (upright).
            Assert.True(TopMinusBottom(true) > 0.02f, "outline-on is upside down");
            Assert.True(TopMinusBottom(false) > 0.02f, "outline-off is upside down (Bug A)");
        }
    }
}
