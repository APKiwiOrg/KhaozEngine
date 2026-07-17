using System;
using System.Numerics;
using Xunit;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Telegraphs;

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

        // The FIXED asymmetric scene3d scene, single-sourced so the HDR-default golden (Golden3D_FixedAsymmetricScene)
        // and the legacy opt-out golden (Golden3D_HdrOff_MatchesLegacyChain) render the exact same content and cannot
        // drift apart. extraSetup runs last, after the shared camera/outline/mesh setup, so a test can flip a post knob.
        static byte[] CaptureScene3dScene(Action<Scene3D> extraSetup)
        {
            MeshHandle floor = default, sphere = default, box = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(6f, 0.1f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.6f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
                    // Fixed framing of an asymmetric region so an orientation flip moves content visibly.
                    scene.Camera.Frame(new Vector3(0.4f, 0.4f, -0.2f), new Vector3(5f, 3f, 5f));
                    extraSetup(scene);
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
        }

        [GpuFact]
        public void Golden3D_FixedAsymmetricScene()
        {
            // Renders under the HDR-default chain now (float16 + ACES tonemap). The committed scene3d grids predate HDR
            // and are rebaked by the coordinator, so this is EXPECTED to fail locally until that bake lands.
            byte[] rgba = CaptureScene3dScene(_ => { });
            GoldenCompare.AssertOrUpdate("scene3d", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_HdrOff_MatchesLegacyChain()
        {
            // The exact scene3d scene (shared helper) with the HDR chain opted out. The coordinator pre-seeds
            // scene3d_hdr_off by copying the committed legacy scene3d grids, proving the escape hatch is byte-identical
            // to the pre-HDR output on all three backends. Not baked in this stage.
            byte[] rgba = CaptureScene3dScene(scene => scene.Post.Hdr.Enabled = false);
            GoldenCompare.AssertOrUpdate("scene3d_hdr_off", rgba, W, H);
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
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
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
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
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

        // Pins the modern telegraph rendering path (feathered edges, noise fills, edge energy, element presets)
        // added on top of the legacy GroundDecal pass that Golden3D_GroundDecals already locks. Uses the
        // GroundTelegraphs builders (TelegraphStyle presets resolved at a fixed progress) instead of raw
        // GroundDecal literals, so a preset/resolve regression shows up here even when it is zero-neutral for the
        // legacy decal path above.
        [GpuFact]
        public void Golden3D_TelegraphModern()
        {
            MeshHandle floor = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(20f, 0.1f));
                    // Baked with outline on, pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
                    // Wider framing than Golden3D_GroundDecals: three telegraphs spread across x=-4/0/+4 (frost
                    // ring outer radius 3, arcane cone range 4) need a much larger AABB than that sibling's tight
                    // decal cluster. Frame() fits any AABB exactly (it transforms the real corners through the
                    // view matrix), so passing the true scene extent keeps the same asymmetric-focus idiom while
                    // still showing all three telegraphs in frame.
                    scene.Camera.Frame(new Vector3(0.4f, 0.4f, -0.2f), new Vector3(16f, 4f, 8f));
                    // Frozen time, same as every other golden here: determinism across bakes and backends.
                    scene.EffectTimeSeconds = 0f;
                },
                drawFrame: scene =>
                {
                    // Fire and Arcane are additive-blend presets (TelegraphStyle.Fire / .Arcane). Golden3D_
                    // GroundDecals draws its floor with the default (white) tint. Adding colour on top of white
                    // clips straight back to white, so the additive fills would be invisible here. Use a dark
                    // neutral floor instead, same fix as TelegraphShowcaseGpuTests.
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.12f, 0.12f, 0.15f, 1f));

                    // Frost ring: inner 1.5, outer 3, at x = -4.
                    scene.DrawGroundDecal(GroundTelegraphs.BuildRing(
                        new Vector3(-4f, 0f, 0f), 1.5f, 3f, 0.6f, TelegraphStyle.Frost));
                    // Fire circle: radius 2.5, at x = 0.
                    scene.DrawGroundDecal(GroundTelegraphs.BuildCircle(
                        new Vector3(0f, 0f, 0f), 2.5f, 0.6f, TelegraphStyle.Fire));
                    // Arcane cone: range 4, half angle 0.5, at x = +4, facing +X (outward, away from the other
                    // two telegraphs so it doesn't overlap the fire circle).
                    scene.DrawGroundDecal(GroundTelegraphs.BuildCone(
                        new Vector3(4f, 0f, 0f), new Vector2(1f, 0f), 0.5f, 4f, 0.6f, TelegraphStyle.Arcane));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("telegraph_modern", rgba, W, H);
        }

        // Shadows gap #1, blob tier: three meshes at different heights over a tile floor with ShadowMode.Blob on.
        // Each mesh submits a ShadowBlob at its footprint; the two grounded meshes drop a full soft dark blob, the
        // raised sphere drops a shrunk, lighter one (the height fade). Locks the blob grounding + the height-fade
        // derivation through the real ground-decal projection path. Off-scene (the default) stays byte-stable via the
        // untouched scene3d golden.
        [GpuFact]
        public void Golden3D_ShadowBlob()
        {
            MeshHandle floor = default, box = default, sphere = default, boxHi = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(8f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    boxHi = scene.LoadMesh(MeshPrimitives.Box(0.7f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.6f));
                    scene.Post.Starfield = false;                 // flat ground so the blobs read cleanly
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
                    scene.Camera.Frame(new Vector3(0.2f, 0.3f, 0f), new Vector3(6f, 4.5f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));

                    // Grounded green box (left): full, dark blob right under it.
                    scene.Draw(box, Matrix4x4.CreateTranslation(-1.6f, 0.45f, 0.8f),
                        new Color(0.15f, 0.75f, 0.2f, 1f));
                    scene.AddShadowBlob(new ShadowBlob(new Vector3(-1.6f, 0f, 0.8f), groundY: 0f, radius: 0.9f));

                    // Grounded red box (right): a second full blob at a distinct spot.
                    scene.Draw(boxHi, Matrix4x4.CreateTranslation(1.5f, 0.35f, -1.2f),
                        new Color(0.85f, 0.2f, 0.15f, 1f));
                    scene.AddShadowBlob(new ShadowBlob(new Vector3(1.5f, 0f, -1.2f), groundY: 0f, radius: 0.75f));

                    // Raised sphere (a "jumping" caster): drawn 2 units up, so its blob is shrunk + lighter (fade).
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(0.2f, 2.0f, 1.6f),
                        new Color(0.25f, 0.5f, 0.9f, 1f), Material.Shiny(0.6f));
                    scene.AddShadowBlob(new ShadowBlob(new Vector3(0.2f, 0f, 1.6f), groundY: 0f, radius: 0.8f,
                        strength: 1f, heightAboveGround: 2.0f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_shadow_blob", rgba, W, H);
        }

        // Shadows gap #1, ShadowMap tier: three meshes on a tile floor with ShadowMode.ShadowMap on and the key light
        // angled, so each caster drops a real PCF shadow ONTO the floor AND onto its neighbours (mesh-to-mesh). Locks
        // the depth pass + light-space fit + PCF sampling through the model fragment. Off (the default) stays
        // byte-stable via the untouched scene3d golden (the shadow tail sits at strength 0 and is never tapped).
        [GpuFact]
        public void Golden3D_ShadowMap()
        {
            MeshHandle floor = default, box = default, tallBox = default, sphere = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    tallBox = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.6f));
                    scene.Post.Starfield = false;
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    // A tight focus so the map packs texels onto this small scene (crisp shadows at 480x320).
                    scene.Post.Quality.Shadows.ShadowNearDistance = 5f;
                    // Key light travelling down-and-to-the-right (-x, -y, -z) so shadows fall toward +x/+z, clearly
                    // on one side of each caster.
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    // A tall box casting a long shadow across the floor toward +x.
                    scene.Draw(tallBox, Matrix4x4.CreateTranslation(-1.6f, 0.7f, -0.6f),
                        new Color(0.15f, 0.75f, 0.2f, 1f));
                    // A shorter red box positioned so the tall box's shadow can fall across it (mesh-to-mesh).
                    scene.Draw(box, Matrix4x4.CreateTranslation(0.9f, 0.45f, 0.4f),
                        new Color(0.85f, 0.2f, 0.15f, 1f));
                    // A raised sphere dropping a detached round shadow onto the floor beneath+beyond it.
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(1.6f, 1.1f, -1.4f),
                        new Color(0.25f, 0.5f, 0.9f, 1f), Material.Shiny(0.6f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_shadow_map", rgba, W, H);
        }

        // Rendering gap #4: the opt-in procedural sky (gradient + sun disc/halo) behind meshes WITH shadows on - the
        // cohesive-look pairing the roadmap wants. The sun direction defaults to the key light, so the disc sits where
        // the light comes from (up + toward +x/+z here) and the casters' shadows fall AWAY from it (toward +x/+z),
        // agreeing by construction. Locks the background pass: gradient fills the sky, the disc reads in the upper
        // background, geometry rejects the sky (depth test), and the normal/depth MRT the outline pass reads is
        // untouched. Off (the default) stays byte-stable via the untouched scene3d golden.
        [GpuFact]
        public void Golden3D_Sky()
        {
            MeshHandle floor = default, box = default, tallBox = default, sphere = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(0.9f));
                    tallBox = scene.LoadMesh(MeshPrimitives.Box(1.4f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.6f));
                    scene.Post.Starfield = false;
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
                    // Sky ON: gradient + sun disc. The sun direction defaults to the key light below.
                    scene.Post.Sky.Enabled = true;
                    // Ortho iso camera: pin the STYLIZED backdrop anchor (the world point-at-infinity projection
                    // degenerates under parallel view rays). This keeps the disc placed by view-space azimuth, so the
                    // golden stays byte-identical to the pre-SunAnchor behaviour. The world anchor is exercised by the
                    // perspective Golden3D_SkyWorldSun instead.
                    scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
                    scene.Post.Sky.HorizonColor = new Color(0.66f, 0.72f, 0.80f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.20f, 0.40f, 0.72f, 1f);
                    scene.Post.Sky.SunColor = new Color(1f, 0.95f, 0.82f, 1f);
                    scene.Post.Sky.SunRadius = 0.09f;   // screen-space NDC-y; a touch large so the disc reads at 480x320
                    scene.Post.Sky.HaloStrength = 0.6f;
                    scene.Post.Sky.HaloFalloff = 0.22f;
                    // Shadows ON (the cohesive pairing).
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.Quality.Shadows.ShadowNearDistance = 5f;
                    // Key light travelling down-and-to-the-right (-x, -y, -z): shadows fall toward +x/+z, and the sun
                    // (its opposite) sits up + toward +x/+z, so it lands in the upper background of this framing.
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    // A tall box casting a long shadow across the floor toward +x.
                    scene.Draw(tallBox, Matrix4x4.CreateTranslation(-1.6f, 0.7f, -0.6f),
                        new Color(0.15f, 0.75f, 0.2f, 1f));
                    // A shorter red box.
                    scene.Draw(box, Matrix4x4.CreateTranslation(0.9f, 0.45f, 0.4f),
                        new Color(0.85f, 0.2f, 0.15f, 1f));
                    // A raised sphere dropping a detached round shadow onto the floor.
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(1.6f, 1.1f, -1.4f),
                        new Color(0.25f, 0.5f, 0.9f, 1f), Material.Shiny(0.6f));
                },
                frames: 2);

            // Regression guard: a background-only image (the pre-fix GreaterEqual sky painted over ALL geometry) must
            // never silently re-bake again. The procedural sky is a purely blue-dominant gradient - EVERY sky cell has
            // blue as the strictly largest channel. Counting cells where blue is NOT the dominant channel used to
            // separate the two cleanly at the pre-chroma tonemap default (floor read as achromatic, ~200 foreground
            // cells for a correct render vs ~6 for a background-only one). At the ChromaPreservation 0.75 default the
            // floor no longer reads achromatic: its lit colour is albedo*(Ambient+diffuse), and the ambient term
            // (PixelPostProcessSettings.AmbientColor, blue-leaning at (0.16,0.19,0.30)) now keeps its blue lean
            // through the hue-preserving rescale instead of being bleached toward white by the old per-channel curve.
            // So most of the floor now counts as "blue-dominant" too, and the only reliable signal left is the
            // saturated meshes (green/red boxes, blue sphere rim). Measured on real Metal hardware at the 0.75
            // default: a correct render (floor + three meshes occluding the sky) shows 34 such cells. The broken
            // background-only baseline (same scene, geometry omitted, sky painting over everything) shows 5. Require
            // a floor of 15 - comfortably above the 5-cell broken baseline, comfortably below the 34-cell correct
            // render - so any future flat-sky regression still trips this before the golden compare, at the new,
            // narrower (mesh-only) margin the chroma-preserved floor leaves available.
            // Downsampled on the same 32x18 grid the golden uses; tolerant of backend noise.
            float[] guardGrid = GoldenCompare.Downsample(rgba, W, H);
            int foregroundCells = 0;
            for (int cell = 0; cell < guardGrid.Length / 3; cell++)
            {
                float r = guardGrid[cell * 3], g = guardGrid[cell * 3 + 1], b = guardGrid[cell * 3 + 2];
                if (b <= MathF.Max(r, g) + 0.02f) foregroundCells++;   // not blue-dominant => foreground, not sky
            }
            Assert.True(foregroundCells >= 15,
                $"scene3d_sky has only {foregroundCells} foreground cells (blue not the dominant channel) of " +
                $"{guardGrid.Length / 3}; the sky pass is painting over the scene (background-only image). Expected " +
                "the coloured meshes to occlude the sky (~34 cells at the chroma-preserved floor's reduced margin). " +
                "Check the SkyRenderer depth test is Equal, not GreaterEqual (GreaterEqual passes on every pixel " +
                "under the [0,1]/LessEqual depth convention).");

            GoldenCompare.AssertOrUpdate("scene3d_sky", rgba, W, H);
        }

        // World-anchored sun disc (SunAnchor.World, the default) under a PERSPECTIVE follow camera - the case the
        // world projection is correct for (a directional sun is a point at infinity, finite on-screen only under
        // perspective). The disc is placed by projecting the world sun direction through the camera, so it sits over
        // the world direction the light really comes from and would stay fixed there as the camera orbits (unlike the
        // stylized backdrop, which slides with the view). The guard ties the rendered disc to the CPU projection: the
        // brightest warm pixel must land where SkyMath.ProjectSunWorldToNdc says the sun is, so a wrong projection (or
        // a silent regression back to the camera-relative placement) trips before the golden compare.
        [GpuFact]
        public void Golden3D_SkyWorldSun()
        {
            MeshHandle floor = default, box = default, pillar = default;
            // Off-axis perspective fly camera looking slightly UP (a follow camera pitches down at its target, so a
            // world sun high in the sky lands off the top of frame). Yawed off -Z so a world-anchored disc lands
            // off-centre - proof it tracks the world, not the screen. AspectRatio matches the render so the projection
            // places x correctly.
            var fly = new FlyCamera3D
            {
                Position = new Vector3(0f, 0.8f, 9f),
                Yaw = MathF.PI + 0.35f,   // look toward -Z, rotated ~20 deg so the disc sits off-centre
                Pitch = 0.12f,            // tilt up so the sky (and the sun) fill the upper frame
                AspectRatio = (float)W / H,
            };
            // Sun up and toward the camera front (dot with forward > 0): lands high in the sky, off to one side.
            var light = new Vector3(0.15f, -0.45f, 0.85f);   // travel dir; sun = -normalize(light), up + in front

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(14f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(1.1f));
                    pillar = scene.LoadMesh(MeshPrimitives.Box(1.0f));
                    scene.Post.Starfield = false;
                    scene.Post.Outline = true;
                    scene.CameraOverride = fly;
                    // Sky ON, World anchor (the default, set explicit so the golden pins the behaviour under test).
                    scene.Post.Sky.Enabled = true;
                    scene.Post.Sky.Anchor = SunAnchor.World;
                    scene.Post.Sky.HorizonColor = new Color(0.66f, 0.72f, 0.80f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.20f, 0.40f, 0.72f, 1f);
                    scene.Post.Sky.SunColor = new Color(1f, 0.95f, 0.82f, 1f);
                    scene.Post.Sky.SunRadius = 0.08f;
                    scene.Post.Sky.HaloStrength = 0.6f;
                    scene.Post.Sky.HaloFalloff = 0.22f;
                    scene.Post.LightDirection = light;
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                    scene.Draw(box, Matrix4x4.CreateTranslation(-1.4f, 0.55f, 0.3f), new Color(0.2f, 0.55f, 0.85f, 1f));
                    scene.Draw(pillar, Matrix4x4.CreateTranslation(1.5f, 0.5f, -0.8f), new Color(0.85f, 0.35f, 0.2f, 1f));
                },
                frames: 2);

            // World-anchoring lock: the disc must render where the CPU point-at-infinity projection places it. Project
            // the same world sun direction the sky used, convert NDC (y up) -> top-origin pixel, and require the local
            // image there to be bright + warm (the sun disc / SunColor), while a control patch of open sky elsewhere is
            // NOT sun-bright. A regression to camera-relative placement moves the disc off this predicted spot.
            var sun = SkyMath.SunDirectionFromLight(light);
            Assert.True(SkyMath.ProjectSunWorldToNdc(fly.View, fly.Projection, sun, out Vector2 sunNdc),
                "the world sun is up and in front, so it must project on-screen for this golden");
            Assert.True(MathF.Abs(sunNdc.X) < 0.95f && MathF.Abs(sunNdc.Y) < 0.95f,
                $"sun should be comfortably on-screen for a stable golden, got {sunNdc}");
            int sunPx = (int)((sunNdc.X * 0.5f + 0.5f) * W);
            int sunPy = (int)((0.5f - sunNdc.Y * 0.5f) * H);   // top-origin: ndc.y up -> pixel y down
            (float r, float g, float b) discAvg = AveragePatch(rgba, W, H, sunPx, sunPy, 6);
            Assert.True(discAvg.r > 0.75f && discAvg.g > 0.7f && discAvg.r >= discAvg.b,
                $"world-projected sun pixel ({sunPx},{sunPy}) should be a bright warm disc, got rgb({discAvg.r:0.##},{discAvg.g:0.##},{discAvg.b:0.##})");
            // Control: a patch on the FAR horizontal side of the sky (upper region, well away from the disc + halo) is
            // plain blue-dominant gradient, not another sun. Pick the edge opposite the disc so the halo can't reach it.
            int ctlPx = sunPx > W / 2 ? W / 10 : W - W / 10;
            (float r, float g, float b) ctlAvg = AveragePatch(rgba, W, H, ctlPx, sunPy, 6);
            Assert.True(ctlAvg.b >= ctlAvg.r,
                $"control sky patch ({ctlPx},{sunPy}) should be blue-dominant gradient, not a second sun, got rgb({ctlAvg.r:0.##},{ctlAvg.g:0.##},{ctlAvg.b:0.##})");

            GoldenCompare.AssertOrUpdate("scene3d_sky_world_sun", rgba, W, H);
        }

        /// <summary>Average RGB (0..1) over a (2*half+1) square patch of the RGBA8 buffer centred at (cx,cy), clamped
        /// to the image. Used by the world-sun golden to sample the projected disc vs a control sky patch.</summary>
        static (float r, float g, float b) AveragePatch(byte[] rgba, int w, int h, int cx, int cy, int half)
        {
            double r = 0, g = 0, b = 0; int n = 0;
            for (int y = Math.Max(0, cy - half); y <= Math.Min(h - 1, cy + half); y++)
                for (int x = Math.Max(0, cx - half); x <= Math.Min(w - 1, cx + half); x++)
                {
                    int i = (y * w + x) * 4;
                    r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
                }
            return n == 0 ? (0f, 0f, 0f) : ((float)(r / n / 255.0), (float)(g / n / 255.0), (float)(b / n / 255.0));
        }

        // Rendering gap #5: the animated water surface (normal perturbation, sky-derived fresnel tint, key-light sun
        // glint, depth-sampled shore fade). A deep lakebed tile sits well below the water surface (so the open-water
        // region is fully opaque - depth-below-surface far past ShoreFadeDistance) while a shallow shelf ramps up
        // near one side to a dry "beach" box that pokes ABOVE the surface (the water pass's own depth test occludes
        // it), so the shelf's slope crosses the fade band and the frame shows both open water (glint + fresnel tint)
        // and a genuine soft shoreline. Time is FROZEN (EffectTimeSeconds = 0, the same mechanism scene3d_beam locks)
        // so the golden is deterministic despite the animated per-pixel wave math. Off (the default, no DrawWater
        // call) stays byte-stable via the untouched scene3d golden - this is a pure ADDITIVE opt-in pass.
        [GpuFact]
        public void Golden3D_Water()
        {
            MeshHandle lakebed = default, shelf = default, beach = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    lakebed = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                    shelf = scene.LoadMesh(MeshPrimitives.Tile(4f, 0.1f));
                    beach = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    scene.Post.Starfield = false;
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    // Sky ON so the water's fresnel horizon tint has the same cohesive sky-derived look the brief
                    // asks for (water borrows the sky's palette by default; harmonized in WaterSettings' own
                    // defaults too, so this also exercises an explicit override agreeing with a custom sky).
                    scene.Post.Sky.Enabled = true;
                    // Ortho iso camera: pin the stylized backdrop anchor (the world projection degenerates under
                    // parallel view rays) so this golden stays byte-identical to the pre-SunAnchor behaviour.
                    scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
                    scene.Post.Sky.HorizonColor = new Color(0.66f, 0.72f, 0.80f, 1f);
                    scene.Post.Sky.ZenithColor = new Color(0.20f, 0.40f, 0.72f, 1f);
                    scene.Post.Sky.SunRadius = 0.09f;
                    scene.Post.Sky.HaloStrength = 0.6f;
                    // Key light angled so the sun sits up-and-toward-camera - lands a specular glint on the water
                    // AND agrees with the sky's sun disc (both derive from the same LightDirection).
                    scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);
                    scene.Post.Water.DeepColor = new Color(0.04f, 0.16f, 0.26f, 0.92f);
                    scene.Post.Water.HorizonColor = new Color(0.60f, 0.70f, 0.80f, 0.75f);
                    scene.Post.Water.WaveScale = 0.9f;         // tight enough that several ripple crests fit across the plane
                    scene.Post.Water.WaveSpeed = 0.4f;
                    scene.Post.Water.NormalStrength = 0.8f;    // strong enough that the ripple shading reads clearly at 480x320
                    scene.Post.Water.ShoreFadeDistance = 0.7f;
                    scene.Post.Water.GlintStrength = 0.8f;
                    scene.Post.Water.GlintExponent = 100f;
                    scene.Camera.Frame(new Vector3(0.1f, 0.3f, 0.2f), new Vector3(6f, 5f, 6f));
                    scene.EffectTimeSeconds = 0f;   // static frame => deterministic golden (no wave scroll)
                },
                drawFrame: scene =>
                {
                    // Deep lakebed at y in [0, 0.1]: with the surface at y=1.0, depth-below-surface is ~0.9 in open
                    // water - well past ShoreFadeDistance (0.7), so the open-water region is fully opaque.
                    scene.Draw(lakebed, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.20f, 0.24f, 0.20f, 1f));
                    // Shallow shelf at y in [0.55, 0.65], offset toward +X/-Z: depth-below-surface ~0.35-0.45, inside
                    // the fade band, so its footprint reads as a soft shoreline gradient rather than a hard clip.
                    scene.Draw(shelf, Matrix4x4.CreateTranslation(2.4f, 0.55f, -2.4f), new Color(0.55f, 0.48f, 0.32f, 1f));
                    // A dry beach box whose top (y=1.4) pokes ABOVE the water surface (y=1.0): the water pass's own
                    // depth test occludes it, locking the "geometry above the surface occludes water" invariant.
                    scene.Draw(beach, Matrix4x4.CreateTranslation(3.6f, 0.6f, -3.6f), new Color(0.62f, 0.56f, 0.38f, 1f));
                    scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 1.0f, centerZ: 0f, halfExtentX: 4.5f));
                },
                frames: 2);

            // Anti-degeneracy guard: the water region must show real per-cell colour variation (animated-normal
            // shading + fresnel gradient + glint), not a flat single-colour sheet (the failure mode a broken bake -
            // e.g. the fragment falling through to a constant tint with no lighting - would produce). Sample the
            // grid cells that fall within the water plane's world footprint by re-deriving their approximate screen
            // position is overkill for a coarse guard; instead require a healthy SPREAD of distinct blue-ish
            // (water-dominant) cell brightnesses across the whole downsampled grid, which only happens when the
            // fresnel/glint/normal terms actually vary per pixel.
            float[] guardGrid = GoldenCompare.Downsample(rgba, W, H);
            int waterCells = 0;
            float minBrightness = float.MaxValue, maxBrightness = float.MinValue;
            for (int cell = 0; cell < guardGrid.Length / 3; cell++)
            {
                float r = guardGrid[cell * 3], g = guardGrid[cell * 3 + 1], b = guardGrid[cell * 3 + 2];
                // Water-ish: blue at least as strong as red (deep tint + horizon tint are both blue-led; the dirt-
                // brown ground/rock are red-led), and not near-black background.
                if (b >= r - 0.02f && MathF.Max(r, MathF.Max(g, b)) > 0.05f)
                {
                    waterCells++;
                    float brightness = (r + g + b) / 3f;
                    minBrightness = MathF.Min(minBrightness, brightness);
                    maxBrightness = MathF.Max(maxBrightness, brightness);
                }
            }
            Assert.True(waterCells >= 40,
                $"scene3d_water has only {waterCells} blue-dominant (water-ish) cells (of {guardGrid.Length / 3}); " +
                "expected a sizeable visible water region. Check the DrawWater plane/camera framing.");
            Assert.True(maxBrightness - minBrightness >= 0.08f,
                $"scene3d_water's blue-dominant cells only span brightness {minBrightness:F3}..{maxBrightness:F3} " +
                "(range < 0.08); a real animated water surface should show meaningful cell-to-cell variation from " +
                "the fresnel gradient + sun glint + shore fade, not a flat single-colour sheet. Check the fragment " +
                "is actually computing the normal/fresnel/glint terms instead of falling back to a constant tint.");

            GoldenCompare.AssertOrUpdate("scene3d_water", rgba, W, H);
        }

        // Shadows gap #1: a SPLAT-TERRAIN ground quad RECEIVING a model's PCF shadow (the shadow term flows through
        // the shared lighting block into the splat fragment identically to the model fragment). A box casts; the
        // terrain receives (model-only casting - terrain does not self-shadow). Locks terrain-receives.
        [GpuFact]
        public void Golden3D_SplatShadow()
        {
            MeshHandle terrain = default, box = default, sphere = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    // A large flat splat-terrain quad on the ground plane; vertex Color carries all-grass weights.
                    var mat = scene.LoadSplatMaterial(8, 8, FiveSolidLayers(8));
                    var wgt = new Vector4(1f, 0f, 0f, 0f);
                    const float e = 8f;
                    var verts = new[]
                    {
                        new ModelVertex(new Vector3(-e, 0, -e), Vector3.UnitY, wgt, new Vector2(0, 0)),
                        new ModelVertex(new Vector3( e, 0, -e), Vector3.UnitY, wgt, new Vector2(1, 0)),
                        new ModelVertex(new Vector3( e, 0,  e), Vector3.UnitY, wgt, new Vector2(1, 1)),
                        new ModelVertex(new Vector3(-e, 0,  e), Vector3.UnitY, wgt, new Vector2(0, 1)),
                    };
                    terrain = scene.LoadMesh(new GltfMesh(verts, new ushort[] { 0, 1, 2, 0, 2, 3 }), mat);
                    box = scene.LoadMesh(MeshPrimitives.Box(1.1f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.55f));
                    scene.Post.Starfield = false;
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
                    scene.Post.Quality.Shadows.ShadowNearDistance = 5f;
                    scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
                    scene.Camera.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(terrain, Matrix4x4.Identity, Color.White);
                    // A green box casting a shadow onto the terrain toward +x/+z.
                    scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f, 0.55f, -0.4f),
                        new Color(0.2f, 0.7f, 0.25f, 1f));
                    // A raised sphere dropping a round shadow onto the terrain.
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(1.4f, 1.0f, 1.0f),
                        new Color(0.85f, 0.35f, 0.2f, 1f), Material.Shiny(0.5f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_splat_shadow", rgba, W, H);
        }

        // Five solid-colour splat layers (grass/dirt/rock/sand/snow) for the splat-shadow golden's ground material.
        static System.Collections.Generic.List<SplatLayerImage> FiveSolidLayers(int size)
        {
            var layers = new System.Collections.Generic.List<SplatLayerImage>();
            byte[][] colors =
            {
                new byte[] { 60, 110, 40, 255 },   // grass
                new byte[] { 90, 75, 55, 255 },    // dirt
                new byte[] { 110, 105, 100, 255 }, // rock
                new byte[] { 190, 175, 125, 255 }, // sand
                new byte[] { 235, 238, 245, 255 }, // snow
            };
            foreach (var c in colors)
            {
                var albedo = new byte[size * size * 4];
                var normal = new byte[size * size * 4];
                for (int p = 0; p < albedo.Length; p += 4)
                {
                    albedo[p] = c[0]; albedo[p + 1] = c[1]; albedo[p + 2] = c[2]; albedo[p + 3] = 255;
                    normal[p] = 128; normal[p + 1] = 128; normal[p + 2] = 255; normal[p + 3] = 255; // flat
                }
                layers.Add(new SplatLayerImage { AlbedoRgba = albedo, NormalRgba = normal, TilesPerMetre = 0.25f, Roughness = 0.8f });
            }
            return layers;
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
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
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
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
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

        // Rendering gap #2: overlapping alpha-blended billboards must composite back-to-front regardless of the
        // order the host queued them. Three big translucent colour billboards straddle the screen centre at
        // different view depths and are submitted FRONT-TO-BACK (near first) - the worst case for the old
        // submission-order code, which would blend the near one under the far ones where they overlap. With the
        // back-to-front sort the far billboard composites behind the mid, and the mid behind the near, so the
        // central overlap reads as the near billboard's colour tinted by the ones behind it, not scrambled.
        [GpuFact]
        public void Golden3D_AlphaOverlap()
        {
            Vector3 fwd = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Post.Starfield = false;   // flat background so the composite reads cleanly
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
                    scene.Post.BackgroundColor = new Color(0.06f, 0.07f, 0.10f, 1f);
                    scene.Camera.Frame(Vector3.Zero, new Vector3(4.5f, 4.5f, 4.5f));
                    fwd = scene.Camera.Forward;
                },
                drawFrame: scene =>
                {
                    // All three overlap around the screen centre (small lateral offsets) but sit at distinct depths
                    // along the view axis. Queue NEAR first, FAR last: submission order is the reverse of the correct
                    // back-to-front draw order, so a broken (unsorted) path composites them wrong.
                    // NEAR: red, toward the eye (-fwd).
                    scene.DrawBillboard(-fwd * 2.4f + new Vector3(0.3f, 0.2f, 0f), 1.7f,
                        new Color(0.95f, 0.15f, 0.15f, 0.6f));
                    // MID: green, at the centre.
                    scene.DrawBillboard(new Vector3(-0.2f, -0.1f, 0f), 1.7f,
                        new Color(0.15f, 0.9f, 0.2f, 0.6f));
                    // FAR: blue, away from the eye (+fwd).
                    scene.DrawBillboard(fwd * 2.4f + new Vector3(0.1f, 0.25f, 0f), 1.7f,
                        new Color(0.2f, 0.35f, 0.95f, 0.6f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_alpha_overlap", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_ParticlesModern()
        {
            MeshHandle floor = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(16f, 0.1f));
                    scene.Post.Starfield = false;   // flat background so the shapes + fade read cleanly
                    scene.Post.Outline = true;      // pinned explicit, matching the other 3D goldens
                    scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
                    scene.Camera.Frame(new Vector3(0f, 0.9f, 0.2f), new Vector3(6.4f, 2.6f, 4.4f));
                    scene.EffectTimeSeconds = 0f;   // frozen time => deterministic noise/flicker terms
                },
                drawFrame: scene =>
                {
                    // Dark floor: gives the additive sprites contrast and the half-sunk glow a surface to
                    // soft-fade against.
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.10f, 0.10f, 0.13f, 1f));

                    // One of each procedural shape across the back row: locks the SDF shaping, the premultiplied
                    // additive compositing, and the per-shape params at their canonical showcase values.
                    ParticleShape[] shapes =
                    {
                        ParticleShape.SoftGlow, ParticleShape.Ember, ParticleShape.Spark,
                        ParticleShape.Wisp, ParticleShape.Ring, ParticleShape.Star,
                    };
                    Color[] tints =
                    {
                        new(1.0f, 0.72f, 0.35f, 0.95f), new(1.0f, 0.45f, 0.15f, 1.0f),
                        new(1.0f, 0.85f, 0.45f, 1.0f), new(0.62f, 0.64f, 0.70f, 0.85f),
                        new(0.55f, 0.85f, 1.0f, 0.95f), new(0.75f, 0.55f, 1.0f, 1.0f),
                    };
                    for (int i = 0; i < shapes.Length; i++)
                    {
                        scene.DrawParticle(new ParticleSprite
                        {
                            Position = new Vector3((i - 2.5f) * 1.55f, 1.5f, -1.6f),
                            Size = 0.62f,
                            Color = tints[i],
                            Shape = shapes[i],
                            ShapeParam = 0.35f,
                            LifeNorm = 0.45f,
                            Seed = 0.137f + 0.61f * i,
                            Blend = i == 3 ? BillboardBlend.Alpha : BillboardBlend.Additive,
                        });
                    }

                    // A velocity-stretched spark: locks the camera-plane stretch path.
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = new Vector3(-2.4f, 1.1f, 1.3f),
                        Velocity = new Vector3(7f, 2f, 0f),
                        Size = 0.4f,
                        Color = new Color(1f, 0.85f, 0.45f, 1f),
                        Shape = ParticleShape.Spark,
                        ShapeParam = 0.5f,
                        LifeNorm = 0.3f,
                        Seed = 0.71f,
                        Stretch = 0.35f,
                        Blend = BillboardBlend.Additive,
                    });

                    // A glow half-sunk into the floor: locks the soft depth fade (its lower half must fade at
                    // the surface, not clip). Queued NEAR-ish last with the alpha wisp above queued earlier, so
                    // a broken back-to-front sort would change the composite.
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = new Vector3(1.9f, 0.08f, 1.5f),
                        Size = 0.85f,
                        Color = new Color(1f, 0.72f, 0.35f, 0.95f),
                        Shape = ParticleShape.SoftGlow,
                        ShapeParam = 0.35f,
                        LifeNorm = 0.3f,
                        Seed = 0.41f,
                        Blend = BillboardBlend.Additive,
                    });
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_particles_modern", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_ParticlesFlipbook()
        {
            MeshHandle floor = default;
            Scene3D.TextureHandle atlas = default, mv = default;
            const int Cols = 4, Rows = 4, CellPx = 32;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(16f, 0.1f));
                    (byte[] ap, int aw, int ah) = FlipbookTestSheets.Atlas(Cols, Rows, CellPx);
                    atlas = scene.LoadTexture(ap, aw, ah);
                    (byte[] mp, int mw, int mh) = FlipbookTestSheets.UniformMotion(Cols, Rows, CellPx, 200, 128);
                    mv = scene.LoadTexture(mp, mw, mh);
                    scene.Post.Starfield = false;
                    scene.Post.Outline = true;      // pinned explicit, matching the other 3D goldens
                    scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
                    scene.Camera.Frame(new Vector3(0f, 0.9f, 0.2f), new Vector3(6.4f, 2.6f, 4.4f));
                    scene.EffectTimeSeconds = 0f;   // frozen time => deterministic
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity, new Color(0.10f, 0.10f, 0.13f, 1f));

                    // A back row of atlas frames: integer cells (0, 5, 10) plus a mid-blend (2.5), tint white so the
                    // sheet hues show. Locks per-frame cell selection + the cross-fade blend.
                    float[] frames = { 0f, 2.5f, 5f, 10f };
                    for (int i = 0; i < frames.Length; i++)
                    {
                        scene.DrawParticle(new ParticleSprite
                        {
                            Position = new Vector3((i - 1.5f) * 1.55f, 1.5f, -1.6f),
                            Size = 0.62f,
                            Color = new Color(1f, 1f, 1f, 0.95f),
                            Flipbook = new ParticleFlipbook(atlas, Cols, Rows, Loop: true),
                            FlipbookFrame = frames[i],
                            Blend = BillboardBlend.Alpha,
                        });
                    }

                    // A motion-vector-warped frame (offset MV sheet, strength 2): locks the two-tap warp path.
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = new Vector3(-2.4f, 1.0f, 1.3f),
                        Size = 0.7f,
                        Color = new Color(1f, 1f, 1f, 1f),
                        Flipbook = new ParticleFlipbook(atlas, Cols, Rows, mv, MotionStrength: 2f, Loop: true),
                        FlipbookFrame = 6.5f,
                        Blend = BillboardBlend.Alpha,
                    });

                    // Procedural sprites interleaved at different depths: the global back-to-front sort splits the
                    // stream into per-atlas runs (procedural = dummy pair), so this locks run-splitting + ordering.
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = new Vector3(1.9f, 1.1f, 1.5f),
                        Size = 0.7f,
                        Color = new Color(1f, 0.72f, 0.35f, 0.95f),
                        Shape = ParticleShape.SoftGlow,
                        ShapeParam = 0.35f,
                        Seed = 0.41f,
                        Blend = BillboardBlend.Additive,
                    });
                    scene.DrawParticle(new ParticleSprite
                    {
                        Position = new Vector3(0.4f, 1.3f, 0.4f),
                        Size = 0.5f,
                        Color = new Color(0.6f, 0.85f, 1f, 1f),
                        Shape = ParticleShape.Ember,
                        ShapeParam = 0.5f,
                        Seed = 0.71f,
                        Blend = BillboardBlend.Additive,
                    });
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_particles_flipbook", rgba, W, H);
        }

        [GpuFact]
        public void Golden3D_Distortion()
        {
            MeshHandle floor = default;
            Scene3D.TextureHandle tex = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    // A colourful grid albedo on the floor gives the screen-space warp visible edges to bend at the
                    // coarse golden-grid scale (a flat floor would average out).
                    (byte[] ap, int aw, int ah) = FlipbookTestSheets.Atlas(8, 8, 24);
                    tex = scene.LoadTexture(ap, aw, ah);
                    floor = scene.LoadMesh(MeshPrimitives.Tile(16f, 0.1f), tex);
                    scene.Post.Starfield = false;
                    scene.Post.Outline = true;      // pinned explicit, matching the other 3D goldens (HDR default on)
                    scene.Post.BackgroundColor = new Color(0.05f, 0.06f, 0.09f, 1f);
                    scene.Camera.Frame(new Vector3(0f, 0.6f, 0.1f), new Vector3(6.0f, 2.6f, 4.2f));
                    scene.EffectTimeSeconds = 0f;   // frozen time => deterministic noise/ring terms
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity);
                    // One of each shape at fixed positions over the textured floor: locks the ripple ring band, the
                    // lens bulge, and the heat wobble through the full offset-field + apply-pass path. Fixed seeds +
                    // frozen time keep every term deterministic.
                    scene.DrawDistortion(new DistortionSprite
                    {
                        Position = new Vector3(-2.2f, 1.1f, -1.2f), Size = 1.3f,
                        Shape = DistortionShape.Ripple, ShapeParam = 0.2f, Strength = 2.2f, Seed = 0.13f,
                    });
                    scene.DrawDistortion(new DistortionSprite
                    {
                        Position = new Vector3(2.0f, 1.0f, -1.0f), Size = 1.3f,
                        Shape = DistortionShape.Lens, ShapeParam = 0.4f, Strength = 2.0f, Seed = 0.41f,
                    });
                    scene.DrawDistortion(new DistortionSprite
                    {
                        Position = new Vector3(0f, 1.2f, 0.4f), Size = 1.4f,
                        Shape = DistortionShape.Heat, ShapeParam = 0.5f, Strength = 1.6f, Seed = 0.7f,
                    });
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_distortion", rgba, W, H);
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
                    // Baked with outline on; pinned explicit when the engine default flipped to off.
                    scene.Post.Outline = true;
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

        // Rendering gap #6: opt-in LDR threshold + separable-blur bloom. A dark scene (starfield off, near-black
        // background + a dim floor) with three motivating bright sources - an emissive sphere (Material.Glowing), a
        // bright magenta beam, and an additive white billboard - so the halo reads clearly against a mostly-dark
        // frame. Off (the default) stays byte-stable via the untouched scene3d/scene3d_beam goldens; this locks
        // bloom ON at its default knobs (Threshold 0.7, Knee 0.15, Intensity 0.6, Radius 4).
        [GpuFact]
        public void Golden3D_Bloom()
        {
            MeshHandle floor = default, sphere = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.55f));
                    scene.Post.Starfield = false;                    // flat dark background - bloom must not wash it
                    scene.Post.BackgroundColor = new Color(0.03f, 0.03f, 0.05f, 1f);
                    // Baked with outline on; pinned explicit when the engine default flipped to off. Outline + bloom
                    // = 2 preceding passes (even), the same flipV=1 parity branch as before the default flip.
                    scene.Post.Outline = true;
                    scene.Post.Bloom.Enabled = true;                 // default knobs otherwise (Threshold/Knee/Intensity/Radius)
                    scene.Camera.Frame(Vector3.Zero, new Vector3(4.5f, 4.5f, 4.5f));
                    scene.EffectTimeSeconds = 0f;                    // static frame => deterministic golden (no beam pulse/scroll)
                },
                drawFrame: scene =>
                {
                    // Dark, non-bright floor: must stay dark under bloom (nothing here crosses the bright-pass
                    // threshold), the anti-wash guard's near-black-cell count leans on this.
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.10f, 0.10f, 0.13f, 1f));
                    // Bright emissive sphere: the motivating "glow instead of flat" case.
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(-1.4f, 0.7f, 0.6f),
                        new Color(1f, 0.85f, 0.3f, 1f), Material.Glowing(new Color(1f, 0.85f, 0.3f, 1f)));
                    // Bright magenta beam off to the other side.
                    scene.DrawBeam(new Vector3(1.6f, 0.2f, -2.2f), new Vector3(1.6f, 0.2f, 2.2f), 0.35f,
                        new Color(1f, 0.2f, 0.9f, 1f), BeamStyle.Default with { Taper = 0.2f });
                    // Bright additive white billboard: a third, distinct bloom source (screen-space, not a mesh).
                    scene.DrawBillboard(new Vector3(0.6f, 1.6f, -0.8f), 0.9f, new Color(1f, 1f, 0.95f, 0.9f));
                },
                frames: 2);

            // Anti-wash guard: bloom must brighten the halo AROUND the bright sources without flooding the whole
            // frame. Downsample to the same 32x18 grid the golden compares against and require a healthy count of
            // still-near-black cells (the dark floor/background far from any bright source) - a broken bake that
            // additively blew out the entire image would collapse this count toward zero, so this trips BEFORE the
            // golden compare silently commits a washed-out bake.
            float[] guardGrid = GoldenCompare.Downsample(rgba, W, H);
            int nearBlackCells = 0;
            for (int cell = 0; cell < guardGrid.Length / 3; cell++)
            {
                float r = guardGrid[cell * 3], g = guardGrid[cell * 3 + 1], b = guardGrid[cell * 3 + 2];
                if (MathF.Max(r, MathF.Max(g, b)) < 0.25f) nearBlackCells++;
            }
            Assert.True(nearBlackCells >= 150,
                $"scene3d_bloom has only {nearBlackCells} near-black cells (of {guardGrid.Length / 3}); bloom is " +
                "washing out the whole frame instead of haloing the bright sources. Expected the dark floor/" +
                "background to dominate the grid (~200+ cells) with only a local halo around the sphere/beam/" +
                "billboard. Check the bright-pass threshold/knee and the additive composite intensity.");

            GoldenCompare.AssertOrUpdate("scene3d_bloom", rgba, W, H);
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
                prim.DrawFilledRect(ctx.Batch, new KhaozEngine.Primitives.Rect(30, 30, 130, 80), new Color(0.20f, 0.45f, 0.85f, 1f));
                prim.DrawRect(ctx.Batch, new KhaozEngine.Primitives.Rect(30, 30, 130, 80), new Color(0.95f, 0.95f, 0.95f, 1f), 3f);

                // A couple of diagonal lines (rotated quads).
                prim.DrawLine(ctx.Batch, new Vector2(40, 130), new Vector2(180, 210), new Color(0.95f, 0.35f, 0.2f, 1f), 4f);
                prim.DrawLine(ctx.Batch, new Vector2(40, 210), new Vector2(180, 130), new Color(0.2f, 0.9f, 0.4f, 1f), 4f);

                // Circle outline + ring, distinct radii.
                prim.DrawCircle(ctx.Batch, new Vector2(280, 80), 45f, new Color(0.9f, 0.8f, 0.2f, 1f), segments: 40, thickness: 2f);
                prim.DrawRing(ctx.Batch, new Vector2(400, 80), 50f, 6f, new Color(0.85f, 0.3f, 0.85f, 1f));

                // Filled circle.
                prim.DrawFilledCircle(ctx.Batch, new Vector2(280, 200), 42f, new Color(0.3f, 0.7f, 0.9f, 1f));

                // Vertical gradient panel.
                prim.DrawVerticalGradient(ctx.Batch, new KhaozEngine.Primitives.Rect(360, 150, 90, 110),
                    new Color(0.9f, 0.9f, 0.95f, 1f), new Color(0.15f, 0.1f, 0.3f, 1f), bands: 16);

                // Progress bar near the bottom.
                prim.DrawProgressBar(ctx.Batch, new KhaozEngine.Primitives.Rect(40, 280, 400, 24), 0.62f,
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
                KhaozEngine.Gui.GuiDraw.HoverGlow(ctx.Batch, white, new KhaozEngine.Primitives.Rect(60, 60, 200, 80), style);
                KhaozEngine.Gui.GuiDraw.FillStyled(ctx.Batch, white, new KhaozEngine.Primitives.Rect(60, 60, 200, 80), style, style.Hover, style.Border);

                // Wider glow (GlowSize 22), bottom-right, to capture the falloff at a second value.
                var wide = KhaozEngine.Gui.GuiStyle.Modern; wide.GlowSize = 22f;
                KhaozEngine.Gui.GuiDraw.HoverGlow(ctx.Batch, white, new KhaozEngine.Primitives.Rect(250, 190, 180, 90), wide);
                KhaozEngine.Gui.GuiDraw.FillStyled(ctx.Batch, white, new KhaozEngine.Primitives.Rect(250, 190, 180, 90), wide, wide.Hover, wide.Border);

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

        // Honest guard for the Bug A parity landmine on the OUTLINE-OFF default path. Renders a vertically
        // asymmetric scene with BARE default settings (outline now off engine-wide => zero preceding post passes,
        // the even-parity flipV=1 blit branch). Asserts (a) the render has real structure (bright sphere vs dark
        // background, not a flat fill) and (b) it is UPRIGHT: a bright red sphere placed high in world space lands
        // in the top half of the image. A default whose pass count flipped the frame would fail (b).
        [GpuFact]
        public void DefaultPost_RendersUprightWithoutOutline()
        {
            MeshHandle floor = default, sphere = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.8f));
                    // No Post settings touched: the bare engine default (outline off) is exactly what we guard.
                    scene.Camera.Frame(new Vector3(0, 0.5f, 0), new Vector3(6f, 5f, 6f));
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.Identity);
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(0, 3f, 0),
                        new Color(1f, 0.1f, 0.1f, 1f), Material.Glowing(new Color(1f, 0.1f, 0.1f, 1f)));
                },
                frames: 2);

            // (a) Structural non-uniformity: the bright sphere vs dark background gives a wide luminance spread,
            // proving the frame actually rendered scene content and is not a uniform clear.
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                double lum = (0.2126 * rgba[i] + 0.7152 * rgba[i + 1] + 0.0722 * rgba[i + 2]) / 255.0;
                if (lum < lo) lo = lum;
                if (lum > hi) hi = lum;
            }
            Assert.True(hi - lo > 0.25, "default render is near-uniform (no scene structure)");

            // (b) Upright: "redness" = R - G isolates the high red sphere from the pale floor and dark bg. The
            // sphere sits high in the world, so the top third must be redder than the bottom third.
            double top = 0, bot = 0; int nt = 0, nb = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    double redness = (rgba[i] - rgba[i + 1]) / 255.0;
                    if (y < H / 3) { top += redness; nt++; } else if (y >= 2 * H / 3) { bot += redness; nb++; }
                }
            Assert.True(top / nt - bot / nb > 0.02, "default render is upside down (Bug A, outline-off path)");
        }

        // Perspective-camera outline: locks the corrected stable outline (Fix C linearized relative depth) AND
        // Bug B's interior-crease normal term under a perspective FollowCamera3D, upright (Bug A) by construction.
        // The pitch is steep enough that the near floor is not at extreme grazing (where a depth edge floods on any
        // plane); the outline reads as silhouettes (floor edges + objects) plus the box's interior creases.
        [GpuFact]
        public void Golden3D_PerspectiveOutline()
        {
            MeshHandle floor = default, box = default, sphere = default;
            var follow = new FollowCamera3D
            {
                Target = new Vector3(0f, 0.5f, 0f),
                Pitch = 0.7f, Yaw = 0.5f, Distance = 9f, HeightOffset = 1.2f,
                AspectRatio = (float)W / H,
            };
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(14f, 0.1f));
                    box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.7f));
                    scene.Post.Starfield = false;
                    scene.Post.BackgroundColor = new Color(0.10f, 0.12f, 0.16f, 1f);
                    scene.Post.Outline = true;
                    scene.Post.OutlineColor = new Color(0.02f, 0.02f, 0.04f, 1f);
                    scene.Post.OutlineDepthThreshold = 0.3f;     // medium (relative Laplacian under perspective)
                    scene.Post.OutlineNormalThreshold = 0.45f;
                    scene.CameraOverride = follow;
                },
                drawFrame: scene =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0, 0, 0));
                    scene.Draw(box, Matrix4x4.CreateTranslation(-1.6f, 0.6f, 0.4f),
                        new Color(0.2f, 0.55f, 0.85f, 1f));
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(1.5f, 0.7f, -0.6f),
                        new Color(0.85f, 0.35f, 0.2f, 1f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("perspective_outline", rgba, W, H);
        }

        // The HDR payoff pin: the float16 chain carries over-range emissive/beam/particle energy into the pre-tonemap
        // bloom, then the ACES curve compresses it back to LDR. HDR is the default, bloom explicitly on. Deterministic
        // (EffectTimeSeconds 0, fixed seeds). Baked by the coordinator (not in this stage).
        [GpuFact]
        public void Golden3D_HdrEmissiveBloom()
        {
            MeshHandle floor = default, sphere = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    floor = scene.LoadMesh(MeshPrimitives.Tile(8f, 0.1f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.7f));
                    scene.Post.Starfield = false;
                    scene.Post.BackgroundColor = new Color(0.02f, 0.03f, 0.05f, 1f);
                    scene.Post.Bloom.Enabled = true;
                    scene.Post.Bloom.Threshold = 1.1f;
                    scene.Post.Bloom.Intensity = 0.7f;
                    scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(6f, 4f, 6f));
                    scene.EffectTimeSeconds = 0f;
                },
                drawFrame: scene =>
                {
                    // Dim floor so the over-range highlights read as the bloom source, not the whole scene.
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f), new Color(0.16f, 0.18f, 0.22f, 1f));
                    // Hot over-range emissive sphere (5x red core).
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(-1.4f, 0.7f, 0.6f), Color.Black,
                        Material.Glowing(new Color(5f, 2.5f, 1f, 1f)));
                    // Over-range beam: the core rides 4x so its thin line blooms through the tonemap.
                    scene.DrawBeam(new Vector3(1.6f, 0.5f, -1.6f), new Vector3(1.6f, 0.5f, 1.8f), 0.28f,
                        new Color(1f, 0.5f, 0.2f, 1f),
                        BeamStyle.Default with { CoreColor = new Color(4f, 1f, 0.5f, 1f), Taper = 0.2f });
                    // A small deterministic modern-particle burst with over-range additive tint (fixed seeds).
                    Span<ParticleSprite> burst = stackalloc ParticleSprite[4];
                    for (int i = 0; i < burst.Length; i++)
                        burst[i] = new ParticleSprite
                        {
                            Position = new Vector3(-0.2f + 0.35f * i, 0.5f + 0.12f * i, 1.4f - 0.2f * i),
                            Size = 0.3f,
                            Color = new Color(3.2f, 2.4f, 0.8f, 1f),   // over-range additive glow -> feeds bloom
                            Shape = ParticleShape.Ember,
                            Seed = 0.13f * (i + 1),
                            Blend = BillboardBlend.Additive,
                        };
                    scene.DrawParticles(burst);
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_hdr_bloom", rgba, W, H);
        }

        // HDR + MSAA(4): the float16 multisampled resolve on a minimal scene (a lit cube + one emissive sphere), so the
        // MSAA edge variance stays inside the coarse grid tolerance. Baked by the coordinator (not in this stage).
        [GpuFact]
        public void Golden3D_HdrMsaa()
        {
            MeshHandle cube = default, sphere = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    cube = scene.LoadMesh(MeshPrimitives.Box(1.0f));
                    sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.6f));
                    scene.Post.Starfield = false;
                    scene.Post.BackgroundColor = new Color(0.03f, 0.04f, 0.06f, 1f);
                    scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
                    scene.Camera.Frame(new Vector3(0f, 0.3f, 0f), new Vector3(4.5f, 3.5f, 4.5f));
                    scene.EffectTimeSeconds = 0f;
                },
                drawFrame: scene =>
                {
                    scene.Draw(cube, Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(-1.1f, 0.5f, 0f),
                        new Color(0.35f, 0.6f, 0.85f, 1f));
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(1.2f, 0.6f, 0.2f), Color.Black,
                        Material.Glowing(new Color(3f, 1.6f, 0.6f, 1f)));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_hdr_msaa", rgba, W, H);
        }
    }
}
